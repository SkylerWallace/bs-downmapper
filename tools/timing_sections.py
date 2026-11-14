"""Find near-repeated rhythm ranges for the timing editor."""

from bisect import bisect_left, bisect_right
from collections import Counter, defaultdict
import math


MIN_SPAN = 6
MIN_MATCHES = 8
MATCH_TOLERANCE = 0.05


def likely_offsets(beats):
    """Vote for translations using distinctive five-hit patterns."""
    votes = Counter()
    occurrences = defaultdict(list)
    gaps = [round((b - a) * 8) for a, b in zip(beats, beats[1:])]
    for i in range(len(gaps) - 3):
        signature = tuple(gaps[i : i + 4])
        if len(set(signature)) < 2 or max(signature) > 32:
            continue
        starts = occurrences[signature]
        for previous in starts[-48:]:
            offset = beats[i] - previous
            if offset >= MIN_SPAN:
                votes[round(offset * 24)] += 1
        starts.append(beats[i])
    return [offset / 24 for offset, count in votes.most_common(72) if count >= 3]


def short_anchor_offsets(beats):
    """Find translations even when local edits break the longer exact fingerprints."""
    occurrences = defaultdict(list)
    for i in range(len(beats) - 2):
        signature = short_anchor(beats, i)
        if signature:
            occurrences[signature].append(beats[i])

    votes = defaultdict(Counter)
    for signature, starts in occurrences.items():
        for i, beat in enumerate(starts):
            for previous in starts[max(0, i - 48) : i]:
                if beat - previous >= 12 - MATCH_TOLERANCE:
                    votes[round((beat - previous) * 24)][signature] += 1
    scored = [
        (sum(min(count, 8) for count in signatures.values()), len(signatures), offset)
        for offset, signatures in votes.items()
        if len(signatures) >= 2 and sum(signatures.values()) >= 4
    ]
    return [offset / 24 for _, _, offset in sorted(scored, reverse=True)[:72]]


def short_anchor(beats, first):
    gaps = tuple(round((beats[i + 1] - beats[i]) * 8) for i in (first, first + 1))
    return gaps if 0 < min(gaps) and min(gaps) != max(gaps) and max(gaps) <= 32 else ()


def has_separated_anchors(beats, matches, minimum_distance=3):
    """Avoid treating an otherwise uniform stream with one pickup as a repeated phrase."""
    first_anchor = None
    for (a, b), (c, d), (e, f) in zip(matches, matches[1:], matches[2:]):
        if (c, d, e, f) != (a + 1, b + 1, a + 2, b + 2):
            continue
        anchor = short_anchor(beats, a)
        if not anchor or anchor != short_anchor(beats, b):
            continue
        if first_anchor is None:
            first_anchor = beats[a]
        elif beats[a] - first_anchor >= minimum_distance:
            return True
    return False


def matching_markers(beats, counts, offset):
    """Return unique correspondences at translated offsets without pairing fills."""
    pairs = []
    right = 0
    for left, beat in enumerate(beats):
        target = beat + offset
        right = bisect_left(beats, target - MATCH_TOLERANCE, lo=right)
        if right == len(beats):
            break
        after = bisect_right(beats, target + MATCH_TOLERANCE, lo=right)
        eligible = [i for i in range(right, after) if counts[i] == counts[left]]
        if eligible:
            partner = min(eligible, key=lambda i: abs(beats[i] - target))
            expected = beats[partner] - offset
            start = bisect_left(beats, expected - MATCH_TOLERANCE)
            stop = bisect_right(beats, expected + MATCH_TOLERANCE)
            # A nearby fill must not claim a closer, already corresponding hit.
            if any(
                counts[i] == counts[left] and abs(beats[i] - expected) < abs(beat - expected)
                for i in range(start, stop)
            ):
                continue
            pairs.append((left, partner))
            right = partner + 1
    return pairs


