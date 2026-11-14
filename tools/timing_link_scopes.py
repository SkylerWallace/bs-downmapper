"""Build verified cross-section links and separate local edit scopes for the timing editor."""

from bisect import bisect_left
from collections import defaultdict
from itertools import combinations
import math

from bookmark_files import bookmark_positions
from meter_repeats import local_downbeats, local_windows, repeat_windows
from timing_sections import extend_repeat_groups, find_timing_sections, matching_markers, musical_grid


BOUNDARY_EPSILON = 1e-6
MIN_SECTION = 8
MAX_SECTION = 128


def bookmark_windows(raw, to_grid, end, folder=None, difficulty=None):
    """Use marker positions only. Names can describe anything the mapper wants."""
    boundaries = sorted(boundary for beat in bookmark_positions(raw, folder, difficulty)
                        if 0 <= (boundary := to_grid(beat)) < end)
    distinct = []
    for boundary in boundaries:
        if not distinct or boundary - distinct[-1] > BOUNDARY_EPSILON:
            distinct.append(boundary)
    if len(distinct) < 2:
        return []
    return [
        (start, stop)
        for start, stop in zip(distinct, [*distinct[1:], end])
        if MIN_SECTION - BOUNDARY_EPSILON <= stop - start <= MAX_SECTION
    ]


def overlaps(first, second):
    return first[0] < second[1] - BOUNDARY_EPSILON and second[0] < first[1] - BOUNDARY_EPSILON


def compare_windows(beats, counts, first, second, *, local, allow_extra_beat=False):
    """Verify full-window rhythm agreement, returning only equal-count, mutually aligned hits."""
    left_span, right_span = first[1] - first[0], second[1] - second[0]
    difference = abs(left_span - right_span)
    if overlaps(first, second):
        return ()
    if allow_extra_beat and min(left_span, right_span) >= 6 and abs(difference - 1) <= .12:
        # An extended last measure belongs to its own occurrence. Align the
        # shared phrase only, so hits in the added beat stay independent.
        span = min(left_span, right_span)
        first, second = (first[0], first[0] + span), (second[0], second[0] + span)
    elif difference > max(.25, min(left_span, right_span) * .05):
        return ()
    a, b = (bisect_left(beats, edge - BOUNDARY_EPSILON) for edge in first)
    c, d = (bisect_left(beats, edge - BOUNDARY_EPSILON) for edge in second)
    size = max(b - a, d - c)
    minimum = 4 if local else 6
    if min(b - a, d - c) < minimum or min(b - a, d - c) < .84 * size:
        return ()
    combined = [*beats[a:b], *beats[c:d]]
    split = b - a
    pairs = [
        (a + i, c + j - split)
        for i, j in matching_markers(combined, [1] * len(combined), second[0] - first[0])
        if i < split <= j
    ]
    aligned = [(i, j) for i, j in pairs if counts[i] == counts[j]]
    if len(pairs) < .88 * size or len(aligned) < max(minimum, .80 * size):
        return ()
    # A bare pulse has many interchangeable phases. Require some rhythmic shape
    # among the shared hits, rather than letting a fill make the match distinctive.
    gaps = {
        round((beats[right] - beats[left]) * 8)
        for (left, _), (right, _) in zip(pairs, pairs[1:])
    }
    if len(gaps) < 2 and (local or len({counts[i] for i, _ in aligned}) < 2):
        return ()
    return aligned


def verified_groups(beats, counts, windows, *, local, candidates=None, allow_extra_beat=False):
    """Choose disjoint groups. Every occurrence must agree with the other group members."""
    windows = sorted(set(windows))
    matches = {}
    neighbors = defaultdict(set)
    comparisons = combinations(windows, 2) if candidates is None else sorted(set(candidates))
    for first, second in comparisons:
        pairs = compare_windows(beats, counts, first, second, local=local, allow_extra_beat=allow_extra_beat)
        if pairs:
            matches[first, second] = pairs
            neighbors[first].add(second)
            neighbors[second].add(first)

    occupied = []
    for first, second in sorted(matches, key=lambda pair: (-len(matches[pair]), pair)):
        if any(overlaps(window, used) for window in (first, second) for used in occupied):
            continue
        members = [first, second]
        for candidate in sorted(neighbors[first] & neighbors[second]):
            if all(candidate in neighbors[member] for member in members) and not any(
                overlaps(candidate, used) for used in occupied
            ):
                members.append(candidate)
        partners = defaultdict(list)
        for member in members[1:]:
            if first < member:
                pairs = matches[first, member]
            else:
                pairs = [(right, left) for left, right in matches[member, first]]
            for left, right in pairs:
                partners[left].append(right)
        occupied.extend(members)
        yield sorted(members), [[left, *peers] for left, peers in sorted(partners.items())]


def phrase_windows(section):
    """Propose common phrase lengths and equal subdivisions without fixing a song meter."""
    start, end = section
    duration = end - start
    lengths = {4, 6, 8, 12, 16, 24, 32}
    for divisor in (2, 3, 4):
        length = duration / divisor
        if 4 - BOUNDARY_EPSILON <= length <= 32 + BOUNDARY_EPSILON:
            lengths.add(round(length, 5))
    for length in lengths:
        for index in range(math.floor((duration + length * .045 + BOUNDARY_EPSILON) / length)):
            yield start + index * length, min(end, start + (index + 1) * length)


def editor_timing_links(
    level, timings, end_beat, *, use_bookmarks=True, long_repeats=False, include_local=True, meter_aware=True
):
    """Use bookmarks as boundary hints, then let rhythm-only detection fill uncovered ranges."""
    to_grid, from_grid = musical_grid(level)
    markers = [{"beat": to_grid(beat), "count": len(notes)} for beat, notes in timings.items()]
    beats = [marker["beat"] for marker in markers]
    counts = [marker["count"] for marker in markers]
    hints = bookmark_windows(level["raw"], to_grid, to_grid(end_beat), level.get("folder"), level.get("difficulty")) if use_bookmarks else []
    sections, links, reserved = [], [], []
    downbeats = local_downbeats(beats, counts) if meter_aware else {}

    def append_group(target, windows, group, scope=None):
        for start, end in windows:
            item = {"start": from_grid(start), "end": from_grid(end), "group": group}
            if scope is not None:
                item["scope"] = scope
            target.append(item)

    group_count = 0
    for windows, pairs in verified_groups(beats, counts, hints, local=False, allow_extra_beat=meter_aware):
        append_group(sections, windows, group_count)
        group_count += 1
        links.extend(pairs)
        reserved.extend(windows)
    fallback, fallback_links = find_timing_sections(
        markers, include_variations=True, reserved=reserved, long_repeats=long_repeats
    )
    previous_fallback = fallback
    if downbeats:
        fallback, fallback_links = extend_with_meter(beats, counts, downbeats, reserved, fallback, fallback_links)
    for item in fallback:
        append_group(sections, [(item["start"], item["end"])], group_count + item["group"])
    links.extend(fallback_links)
    result = {"sections": sections, "markerLinks": links}
    if not include_local:
        return result

    # Keep local groups separate. A marker can belong to both a cross-section and
    # a local group, but local scopes are disjoint and never connect to one another.
    scopes = list(hints)
    for item in [*previous_fallback, *fallback]:
        section = item["start"], item["end"]
        if not any(overlaps(section, scope) for scope in scopes):
            scopes.append(section)
    local_sections, local_links, local_scopes = [], [], []
    local_group = 0
    for section in sorted(scopes):
        groups = local_repeat_groups(beats, counts, section, downbeats)
        if not groups:
            continue
        scope = len(local_scopes)
        local_scopes.append({"start": from_grid(section[0]), "end": from_grid(section[1])})
        for windows, pairs in groups:
            append_group(local_sections, windows, local_group, scope)
            local_group += 1
            local_links.extend({"scope": scope, "markers": pair} for pair in pairs)

    return {
        **result,
        "subSections": local_sections,
        "subMarkerLinks": local_links,
        "subScopes": local_scopes,
    }


def local_repeat_groups(beats, counts, section, downbeats):
    windows = list(phrase_windows(section))
    previous = list(verified_groups(beats, counts, windows, local=True))
    proposed = list(local_windows(section, downbeats))
    if not proposed:
        return previous
    enriched = list(verified_groups(beats, counts, [*windows, *proposed], local=True, allow_extra_beat=True))

    def as_ranges(groups):
        return (
            [[{"start": start, "end": end} for start, end in windows] for windows, _ in groups],
            [links for _, links in groups],
        )

    combined = extend_repeat_groups(*as_ranges(previous), *as_ranges(enriched))
    return [([(item["start"], item["end"]) for item in group], links) for group, links in combined]


def extend_with_meter(beats, counts, downbeats, reserved, fallback, fallback_links):
    """A meter hypothesis can extend rhythm-only groups while retaining their verified links."""
    candidates = (
        (first, second) for first, second in repeat_windows(beats, downbeats)
        if not any(overlaps(used, first) or overlaps(used, second) for used in reserved)
    )
    meter_ranges, meter_links = [], []
    for windows, pairs in verified_groups(beats, counts, (), local=False, candidates=candidates, allow_extra_beat=True):
        meter_ranges.append([{"start": start, "end": end} for start, end in windows])
        meter_links.append(pairs)
    if not meter_ranges:
        return fallback, fallback_links

    previous_ranges, previous_links = defaultdict(list), defaultdict(list)
    marker_group = {}
    for section in fallback:
        group = section["group"]
        previous_ranges[group].append(section)
        first = bisect_left(beats, section["start"] - BOUNDARY_EPSILON)
        stop = bisect_left(beats, section["end"])
        marker_group.update((i, group) for i in range(first, stop))
    for link in fallback_links:
        previous_links[marker_group[link[0]]].append(link)
    combined = extend_repeat_groups(
        list(previous_ranges.values()), [previous_links[group] for group in previous_ranges],
        meter_ranges, meter_links,
    )
    return (
        [{**item, "group": group} for group, (windows, _) in enumerate(combined) for item in windows],
        [link for _, links in combined for link in links],
    )