def range_match(beats, counts, pairs, left_indices, start, end, offset, *, variations=False, long_span=0):
    first, last = (bisect_left(left_indices, edge) for edge in (start, end))
    matches = pairs[first:last]
    if len(matches) < MIN_MATCHES:
        return ()
    first_right, last_right = matches[0][1], matches[-1][1]
    first_left, last_left = matches[0][0], matches[-1][0]
    extent = beats[last_left] - beats[first_left]
    if extent < max(MIN_SPAN, .80 * long_span) - MATCH_TOLERANCE:
        return ()
    translated_start = beats[start] + offset
    translated_end = beats[end - 1] + offset
    right_count = (
        max(last_right + 1, bisect_right(beats, translated_end + MATCH_TOLERANCE))
        - min(first_right, bisect_left(beats, translated_start - MATCH_TOLERANCE))
    )
    total = end - start + right_count
    similarity, coverage = (0.84, 0.78) if variations else (0.88, 0.82)
    if long_span:
        similarity, coverage = (0.91, 0.87)
    if 2 * len(matches) / total < similarity or len(matches) / max(end - start, right_count) < coverage:
        return ()
    # A small fill can break a few matches. A long gap of unmatched material
    # should divide two ranges rather than join them.
    for (a, b), (c, d) in zip(matches, matches[1:]):
        if c - a > 6 or d - b > 6 or beats[c] - beats[a] > 6:
            return ()
    if variations:
        editable = sum(counts[left] == counts[right] for left, right in matches)
        required = .88 * max(end - start, right_count) if long_span else .70 * len(matches)
        distance = .50 * long_span if long_span else 3
        if editable < max(MIN_MATCHES, required) or not has_separated_anchors(beats, matches, distance):
            return ()
    return matches


def extend_repeat_groups(sections, links, long_sections, long_links):
    """Accept longer ranges only when they keep every relationship they overlap."""
    replaced = set()
    extensions = []
    for occurrences, correspondences in zip(long_sections, long_links):
        occupied = [
            i for i, group in enumerate(sections)
            if any(a["start"] < b["end"] - 1e-6 and b["start"] < a["end"] - 1e-6
                   for a in group for b in occurrences)
        ]
        peers = {marker: set(link) for link in correspondences for marker in link}
        if any(
            not all(
                any(b["start"] <= a["start"] + 1e-6 and a["end"] <= b["end"] + 1e-6 for b in occurrences)
                for a in sections[i]
            )
            or any(not peers.get(link[0], set()).issuperset(link) for link in links[i])
            for i in occupied
        ):
            continue
        old_pair_count = sum(len(link) * (len(link) - 1) for i in occupied for link in links[i])
        new_pair_count = sum(len(link) * (len(link) - 1) for link in correspondences)
        if new_pair_count <= old_pair_count:
            continue
        replaced.update(occupied)
        extensions.append((occurrences, correspondences))
    return [
        (group, links[i]) for i, group in enumerate(sections) if i not in replaced
    ] + extensions


def find_timing_sections(
    markers, *, to_grid=lambda beat: beat, from_grid=lambda beat: beat, include_variations=False,
    reserved=(), long_repeats=False,
):
    """Return disjoint repeated ranges and groups of corresponding marker indices."""
    beats = [to_grid(marker["beat"]) for marker in markers]
    counts = [marker["count"] for marker in markers]
    reserved = tuple(reserved)
    occupied, sections, links_by_group = list(reserved), [], []

    def free(start, end):
        return all(end <= a + 1e-6 or start >= b - 1e-6 for a, b in occupied)

    def discover(offsets, *, variations=False, long_form=False):
        comparisons = {}
        comparison_counts = [1] * len(counts) if variations else counts

        def comparison(offset):
            if offset not in comparisons:
                pairs = matching_markers(beats, comparison_counts, offset)
                comparisons[offset] = pairs, [left for left, _ in pairs]
            return comparisons[offset]

        candidates = []
        spans = (96, 80, 64, 56, 48, 40) if long_form else (32, 24, 16, 12)
        if not variations:
            spans = (*spans, 8)
        for offset in offsets:
            pairs, indices = comparison(offset)
            for left in indices:
                if variations and any(a <= beats[left] < b for a, b in occupied):
                    continue
                for span in spans:
                    if span > offset + MATCH_TOLERANCE:
                        continue
                    end = bisect_left(beats, beats[left] + span - MATCH_TOLERANCE)
                    long_span = span if long_form else 0
                    if long_form and (
                        not free(beats[left], beats[end - 1] + 1e-6)
                        or not free(beats[left] + offset, beats[end - 1] + offset + 1e-6)
                    ):
                        continue
                    matches = range_match(
                        beats, counts, pairs, indices, left, end, offset,
                        variations=variations, long_span=long_span,
                    )
                    if matches:
                        duration = beats[matches[-1][0]] - beats[left]
                        candidates.append((len(matches), duration, left, end, offset, long_span))
                        break

        for _, _, first, end, paired_offset, long_span in sorted(
            candidates, key=lambda row: (-row[0], -row[1], row[2])
        ):
            start = beats[first]
            # Keep the preview band beyond the final marker, without spilling over
            # into the following source hit or the beginning of the next repeat.
            stop = min(beats[end - 1] + 0.125, beats[end] if end < len(beats) else math.ceil(beats[-1] + 0.125))
            if not free(start, stop):
                continue
            partners = defaultdict(list)
            members = [(start, stop)]
            probes = [paired_offset, *(value for value in offsets if value != paired_offset)]
            probes.extend(-value for value in offsets)
            for offset in probes:
                if abs(offset) < stop - start - MATCH_TOLERANCE:
                    continue
                translated_start, translated_end = start + offset, stop + offset
                if not free(translated_start, translated_end) or any(
                    translated_end > a + 1e-6 and translated_start < b - 1e-6 for a, b in members
                ):
                    continue
                pairs, indices = comparison(offset)
                matches = range_match(
                    beats, counts, pairs, indices, first, end, offset, variations=variations, long_span=long_span
                )
                if not matches or matches[0][0] != first:
                    continue
                # Start on the actual first shared hit even if a humanized repeat
                # differs slightly from its voted translation.
                translated_start = beats[matches[0][1]]
                translated_end = max(translated_end, beats[matches[-1][1]] + 1e-6)
                if not free(translated_start, translated_end) or any(
                    translated_end > a + 1e-6 and translated_start < b - 1e-6 for a, b in members
                ):
                    continue
                members.append((translated_start, translated_end))
                for left, right in matches:
                    if counts[left] == counts[right]:
                        partners[left].append(right)
            if len(members) < 2:
                continue
            sections.append([
                {"start": a, "end": b}
                for a, b in sorted(members)
            ])
            occupied.extend(members)
            links_by_group.append([[left, *other] for left, other in partners.items()])
    offsets = likely_offsets(beats)
    variant_offsets = []
    if include_variations or long_repeats:
        variant_offsets = list(dict.fromkeys([*short_anchor_offsets(beats), *offsets]))
    discover(offsets)
    if include_variations:
        # Keep exact groups intact, then look for variants only in the remaining space.
        discover(variant_offsets, variations=True)
    groups = list(zip(sections, links_by_group))
    if long_repeats:
        short_sections, short_links = sections, links_by_group
        occupied, sections, links_by_group = list(reserved), [], []
        discover(
            [offset for offset in variant_offsets if offset >= 40 - MATCH_TOLERANCE],
            variations=True,
            long_form=True,
        )
        groups = extend_repeat_groups(short_sections, short_links, sections, links_by_group)
    return (
        [
            {"start": from_grid(item["start"]), "end": from_grid(item["end"]), "group": group_id}
            for group_id, (group, _) in enumerate(groups) for item in group
        ],
        [link for _, links in groups for link in links],
    )


def musical_grid(level):
    grid = level.get("legacyGridClock")
    if grid is None:
        return float, float
    clock = level["clock"]
    return (
        lambda beat: grid.beat_at_seconds(clock.seconds(beat)),
        lambda beat: clock.beat_at_seconds(grid.seconds(beat)),
    )


def chart_timing_sections(level, timings, *, include_variations=False):
    """Compare rhythms in the musical grid, including on legacy tempo maps."""
    markers = [{"beat": beat, "count": len(notes)} for beat, notes in timings.items()]
    to_grid, from_grid = musical_grid(level)
    return find_timing_sections(
        markers,
        to_grid=to_grid,
        from_grid=from_grid,
        include_variations=include_variations,
    )
