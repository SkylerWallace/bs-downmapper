"""Timing-only rhythm input preparation for the ChroMapper workflow."""

import math
from bisect import bisect_left
from collections import Counter, defaultdict
from itertools import combinations
from statistics import median

from analyze_standard_spreads import groups, nearest
from audit_placements import check_group
from beatmap_io import linked_anchors, linked_endpoints, linked_objects


EASY_RANK_WEIGHT = 2.0
EASY_BOOSTED_RANK_WEIGHT = 3.5
EASY_MOTIF_RANK_WEIGHT = 4.5


def repair_conflicting_links(level, choices=None, release_for_direction=(), visibility_choices=None):
    """Release fixed links implicated by simultaneous or sequential placement failures."""
    links = list(linked_objects(level["raw"]))
    attachments = defaultdict(set)
    for index, (_, _, endpoints) in enumerate(links):
        for note_id in linked_anchors(level["notes"], endpoints):
            attachments[note_id].add(index)
    removed = set()
    repairs = []
    choices = choices or {}
    for beat, group in groups(level["notes"]).items():
        if len(group) > 12:
            continue  # A Standard grid cannot place this many simultaneous heads.
        fixed = [n for n in group if attachments[n["id"]] - removed]
        conflicts = [
            (a["id"], b["id"])
            for a, b in combinations(fixed, 2)
            if any(f["rule"] != "G04" for f in check_group([a, b]))
        ]
        if not conflicts:
            continue
        implicated = {note_id for pair in conflicts for note_id in pair}
        options = [n for n in fixed if n["id"] in implicated]
        alternatives = {}
        for count in range(1, len(options) + 1):
            for release in combinations(options, count):
                dropped = set.union(*(attachments[n["id"]] for n in release)) - removed
                if not all(
                    attachments[a] <= dropped | removed or attachments[b] <= dropped | removed
                    for a, b in conflicts
                ):
                    continue
                # On a tie, keep the less demanding fixed positions for Easy.
                difficulty = sum(n["y"] + abs(n["x"] - (n["c"] + 1)) for n in release)
                score = len(dropped), -difficulty, sorted(dropped)
                key = frozenset(dropped)
                if key not in alternatives or score < alternatives[key]:
                    alternatives[key] = score
        choice = min(choices.get(beat, 0), len(alternatives) - 1)
        dropped = sorted(alternatives.values())[choice][2]
        removed.update(dropped)
        repairs.append(
            {
                "beat": beat,
                "choice": choice,
                "alternatives": len(alternatives),
                "conflictingNoteIds": [list(pair) for pair in conflicts],
                "affectedBeats": sorted({point[0] for i in dropped for point in links[i][2]}),
                "removedLinks": [{"type": links[i][0], "object": links[i][1]} for i in dropped],
            }
        )
    by_id = {n["id"]: n for n in level["notes"]}
    for note_id in sorted(release_for_direction):
        dropped = sorted(attachments[note_id] - removed)
        if not dropped:
            continue
        removed.update(dropped)
        repairs.append(
            {
                "beat": by_id[note_id]["b"],
                "releasedForDirection": note_id,
                "removedLinks": [{"type": links[i][0], "object": links[i][1]} for i in dropped],
            }
        )
    for target_id, (blocker_ids, choice) in (visibility_choices or {}).items():
        # Free either the target or all of its fixed occluders. Try the smaller
        # change first, preferring to move the occluder when costs are equal.
        blockers = set().union(*(attachments[i] for i in blocker_ids)) - removed
        target = attachments[target_id] - removed
        if not blockers or not target:
            continue
        alternatives = sorted(dict.fromkeys((frozenset(blockers), frozenset(target))), key=len)
        choice = min(choice, len(alternatives) - 1)
        dropped = sorted(alternatives[choice])
        removed.update(dropped)
        repairs.append(
            {
                "beat": by_id[target_id]["b"],
                "hand": by_id[target_id]["c"],
                "releasedForVisibility": target_id,
                "blockingNoteIds": list(blocker_ids),
                "blockingBeats": [by_id[i]["b"] for i in blocker_ids],
                "visibilityChoice": choice,
                "alternatives": len(alternatives),
                "removedLinks": [{"type": links[i][0], "object": links[i][1]} for i in dropped],
            }
        )
    if not removed:
        return level, repairs
    discarded = {id(links[i][1]) for i in removed}
    raw = {**level["raw"]}
    for key in {links[i][0] for i in removed}:
        raw[key] = [obj for obj in raw[key] if id(obj) not in discarded]
    endpoints = list(linked_endpoints(raw))
    return {
        **level,
        "raw": raw,
        "linkedEndpoints": endpoints,
        "anchorIds": linked_anchors(level["notes"], endpoints),
    }, repairs


def simplify_easy_links(level, minimum_gap, protected_ids):
    """Keep the clearer gesture when attached arcs or short chains stagger too tightly."""
    clock = level["clock"]
    candidates = []
    for key, obj, points in linked_objects(level["raw"]):
        if key == "arcs" and len(points) != 2:
            continue
        anchors = [linked_anchors(level["notes"], [point]) for point in points]
        if not all(len(ids) == 1 for ids in anchors) or any(
            ids.intersection(protected_ids) for ids in anchors
        ):
            continue
        head = points[0][0]
        tail = points[-1][0] if len(points) == 2 else obj["tb"]
        if len(points) == 1 and clock.gap(head, tail) > 0.18:
            continue
        # Whole beats rank ahead of offbeats. Prefer the stronger head, then tail.
        weakness = lambda beat: round(abs(beat - round(beat)), 4)
        phase = head < round(head) if len(points) == 1 else False
        candidates.append(((weakness(head), phase, weakness(tail), head), key, obj, points, tail))

    kept = []
    removed = set()
    for _, key, obj, points, tail in sorted(candidates, key=lambda candidate: candidate[0]):
        head, hand, _, _ = points[0]
        for other_key, other_points, other_tail in kept:
            if key != other_key:
                continue
            other_head, other_hand, _, _ = other_points[0]
            head_gap = abs(clock.gap(head, other_head))
            tail_gap = abs(clock.gap(tail, other_tail))
            repeated_chain = (
                len(points) == 1
                and hand == other_hand
                and round(head) == round(other_head)
                and 0.001 < head_gap < 1.25 * minimum_gap
            )
            staggered_arc = (
                len(points) == 2
                and hand != other_hand
                and 0.001 < head_gap < minimum_gap
                and tail_gap < minimum_gap
            )
            if repeated_chain or staggered_arc:
                removed.add(id(obj))
                break
        else:
            kept.append((key, points, tail))
    if not removed:
        return level
    keys = {key for _, key, obj, _, _ in candidates if id(obj) in removed}
    raw = {
        **level["raw"],
        **{key: [obj for obj in level["raw"][key] if id(obj) not in removed] for key in keys},
    }
    endpoints = list(linked_endpoints(raw))
    return {
        **level,
        "raw": raw,
        "linkedEndpoints": endpoints,
        "anchorIds": linked_anchors(level["notes"], endpoints),
        "simplifiedLinkCount": len(removed),
    }


def timing_link_context(level, protected, *, maximum_reach=None, anchor_allowed=None):
    """Give linked attacks a soft preference and optionally limit the reach of the gesture."""
    raw = level["raw"]
    links = list(linked_objects(raw))
    if not links:
        return level
    # Index once by attachment cell. A source note can be part of several links.
    by_cell = defaultdict(list)
    for n in level["notes"]:
        by_cell[n["c"], n["x"], n["y"]].append(n)
    kept = defaultdict(list)
    locked = set()
    accents = set()
    attachments = []
    for key, obj, points in links:
        notes = {
            n["id"]: n
            for beat, hand, x, y in points
            for n in by_cell[hand, x, y]
            if abs(n["b"] - beat) < 0.0001
        }
        accents.update(round(n["b"], 5) for n in notes.values())
        cue_link = bool(protected.intersection(notes))
        if anchor_allowed and not cue_link and not all(map(anchor_allowed, notes.values())):
            continue
        if maximum_reach is None or cue_link:
            kept[key].append(obj)
            attachments.append((key, obj, notes))
            if cue_link:
                locked.update(notes)
            continue
        if key == "arcs":
            # The v4 metadata also describes ends that cannot attach to a note.
            table = raw["colorNotesData"]
            head, tail = table[obj.get("hi", 0)], table[obj.get("ti", 0)]
            x, y = head.get("x", 0), head.get("y", 0)
            tx, ty = tail.get("x", 0), tail.get("y", 0)
        elif key == "chains":
            _, _, x, y = points[0]
            tail = raw["chainsData"][obj.get("ci", 0)]
            tx, ty = tail.get("tx", 0), tail.get("ty", 0)
        elif key == "burstSliders":
            _, _, x, y = points[0]
            tx, ty = obj["tx"], obj["ty"]
        else:
            _, _, x, y = points[0]
            _, _, tx, ty = points[-1]
        # Upper endpoints demand additional reach on beginner tiers.
        span = math.hypot(tx - x, ty - y) + 0.6 * (max(y, ty) > 1)
        if span <= maximum_reach:
            kept[key].append(obj)
            attachments.append((key, obj, notes))
    prepared = {**raw, **{key: kept[key] for key, _, _ in links}}
    return {
        **level,
        "raw": prepared,
        "linkedEndpoints": list(linked_endpoints(prepared)),
        "anchorIds": locked,
        "linkAccentBeats": accents,
        "optionalLinkAttachments": attachments,
        "simplifiedLinkCount": level.get("simplifiedLinkCount", 0) + len(links) - len(attachments),
    }


def retain_timing_links(level, notes):
    """Drop a link when pruning or merging removes one of its attached attacks."""
    if "optionalLinkAttachments" not in level:
        return level
    selected = {n["id"]: n["b"] for n in notes}
    attachments = level["optionalLinkAttachments"]
    kept = defaultdict(list)
    dropped = 0
    for key, obj, anchors in attachments:
        if all(selected.get(i) == n["b"] for i, n in anchors.items()):
            kept[key].append(obj)
        else:
            dropped += 1
    raw = {**level["raw"], **{key: kept[key] for key, _, _ in attachments}}
    endpoints = list(linked_endpoints(raw))
    return {
        **level,
        "raw": raw,
        "linkedEndpoints": endpoints,
        "anchorIds": linked_anchors(notes, endpoints),
        "simplifiedLinkCount": level["simplifiedLinkCount"] + dropped,
    }


def merge_nearby_singles(level, retained, sections, streams, protected_ids):
    """Fold a short, isolated offbeat into its adjacent stronger grid accent."""
    kept = {n["id"] for n in retained}
    protected = set(protected_ids) | level["anchorIds"] | {i for s in streams for i in s["noteIds"]}
    original = list(groups(level["notes"]).values())
    merges = []
    section_index = 0
    for i, (first, second) in enumerate(zip(original, original[1:])):
        if len(first) != 1 or len(second) != 1:
            continue
        a, b = first[0], second[0]
        if a["c"] == b["c"] or any(n["id"] not in kept or n["id"] in protected for n in (a, b)):
            continue
        if abs(b["b"] - a["b"] - 0.25) > 0.0001 or level["clock"].gap(a["b"], b["b"]) > 0.14:
            continue
        # Require a single stronger half-beat anchor and room on both sides of the pair.
        if abs(a["b"] * 4 - round(a["b"] * 4)) > 0.0004:
            continue
        if (i and a["b"] - original[i - 1][0]["b"] < 0.5 - 0.0001) or (
            i + 2 < len(original) and original[i + 2][0]["b"] - b["b"] < 0.5 - 0.0001
        ):
            continue
        while section_index + 1 < len(sections) and a["b"] >= sections[section_index]["endBeatExclusive"]:
            section_index += 1
        if b["b"] >= sections[section_index]["endBeatExclusive"]:
            continue
        if any(a["b"] - 0.5 <= n["b"] <= b["b"] + 0.5 for n in level["bombs"]):
            continue
        if any(w["b"] <= b["b"] and w["b"] + w["d"] >= a["b"] for w in level["walls"]):
            continue
        if any(a["b"] - 0.5 <= beat <= b["b"] + 0.5 for beat, *_ in level["linkedEndpoints"]):
            continue
        left, right = (a, b) if a["c"] == 0 else (b, a)
        if left["x"] >= 2 or right["x"] < 2 or check_group([a, b]):
            continue
        target, moved = (a, b) if round(a["b"] * 4) % 2 == 0 else (b, a)
        merges.append({"noteId": moved["id"], "targetId": target["id"]})
    return merges


def apply_merges(level, notes, merges):
    if not merges:
        return notes
    by_id = {n["id"]: n for n in notes}
    original_singles = {g[0]["id"] for g in groups(level["notes"]).values() if len(g) == 1}
    used = set()
    moved = {}
    for merge in merges:
        pair = (merge.get("noteId"), merge.get("targetId"))
        if any(
            type(i) is not int
            or i not in by_id
            or i not in original_singles
            or i in used
            or i in level["anchorIds"]
            for i in pair
        ):
            raise ValueError("A timing merge needs two available, unlinked original singles.")
        source, target = (by_id[i] for i in pair)
        if (
            source["c"] == target["c"]
            or abs(abs(source["b"] - target["b"]) - 0.25) > 0.0001
            or (abs(target["b"] * 2 - round(target["b"] * 2)) > 0.0002)
        ):
            raise ValueError("A timing merge needs opposite hands and a nearby half-beat destination.")
        used.update(pair)
        moved[pair[0]] = target["b"]
    return sorted(
        (dict(n, b=moved[n["id"]]) if n["id"] in moved else n for n in notes), key=lambda n: (n["b"], n["c"])
    )


def refine_easy_pulse(level, retained, merges, protected, minimum_gap, pulse):
    """Refine Easy attacks on the local tempo grid while keeping the same note budget."""
    timings = list(groups(level["notes"]).items())
    raw = [b for b, _ in timings]
    clock = level["clock"]
    grid = level.get("legacyGridClock") or clock
    times = [clock.seconds(b) for b in raw]
    beats = [grid.beat_at_seconds(t) for t in times] if grid is not clock else raw
    beat_seconds = [grid.gap(b, b + 1) for b in beats]
    rank = [level["rankScores"][b] for b in raw]
    mix = [level["onsetScores"][b] for b in raw]
    by_id = {n["id"]: i for i, (_, group) in enumerate(timings) for n in group}
    # Use the played order for spacing, then restore the original merge inputs
    # so the timing editor can still display and undo each merge.
    locked = {by_id[n] for n in protected | level["anchorIds"] if n in by_id}
    locked.update(by_id[merge[key]] for merge in merges for key in ("noteId", "targetId"))
    windows, accents = easy_sparse_cues(level, timings)
    locked.update(i for i, b in enumerate(raw) if b in accents)
    suppressed = {
        i
        for i, b in enumerate(raw)
        if b not in accents and any(start - 0.0001 <= b <= end + 0.0001 for start, end in windows)
    }
    destinations = {m["noteId"]: m["targetId"] for m in merges}
    kept = defaultdict(list)
    for note in retained:
        index = by_id[destinations.get(note["id"], note["id"])]
        kept[index].append(note)
    near_beat = [abs(b - round(b)) <= 0.065 for b in beats]

    def place(index, note):
        group = timings[index][1]
        kept[index] = [min(group, key=lambda n: (n["c"] != note["c"], n["y"], abs(n["x"] - (n["c"] + 1))))]

    def replace_singles(slow):
        for i in sorted(kept)[1:-1]:
            if i in locked or len(kept[i]) != 1:
                continue
            if slow:
                if beat_seconds[i] < 0.90 * pulse or mix[i] > 0.45:
                    continue
                radius = 0.51
            else:
                if near_beat[i] or not minimum_gap + 0.01 <= beat_seconds[i] <= 0.88 * pulse:
                    continue
                radius = 0.60
            ordered = sorted(kept)
            pos = bisect_left(ordered, i)
            before, after = (times[j] for j in (ordered[pos - 1], ordered[pos + 1]))
            spacing = min(minimum_gap, times[i] - before, after - times[i])
            first = bisect_left(beats, beats[i] - radius, ordered[pos - 1] + 1, i + 1)
            choices = []
            for j in range(first, ordered[pos + 1]):
                if beats[j] - beats[i] > radius:
                    break
                if j in kept or j in locked or j in suppressed:
                    continue
                if min(times[j] - before, after - times[j]) < spacing - 1e-8:
                    continue
                if slow:
                    # At slow tempos, a clear subdivision can be more readable
                    # than a quiet note on a beat. Require strong audio contrast.
                    phase_error = min(abs(beats[j] * s - round(beats[j] * s)) for s in (2, 3))
                    if phase_error > 0.015 or mix[j] < 1.4 * mix[i]:
                        continue
                elif not near_beat[j]:
                    continue
                choices.append(j)
            if not choices:
                continue

            def quality(j):
                left, right = times[j] - before, after - times[j]
                if slow:
                    return 0.35 * rank[j] + mix[j] + 0.25 * min(left, right) / (left + right)
                phase = 0.75 if near_beat[j] else 0.1125 if abs(beats[j] - round(beats[j])) > 0.435 else 0
                return rank[j] + 0.25 * mix[j] + phase - 0.4 * ((left - pulse) ** 2 + (right - pulse) ** 2)

            j = max(choices, key=quality)
            if quality(j) - quality(i) > (0.30 if slow else 0.025):
                place(j, kept.pop(i)[0])

    replace_singles(slow=False)
    # Give a small share of second swings from doubles to recurring triplet
    # subdivisions, provided there is room for a separate Easy hit in seconds.
    blocks = defaultdict(list)
    for i, b in enumerate(beats):
        blocks[math.floor((b + 0.001) / 8)].append(i)
    for indices in blocks.values():
        subdivisions = [i for i in indices if abs(beats[i] - round(beats[i])) > 0.10]
        if len(subdivisions) < 4 or median(beat_seconds[i] for i in indices) < 0.84 * pulse:
            continue
        thirds = {i for i in indices if abs(beats[i] * 3 - round(beats[i] * 3)) < 0.015}
        if len(thirds.intersection(subdivisions)) < 0.30 * len(subdivisions):
            continue
        for _ in range(2):
            order = sorted(kept)
            choices = []
            for i in sorted(thirds - kept.keys() - suppressed - locked):
                pos = bisect_left(order, i)
                if not 0 < pos < len(order):
                    continue
                left, right = times[i] - times[order[pos - 1]], times[order[pos]] - times[i]
                if min(left, right) < minimum_gap - 1e-8:
                    continue
                choices.append((rank[i] + 0.65 * mix[i] + 0.30 * min(left, right) / (left + right), i))
            if not choices:
                break
            value, i = max(choices)
            donors = [
                j for j in kept if len(kept[j]) == 2 and j not in locked and abs(times[i] - times[j]) < 60
            ]
            if not donors:
                break
            donor = min(donors, key=lambda j: rank[j] + 0.20 * mix[j])
            if value < 0.27 + 0.30 * rank[donor]:
                break
            first, second = kept[donor]
            kept[donor] = [first]
            place(i, second)
    replace_singles(slow=True)
    return sorted((n for group in kept.values() for n in group), key=lambda n: (n["b"], n["c"]))


def timeline(level):
    clock = level["clock"]
    audio = level["audioData"]
    end = max(n["b"] for n in level["notes"])
    if audio:
        end = max(end, audio["bpmData"][-1]["eb"])
    return {
        "endBeat": max(1, math.ceil(end)),
        "regions": [
            {"beat": b, "seconds": t, "secondsPerBeat": rate}
            for b, t, rate in zip(clock.starts, clock.times, clock.rates)
        ],
        "timings": [
            {
                "beat": g[0]["b"],
                "count": len(g),
                "locked": any(n["id"] in level["anchorIds"] for n in g),
                "hands": [n["c"] for n in g],
                "noteIds": [n["id"] for n in g],
            }
            for g in groups(level["notes"]).values()
        ],
    }


def edited_notes(level, edits):
    grouped = groups(level["notes"])
    beats = sorted(grouped)
    end = timeline(level)["endBeat"]

    def beat_value(value):
        if (
            isinstance(value, bool)
            or not isinstance(value, (float, int))
            or not math.isfinite(value)
            or not 0 <= value <= end
        ):
            raise ValueError("Timing edits must be on the song timeline")
        return float(value)

    removed = set()
    for value in edits.get("removed", []):
        beat = nearest(beat_value(value), beats)
        if beat is None:
            raise ValueError(
                "A removed timing no longer exists in the saved pattern chart. Reopen the timing editor."
            )
        if any(n["id"] in level["anchorIds"] for n in grouped[beat]):
            raise ValueError("A timing attached to an arc or chain cannot be deleted")
        removed.update(n["id"] for n in grouped[beat])
    ids = {n["id"] for n in level["notes"]}
    for note_id in edits.get("removedNoteIds", []):
        if type(note_id) is not int or note_id not in ids:
            raise ValueError(
                "A removed note no longer exists in the saved pattern chart. Reopen the timing editor."
            )
        if note_id in level["anchorIds"]:
            raise ValueError("A note attached to an arc or chain cannot be deleted")
        removed.add(note_id)
    notes = apply_merges(
        level, [n for n in level["notes"] if n["id"] not in removed], edits.get("merges", [])
    )
    used = sorted(groups(notes))
    for item in sorted(edits.get("added", []), key=lambda item: item["beat"]):
        beat = beat_value(item["beat"])
        if nearest(beat, used) is not None or nearest(beat, beats) is not None:
            raise ValueError("An added timing overlaps an existing timing. Restore that marker instead.")
        hand = item.get("hand", -1)
        if hand not in (-1, 0, 1):
            raise ValueError("Added timing hand must be Auto, Left or Right")
        if hand == -1:
            previous = max((n for n in notes if n["b"] < beat), key=lambda n: n["b"], default=None)
            hand = 1 - previous["c"] if previous else 1
        templates = [n for n in level["notes"] if n["c"] == hand] or level["notes"]
        template = min(templates, key=lambda n: abs(n["b"] - beat))
        reserved = {
            (x, y) for b, c, x, y in level["linkedEndpoints"] if abs(b - beat) <= 0.0001 and c == hand
        }
        cell = next(
            (
                (x, y)
                for x, y in [(template["x"], template["y"]), *((x, y) for x in range(4) for y in range(3))]
                if (x, y) not in reserved
            ),
            None,
        )
        if cell is None:
            raise ValueError("The added timing is occupied by linked endpoints")
        notes.append({**template, "b": beat, "c": hand, "x": cell[0], "y": cell[1]})
        used.append(beat)
        used.sort()
    if not notes:
        raise ValueError("Keep at least one timing in the song")
    return sorted(notes, key=lambda n: (n["b"], n["c"]))


def easy_motif_indices(timings):
    """Keep the pulse of a repeated figure and its hand-off or offbeat accent."""
    for i, (beat, group) in enumerate(timings):
        if len(group) == 1 and i >= 7:
            run = timings[i - 7 : i]
            hand = run[0][1][0]["c"]
            start = run[0][0]
            if (
                group[0]["c"] != hand
                and all(
                    len(notes) == 1 and notes[0]["c"] == hand and abs(b - start - j * 0.5) < 0.0001
                    for j, (b, notes) in enumerate(run)
                )
                and abs(beat - start - 3.5) < 0.0001
            ):
                yield from (i - 7, i - 5, i - 3, i)
        if len(group) == 2 and len({n["c"] for n in group}) == 2 and abs(beat % 1 - 0.5) < 0.0001:
            j = i + 1
            while j < len(timings):
                b, notes = timings[j]
                if abs(b - beat - (j - i)) > 0.0001 or len(notes) != 2 or notes[0]["c"] == notes[1]["c"]:
                    break
                j += 1
            if j - i >= 3:
                yield from range(i, j)


def easy_sparse_cues(level, timings):
    """Leave space for sustained wall sequences and repeated bomb fills."""
    clock = level["clock"]
    long_walls = sorted(
        (w["b"], w["b"] + w["d"])
        for w in level.get("walls", [])
        if w["d"] >= 3 and clock.gap(w["b"], w["b"] + w["d"]) >= 1.5
    )
    runs = []
    for start, end in long_walls:
        if not runs or start > runs[-1][-1][1] + 2:
            runs.append([])
        if not runs[-1] or start > runs[-1][-1][0] + 0.0001:
            runs[-1].append((start, end))
    windows = []
    accents = set()
    for run in runs:
        if len(run) < 3:
            continue
        entries = [
            next((beat for beat, _ in timings if start - 0.0001 <= beat <= start + 2 + 0.0001), None)
            for start, _ in run
        ]
        if all(beat is not None for beat in entries):
            accents.update(entries)
            windows.append((run[0][0], run[-1][1] + 2))

    clusters = []
    for beat in sorted({round(b["b"], 4) for b in level.get("bombs", [])}):
        if not clusters or beat - clusters[-1][-1] > 0.26:
            clusters.append([])
        clusters[-1].append(beat)
    fills = [
        (math.floor(c[0]), c[-1], tuple(round(b - math.floor(c[0]), 3) for b in c))
        for c in clusters
        if len(c) >= 3 and c[0] % 1 <= 0.5 and c[-1] - c[0] <= 0.75
    ]
    counts = Counter(pattern for _, _, pattern in fills)
    beats = [beat for beat, _ in timings]
    for start, end, pattern in fills:
        if counts[pattern] < 2:
            continue
        before, after = nearest(start - 2, beats), nearest(start + 2, beats)
        if before is not None and after is not None:
            accents.update((before, after))
            windows.append((start - 1, end + 0.5))
    return windows, accents


def easy_phrase_cue_weights(timings, starts):
    """Find recurring accents and sustained hand changes on the source beat grid."""
    beats = [b for b, _ in timings]
    hands = [{n["c"] for n in group} for _, group in timings]
    cues = {}
    for start, stop in zip(starts, starts[1:] + [len(timings)]):
        run = 0
        for i in range(start, stop):
            before = beats[i] - beats[i - 1] if i > start else 2
            after = beats[i + 1] - beats[i] if i + 1 < stop else 2
            roles = set()
            if min(before, after) >= 0.25 and max(before, after) >= 0.75 and abs(before - after) >= 0.25:
                roles.add("spaced_accent")
            if len(hands[i]) == 2 and (
                (i > start and len(hands[i - 1]) == 1) or (i + 1 < stop and len(hands[i + 1]) == 1)
            ):
                roles.add("double_accent")
            if i > start and before <= 1 and len(hands[i]) == len(hands[i - 1]) == 1:
                if hands[i] != hands[i - 1] and run >= 3:
                    roles.add("handoff")
                run = run + 1 if hands[i] == hands[i - 1] else 1
            else:
                run = int(len(hands[i]) == 1)
            if roles:
                cues[i] = (roles, min(before, 2), min(after, 2))
    weights = {}
    for i, (roles, before, after) in cues.items():
        support = 0
        for period in (2, 4, 8, 16):
            for sign in (-1, 1):
                target = beats[i] + sign * period
                index = bisect_left(beats, target - 0.025)
                while index < len(beats) and beats[index] <= target + 0.025:
                    match = cues.get(index)
                    if (
                        match
                        and roles & match[0]
                        and abs(before - match[1]) <= 0.125
                        and abs(after - match[2]) <= 0.125
                    ):
                        support += 1
                        break
                    index += 1
        if support or "handoff" in roles:
            weights[i] = min(1, 0.28 * support + (0.35 if "handoff" in roles else 0.18))
    return weights


def easy_swap_costs(beats, starts, cue_weights):
    """Score attacks using the source grid and recurring local rhythm."""
    costs = {}
    for first, end in zip(starts, starts[1:] + [len(beats)]):
        shapes = {
            i: (min(beats[i] - beats[i - 1], 2), min(beats[i + 1] - beats[i], 2))
            for i in range(first + 1, end - 1)
        }
        for i, (before, after) in shapes.items():
            beat = beats[i]
            whole = abs(beat - round(beat)) < 0.0001
            half = abs(beat * 2 - round(beat * 2)) < 0.0002
            quarter = abs(beat * 4 - round(beat * 4)) < 0.0004
            cost = 0 if whole else 0.55 if half else 0.85 if quarter else 1.2
            if whole and round(beat) % 2 == 0:
                cost -= 0.28
            # A repeated attack after a short lead-in, or a pickup into the
            # next attack, can outweigh the grid preference on its own.
            if abs(before - after) >= 0.24:
                support = 0
                for offset in (-16, -8, -4, 4, 8, 16):
                    target = nearest(beat + offset, beats)
                    if target is None:
                        continue
                    shape = shapes.get(bisect_left(beats, target))
                    if shape and abs(before - shape[0]) <= 0.125 and abs(after - shape[1]) <= 0.125:
                        support += 1
                cost -= min(0.95, 0.24 * support + 0.3 * min(1, max(before, after) - min(before, after)))
            costs[i] = cost - 0.45 * cue_weights.get(i, 0)
    return costs


def easy_pickup_pairs(beats, starts):
    """Find a grid attack and offbeat response with a repeated source rhythm."""
    pairs = set()
    for first, end in zip(starts, starts[1:] + [len(beats)]):
        for i in range(first, end - 2):
            start = beats[i]
            if abs(start / 2 - round(start / 2)) > 0.00005:
                continue
            for j in range(i + 2, min(i + 4, end)):
                duration = beats[j] - start
                if abs(duration - 1.5) > 0.0001:
                    continue
                gaps = [b - a for a, b in zip(beats[i:j], beats[i + 1 : j + 1])]
                if max(gaps) - min(gaps) < 0.24:
                    continue
                for offset in (-8, -4, 4, 8):
                    k = bisect_left(beats, start + offset - 0.0001, first, end)
                    if k + j - i < end and all(
                        abs(beats[k + d] - beats[i + d] - offset) <= 0.0001 for d in range(j - i + 1)
                    ):
                        pairs.add((i, j))
                        break
    return pairs


def easy_pulse_rank(beat):
    """Recognize the pulse even when a source onset sits slightly off the grid."""
    half_steps = round(beat * 2)
    if abs(beat - half_steps / 2) > 1 / 16:
        return 3
    if half_steps % 2:
        return 2
    return int(half_steps % 4 != 0)


def easy_accent_grid(timings, beats, times, starts, pulse):
    """Find repeated swing and irregular-length riffs on the local beat grid."""
    # This resolution represents both triplet subdivisions and sixteenth-beat offsets.
    slots = [round(b * 48) for b in beats]
    chords = [len({n["c"] for n in group}) == 2 for _, group in timings]
    weights = {}
    for first, end in zip(starts, starts[1:] + [len(beats)]):
        for i in range(first + 1, end - 1):
            left = bisect_left(beats, beats[i] - 16, first, i)
            right = bisect_left(beats, beats[i] + 16, i, end)
            if right - left < 3 or beats[right - 1] - beats[left] < 12:
                continue
            rate = (times[right - 1] - times[left]) / (beats[right - 1] - beats[left])
            nearby = range(left, right)
            if 0.75 * pulse <= 2 * rate <= 1.25 * pulse:
                # A swung chord is often the response to the preceding pulse.
                # Both positions should survive even if an even grid hit sits between them.
                swing = Counter(
                    slots[j] % 192
                    for j in nearby
                    if slots[j] % 12 and j + 1 < end and beats[j + 1] - beats[j] >= (0.75 if chords[j] else 1)
                )
                if swing:
                    phase, support = swing.most_common(1)[0]
                    if support >= 4 and support >= 0.7 * sum(swing.values()):
                        slot = slots[i] % 192
                        companion = round(phase / 48 - 2) * 48 % 192
                        accented = chords[i] or beats[i + 1] - beats[i] >= 1
                        if slot == companion or (slot == phase and accented):
                            weights[i] = 2.5
                        elif slot == (companion + 96) % 192:
                            weights[i] = 1
                        continue
            if 0.8 * pulse <= 3 * rate <= 1.45 * pulse:
                phases = Counter(slots[j] // 48 % 3 for j in nearby if chords[j] and slots[j] % 48 == 0)
                if phases:
                    phase, support = phases.most_common(1)[0]
                    if support >= 4 and support >= 0.67 * sum(phases.values()):
                        if slots[i] % 144 == 48 * phase:
                            weights[i] = 2
                        continue
            if 1.9 * pulse <= 5 * rate <= 2.6 * pulse:
                # A recurring stack marks the start of a longer riff. Keep the
                # attack and leave room for one response midway to the next one.
                stacks = Counter(slots[j] % 240 for j in nearby if chords[j] and len(timings[j][1]) >= 3)
                if stacks:
                    phase, support = stacks.most_common(1)[0]
                    if support >= 3 and support >= 0.7 * sum(stacks.values()):
                        slot = slots[i] % 240
                        if slot == phase:
                            weights[i] = 2.5
                        elif slot == (phase + 144) % 240:
                            weights[i] = 1.5
    return weights


def redistribute_easy_phrase_budget(timings, times, starts, kept, anchors, costs, minimum_gap, pulse):
    """Trade low-value notes in slower phrases for playable attacks in denser phrases."""
    phrases = []
    for first, end in zip(starts, starts[1:] + [len(times)]):
        duration = times[end - 1] - times[first]
        if duration < 4 * pulse:
            continue
        indices = [i for i in range(first, end) if i in kept]
        source_gap = median(times[i + 1] - times[i] for i in range(first, end - 1))
        count = sum(len(kept[i]) for i in indices)
        phrases.append((source_gap, count / (duration + pulse), indices, count))
    if len(phrases) < 2:
        return

    added = Counter()
    removed = Counter()
    while True:
        donors = {}
        receivers = {}
        for phrase, (gap, _, indices, count) in enumerate(phrases):
            if not added[phrase] and removed[phrase] < 0.25 * count:
                donations = []
                for left, i, right in zip(indices, indices[1:], indices[2:]):
                    if i in anchors:
                        continue
                    if len(kept[i]) == 2:
                        donations.append((0.25 + max(0, -costs[i]), i))
                    elif costs[i] >= 0.25 and times[right] - times[left] <= 2.5 * pulse:
                        donations.append((1 - costs[i], i))
                if donations:
                    donors[phrase] = min(donations)
            if gap <= 0.25 * pulse and not removed[phrase] and added[phrase] < 0.35 * count:
                insertions = []
                for left, right in zip(indices, indices[1:]):
                    if times[right] - times[left] < 2 * minimum_gap:
                        continue
                    for i in range(left + 1, right):
                        before, after = times[i] - times[left], times[right] - times[i]
                        if min(before, after) >= minimum_gap:
                            insertions.append((costs[i] - 2 * min(before, after) / pulse, i, left))
                if insertions:
                    receivers[phrase] = min(insertions)
        transfers = []
        for receive, (add_cost, insert, left) in receivers.items():
            gap, rate, _, _ = phrases[receive]
            for donate, (drop_cost, drop) in donors.items():
                donor_gap, donor_rate, _, _ = phrases[donate]
                if donor_gap >= 1.6 * gap and donor_rate >= rate:
                    transfers.append((gap, add_cost + drop_cost, receive, donate, insert, drop, left))
        if not transfers:
            break
        _, _, receive, donate, insert, drop, left = min(transfers)
        hand = kept[left][-1]["c"]
        kept[insert] = [
            min(
                timings[insert][1],
                key=lambda n: (
                    n["c"] == hand,
                    n["y"],
                    abs(n["x"] - (n["c"] + 1)),
                ),
            )
        ]
        receiving_indices = phrases[receive][2]
        receiving_indices.insert(bisect_left(receiving_indices, insert), insert)
        if len(kept[drop]) == 2:
            kept[drop] = [min(kept[drop], key=lambda n: (n["y"], abs(n["x"] - (n["c"] + 1))))]
        else:
            del kept[drop]
            phrases[donate][2].remove(drop)
        added[receive] += 1
        removed[donate] += 1


def ranked_budget_phrases(beats, starts):
    """Keep source rests and split long runs at 32-beat grid boundaries."""
    width = 32
    for start, end in zip(starts, starts[1:] + [len(beats)]):
        if end - start < 3:
            continue
        edges = [start]
        bar = (math.floor((beats[start] + 0.002) / width) + 1) * width
        while bar <= beats[end - 1] - 0.30 * width:
            split = bisect_left(beats, bar - 0.002, edges[-1] + 1, end)
            if beats[split] - beats[edges[-1]] >= 0.45 * width:
                edges.append(split)
            bar += width
        yield from zip(edges, edges[1:] + [end])


def allocate_ranked_phrase_budget(
    timings, beats, times, starts, kept, protected, rank_scores, minimum_gap, pulse
):
    """Move a second note from a double to a ranked attack in another phrase."""
    phrases = list(ranked_budget_phrases(beats, starts))
    probabilities = [rank_scores[beat] for beat, _ in timings]
    budgets = [sum(len(kept.get(i, ())) for i in range(first, end)) for first, end in phrases]
    demand = []
    for first, end in phrases:
        # Nearby onsets can compete for one playable slot. Count the strongest
        # evidence in each slot instead of summing every candidate in a burst.
        slots = {}
        for i in range(first, end):
            slot = round((times[i] - times[first]) / (0.7 * pulse))
            slots[slot] = max(slots.get(slot, 0), probabilities[i])
        demand.append(sum(slots.values()))
    total_demand = sum(demand)
    if total_demand <= 1e-8:
        return
    total_budget = sum(budgets)
    targets = [value * total_budget / total_demand for value in demand]
    initial_budgets = budgets[:]
    caps = [max(1, math.ceil(0.2 * count)) for count in budgets]

    for _ in range(math.ceil(0.04 * total_budget)):
        donors, receivers = [], []
        for phrase, (first, end) in enumerate(phrases):
            local = [i for i in range(first, end) if i in kept]
            excess = budgets[phrase] - targets[phrase]
            if excess >= 0.5 and initial_budgets[phrase] - budgets[phrase] < caps[phrase]:
                donors.extend(
                    (0.35 * probabilities[i] - 0.04 * min(3, excess), phrase, i)
                    for i in local[1:-1]
                    if i not in protected and len(kept[i]) == 2
                )
            if excess <= -0.5 and budgets[phrase] - initial_budgets[phrase] < caps[phrase]:
                for left, right in zip(local, local[1:]):
                    for i in range(left + 1, right):
                        before, after = times[i] - times[left], times[right] - times[i]
                        if i in protected or min(before, after) < minimum_gap or probabilities[i] < 0.32:
                            continue
                        # Strong phrase demand alone should not turn an established
                        # pulse into a faster run. Compare tight hits with their neighbors.
                        neighbor_confidence = (probabilities[left] + probabilities[right]) / 2
                        if min(before, after) < 0.5 * pulse and probabilities[i] < 0.75 * neighbor_confidence:
                            continue
                        spacing = (
                            (before / pulse - 1) ** 2
                            + (after / pulse - 1) ** 2
                            - ((before + after) / pulse - 1) ** 2
                        )
                        value = probabilities[i] - 0.10 * spacing - 0.04 * max(-3, excess)
                        receivers.append((value, phrase, i, left))
        if not donors or not receivers:
            break
        drop_cost, donor, drop = min(donors)
        add_value, receiver, insert, left = max(receivers)
        if add_value - drop_cost < 0.10:
            break
        hand = kept[left][-1]["c"]
        kept[insert] = [
            min(timings[insert][1], key=lambda n: (n["c"] == hand, n["y"], abs(n["x"] - (n["c"] + 1))))
        ]
        kept[drop] = [min(kept[drop], key=lambda n: (n["y"], abs(n["x"] - (n["c"] + 1))))]
        budgets[donor] -= 1
        budgets[receiver] += 1


def refine_ranked_sequences(
    timings,
    beats,
    times,
    starts,
    kept,
    anchors,
    fixed,
    near_accents,
    rank_scores,
    onset_scores,
    minimum_gap,
    pulse,
):
    """Choose a coherent path with the same number of singles between locked hits."""
    scores = [rank_scores[beat] for beat, _ in timings]
    for first, end in ranked_budget_phrases(beats, starts):
        selected = [i for i in range(first, end) if i in kept]
        if len(selected) < 3:
            continue
        locked = [
            i
            for i in selected
            if i in (selected[0], selected[-1])
            or i in anchors
            or i in near_accents
            or len(kept[i]) != 1
            or scores[i] >= 0.8
            or (i in fixed and scores[i] >= 0.45)
        ]
        for left, right in zip(locked, locked[1:]):
            interior = [i for i in selected if left < i < right]
            if not interior:
                continue
            original = set(interior)
            paths = {left: (0, ())}
            for previous, current in zip([left] + interior, interior + [right]):
                old_gap = times[current] - times[previous]
                if current == right:
                    choices = [right]
                else:
                    first_choice = bisect_left(beats, beats[current] - 3, left + 1, right)
                    last_choice = bisect_left(beats, beats[current] + 3.0001, first_choice, right)
                    choices = [
                        i for i in range(first_choice, last_choice) if i not in near_accents or i == current
                    ]
                next_paths = {}
                for i in choices:
                    value = 0
                    if i != right:
                        beat, group = timings[i]
                        value = (
                            2 * scores[i]
                            - 0.08 * easy_pulse_rank(beats[i])
                            + 0.20 * onset_scores[beat]
                            + 0.25 * (len(group) > 1)
                            + 0.10 * (i in original)
                        )
                    for j, (quality, path) in paths.items():
                        gap = times[i] - times[j]
                        if not min(minimum_gap, old_gap) - 1e-8 <= gap <= max(old_gap, 2 * pulse) + 1e-8:
                            continue
                        option = (quality + value - (gap / pulse - 1) ** 2, (*path, i))
                        if i not in next_paths or option > next_paths[i]:
                            next_paths[i] = option
                paths = next_paths
            if right not in paths:
                continue
            chosen = paths[right][1][:-1]
            if sum(scores[i] for i in chosen) - sum(scores[i] for i in interior) < 0.12:
                continue
            replaced = [kept.pop(i)[0] for i in interior]
            for i, note in zip(chosen, replaced):
                kept[i] = [
                    min(
                        timings[i][1], key=lambda n: (n["c"] != note["c"], n["y"], abs(n["x"] - (n["c"] + 1)))
                    )
                ]


def refine_ranked_doubles(
    timings, beats, times, starts, kept, protected, rank_scores, onset_scores, minimum_gap, pulse
):
    """Keep the double intact when a nearby compatible source chord is stronger."""
    for first, end in ranked_budget_phrases(beats, starts):
        hits = [i for i in range(first, end) if i in kept]
        for h in range(1, len(hits) - 1):
            left, current, right = hits[h - 1 : h + 2]
            beat = timings[current][0]
            if current in protected or len(kept[current]) != 2 or rank_scores[beat] > 0.60:
                continue
            old_gap = min(times[current] - times[left], times[right] - times[current])
            spacing = max(minimum_gap, min(old_gap, 0.70 * pulse))
            choices = []
            for i in range(left + 1, right):
                distance = abs(beats[i] - beats[current])
                if i == current or i in protected or distance > 0.75:
                    continue
                target, group = timings[i]
                if len(group) != 2 or group[0]["c"] == group[1]["c"] or not easy_double(group):
                    continue
                if min(times[i] - times[left], times[right] - times[i]) < spacing:
                    continue
                gain = (
                    rank_scores[target]
                    - rank_scores[beat]
                    + 0.12 * (onset_scores[target] - onset_scores[beat])
                    - 0.035 * distance
                )
                if gain >= 0.20:
                    choices.append((gain, i))
            if choices:
                i = max(choices)[1]
                del kept[current]
                kept[i] = timings[i][1]
                hits[h] = i


def preserve_recurring_figures(timings, beats, times, starts, kept, protected, rank_scores, minimum_gap):
    """Carry a repeated spaced entrance through the switch to a faster source rhythm."""
    moved = 0
    scores = [rank_scores[beat] for beat, _ in timings]
    for first, end in zip(starts, starts[1:] + [len(beats)]):
        blocks = defaultdict(list)
        for i in range(first, end):
            blocks[4 * math.floor((beats[i] + 0.001) / 4)].append(i)
        figures = {}
        for bar, indices in blocks.items():
            if len(indices) < 5 or abs(beats[indices[0]] - bar) > 0.02:
                continue
            figure = [indices[0]]
            for i in indices[1:]:
                if times[i] - times[figure[-1]] < 0.25:
                    break
                figure.append(i)
            shape = tuple(round((beats[i] - bar) * 48) for i in figure)
            if len(figure) == 4 and len(figure) < len(indices) and any(slot % 24 for slot in shape):
                figures[bar] = shape, figure

        for bar, (shape, figure) in figures.items():
            repeats = sum(figures.get(bar + shift, (None,))[0] == shape for shift in (-12, -8, -4, 4, 8, 12))
            if repeats < 2:
                continue
            block = blocks[bar]
            old = [i for i in block if i in kept]
            shared = set(old).intersection(figure)
            if len(shared) < 2 or len(old) > len(figure):
                continue
            pinned = shared | {i for i in old if i in protected or len(kept[i]) != 1}
            previous = max((i for i in kept if i < block[0]), default=None)
            following = min((i for i in kept if i > block[-1]), default=None)
            donor = None
            if len(old) == 3 and moved < math.ceil(0.03 * len(kept)):
                donors = [
                    i
                    for i, group in kept.items()
                    if len(group) == 2
                    and i not in protected
                    and i not in block
                    and abs(beats[i] - bar) <= 128
                ]
                if donors:
                    donor = min(donors, key=lambda i: (scores[i], abs(beats[i] - bar), i))

            choices = []
            for path in combinations(figure, len(old) + int(donor is not None)):
                if not pinned.issubset(path):
                    continue
                if previous is not None and times[path[0]] - times[previous] < min(
                    minimum_gap, times[old[0]] - times[previous]
                ):
                    continue
                if following is not None and times[following] - times[path[-1]] < min(
                    minimum_gap, times[following] - times[old[-1]]
                ):
                    continue
                choices.append((sum(scores[i] for i in path), path))
            if not choices:
                continue
            chosen = set(max(choices)[1])
            displaced = [kept.pop(i)[0] for i in old if i not in chosen]
            if donor is not None:
                first_note, second_note = sorted(
                    kept[donor], key=lambda n: (n["y"], abs(n["x"] - (n["c"] + 1)))
                )
                kept[donor] = [first_note]
                displaced.append(second_note)
                moved += 1
            for i, note in zip(sorted(chosen - set(old)), displaced):
                kept[i] = [
                    min(
                        timings[i][1], key=lambda n: (n["c"] != note["c"], n["y"], abs(n["x"] - (n["c"] + 1)))
                    )
                ]


def swap_easy_phrase_singles(
    timings,
    times,
    starts,
    retained,
    anchors,
    minimum_gap,
    pulse,
    cue_weights,
    *,
    grid=None,
    redistribute=False,
    onset_scores=None,
    rank_scores=None,
    boosted_rank=False,
    ranked_budget=False,
    motif_rank=False,
    budget_rank_scores=None,
    phrase_search=False,
    recurring_figures=False,
):
    """Refine the pulse at a fixed note count between protected attacks."""
    retained_ids = {n["id"] for n in retained}
    kept = {}
    for i, (_, group) in enumerate(timings):
        selected = [n for n in group if n["id"] in retained_ids]
        if selected:
            kept[i] = selected
    beats = [grid.beat_at_seconds(time) for time in times] if grid else [beat for beat, _ in timings]
    costs = easy_swap_costs(beats, starts, cue_weights)
    pulse_ranks = [easy_pulse_rank(beat) for beat in beats]
    pickups = easy_pickup_pairs(beats, starts)
    accent_grid = easy_accent_grid(timings, beats, times, starts, pulse)
    accent_beats = sorted(beats[i] for i in accent_grid)
    near_accents = set()
    if accent_beats and (onset_scores is not None or boosted_rank):
        for i in costs:
            left = bisect_left(accent_beats, beats[i] - 2.0001)
            if left < len(accent_beats) and accent_beats[left] <= beats[i] + 2.0001:
                near_accents.add(i)
    if onset_scores is not None:
        # A loud neighboring hit should carry less weight when the chart
        # already supports a recurring swing or irregular riff accent.
        for i in costs:
            near_accent = i in near_accents
            costs[i] -= (0.25 if near_accent else 1) * onset_scores[timings[i][0]]
            if near_accent:
                continue
            # The source chart can mark a quieter accent with a stack or
            # with space on either side of a short figure.
            group = timings[i][1]
            before, after = beats[i] - beats[i - 1], beats[i + 1] - beats[i]
            costs[i] -= 1.2 * min(1, max(0, after - before))
            costs[i] -= 0.9 if len(group) >= 3 else 0.6 if len(group) == 2 else 0
            # Keep the entrance of a paired two-hand figure audible in the
            # easier rhythm, even when its second chord is louder.
            if (
                before >= 1.2
                and after <= 1.0001
                and len({n["c"] for n in group}) == 2
                and len({n["c"] for n in timings[i + 1][1]}) == 2
            ):
                costs[i] -= 1.8 * min(1, before - after)
    if rank_scores is not None:
        boosted_weight = EASY_MOTIF_RANK_WEIGHT if motif_rank else EASY_BOOSTED_RANK_WEIGHT
        for i in costs:
            rank_score = rank_scores[timings[i][0]]
            if boosted_rank:
                # Keep the learned signal weaker around established swing and riff accents.
                influence = 0.5 if i in near_accents else 1
                costs[i] -= influence * boosted_weight * (rank_score - 0.5)
            else:
                costs[i] -= EASY_RANK_WEIGHT * rank_score
    fixed = {i for i in kept if i in anchors or len(kept[i]) != 1}
    # Keep an established two-beat downbeat pulse in phase. Moving its
    # interior as a block can lower the local cost while shifting the groove.
    pulse_run = []
    for i in kept:
        if pulse_run and abs(beats[i] - beats[pulse_run[-1]] - 2) > 0.0001:
            pulse_run = []
        if abs(beats[i] / 2 - round(beats[i] / 2)) <= 0.00005:
            pulse_run.append(i)
            if len(pulse_run) == 4:
                fixed.update(pulse_run)
            elif len(pulse_run) > 4:
                fixed.add(i)
    if accent_beats:
        for i in fixed - anchors:
            neighbor = bisect_left(accent_beats, beats[i] - 1.0001)
            if (
                len(kept[i]) == 1
                and neighbor < len(accent_beats)
                and accent_beats[neighbor] <= beats[i] + 1.0001
            ):
                fixed.remove(i)
    fixed = sorted(fixed)
    timing_radius = 1.5 if motif_rank else 1
    replacements = {}
    for left, right in zip(fixed, fixed[1:]):
        original = [i for i in range(left, right + 1) if i in kept]
        if len(original) < 3:
            continue
        paths = {left: (0, ())}
        for previous, current in zip(original, original[1:]):
            old_gap = times[current] - times[previous]
            # Let repeated accents move farther than ordinary substitutes.
            choices = (
                [right]
                if current == right
                else [
                    i
                    for i in range(
                        bisect_left(beats, beats[current] - 2.0001, left + 1, right),
                        bisect_left(beats, beats[current] + 2.0001, current, right),
                    )
                    if i in accent_grid or abs(beats[i] - beats[current]) <= timing_radius + 0.0001
                ]
            )
            next_paths = {}
            for i in choices:
                move = abs(timings[i][0] - timings[current][0])
                attack_cost = 0 if i == right else costs[i] - accent_grid.get(i, 0)
                if move:
                    attack_cost += 0.15 + 0.15 * move
                # Recurrence alone can reward filler just before or after a
                # pulse hit. Require stronger evidence to displace that hit.
                if (
                    pulse_ranks[current] < 2
                    and pulse_ranks[i] > pulse_ranks[current]
                    and beats[i] - beats[i - 1] < 1 - 0.0001
                    and i not in accent_grid
                ):
                    attack_cost += 0.7 * (pulse_ranks[i] - pulse_ranks[current])
                for j, (cost, path) in paths.items():
                    gap = times[i] - times[j]
                    if not min(old_gap, minimum_gap) - 1e-8 <= gap <= max(old_gap, 2 * pulse) + 1e-8:
                        continue
                    pickup_bonus = 1 if (j, i) in pickups and 0.7 * pulse <= gap <= 0.9 * pulse else 0
                    option = (cost + attack_cost + 1.5 * (gap / pulse - 1) ** 2 - pickup_bonus, (*path, i))
                    if i not in next_paths or option < next_paths[i]:
                        next_paths[i] = option
            paths = next_paths
        for old, new in zip(original[1:-1], paths[right][1][:-1]):
            if old != new:
                hand = kept[old][0]["c"]
                replacements[old] = (
                    new,
                    min(
                        timings[new][1],
                        key=lambda n: (
                            n["c"] != hand,
                            n["y"],
                            abs(n["x"] - (n["c"] + 1)),
                        ),
                    ),
                )
    for old in replacements:
        del kept[old]
    kept.update((new, [note]) for new, note in replacements.values())
    if redistribute:
        accent_costs = {i: cost - accent_grid.get(i, 0) for i, cost in costs.items()}
        redistribute_easy_phrase_budget(
            timings, times, starts, kept, anchors, accent_costs, minimum_gap, pulse
        )
        if ranked_budget and rank_scores is not None:
            allocate_ranked_phrase_budget(
                timings,
                beats,
                times,
                starts,
                kept,
                anchors | near_accents,
                budget_rank_scores if budget_rank_scores is not None else rank_scores,
                minimum_gap,
                pulse,
            )
    if phrase_search and rank_scores is not None and onset_scores is not None:
        refine_ranked_sequences(
            timings,
            beats,
            times,
            starts,
            kept,
            anchors,
            set(fixed),
            near_accents,
            rank_scores,
            onset_scores,
            minimum_gap,
            pulse,
        )
        refine_ranked_doubles(
            timings,
            beats,
            times,
            starts,
            kept,
            anchors | near_accents,
            rank_scores,
            onset_scores,
            minimum_gap,
            pulse,
        )
        if recurring_figures:
            preserve_recurring_figures(
                timings, beats, times, starts, kept, anchors | near_accents, rank_scores, minimum_gap
            )
    return sorted((n for notes in kept.values() for n in notes), key=lambda n: (n["b"], n["c"]))


def easy_timing_notes(
    level, protected, minimum_gap, pulse, *, phrase_accents=False, phrase_swaps=False, phrase_budget=False
):
    timings = list(groups(level["notes"]).items())
    windows, accents = easy_sparse_cues(level, timings)
    timings = [
        (b, g)
        for b, g in timings
        if b in accents
        or any(n["id"] in protected for n in g)
        or not any(start - 0.0001 <= b <= end + 0.0001 for start, end in windows)
    ]
    if not timings:
        return level["notes"][:1]
    times = [level["clock"].seconds(b) for b, _ in timings]
    required = {i for i, (b, g) in enumerate(timings) if b in accents or any(n["id"] in protected for n in g)}
    for i in sorted(set(easy_motif_indices(timings))):
        if all(abs(times[i] - times[j]) >= minimum_gap for j in required):
            required.add(i)

    # True source rests separate phrases. Density and stream boundaries can fall mid-phrase.
    starts = [0] + [
        i
        for i in range(1, len(times))
        if timings[i][0] - timings[i - 1][0] >= 2.5 and times[i] - times[i - 1] >= 1.25 * pulse
    ]
    cue_weights = easy_phrase_cue_weights(timings, starts) if phrase_accents else {}
    for i, (beat, _) in enumerate(timings):
        if beat in level.get("linkAccentBeats", ()):
            cue_weights[i] = cue_weights.get(i, 0) + 0.25
    selected = set(required)
    for first, end in zip(starts, starts[1:] + [len(times)]):
        anchors = sorted(i for i in required if first <= i < end)
        if not anchors or times[anchors[0]] - times[first] >= minimum_gap:
            anchors.insert(0, first)
        if times[end - 1] - times[anchors[-1]] >= minimum_gap:
            anchors.append(end - 1)
        required.update(anchors)
        selected.update(anchors)
        for a, b in zip(anchors, anchors[1:]):
            # Find a coherent pulse between fixed accents. A skipped beat costs more
            # as the gap grows; weaker subdivisions can win when they bridge a gap.
            costs = {a: 0}
            previous = {}
            for i in range(a + 1, b + 1):
                beat = timings[i][0]
                grid_cost = (
                    0
                    if abs(beat / 2 - round(beat / 2)) < 0.0001
                    else 0.2
                    if abs(beat - round(beat)) < 0.0001
                    else 0.65
                    if abs(beat * 2 - round(beat * 2)) < 0.0002
                    else 1.5
                )
                # Resolve close pulse choices without rewarding denser paths.
                grid_cost -= min(0.35, 0.65 * grid_cost) * cue_weights.get(i, 0)
                for j in range(i - 1, a - 1, -1):
                    gap = times[i] - times[j]
                    if gap > 3 * pulse and j != a and i != b:
                        break
                    if j not in costs or (gap < minimum_gap and (j, i) != (a, b)):
                        continue
                    cost = costs[j] + 3 * ((gap / pulse) - 1) ** 2 + (grid_cost if i != b else 0)
                    if cost < costs.get(i, math.inf):
                        costs[i], previous[i] = cost, j
            while b != a:
                selected.add(b)
                b = previous[b]

    result = []
    last_hand = None
    indices = sorted(selected)
    for k, i in enumerate(indices):
        _, group = timings[i]
        fixed = [n for n in group if n["id"] in protected]
        left_gap = times[i] - times[indices[k - 1]] if k else math.inf
        right_gap = times[indices[k + 1]] - times[i] if k + 1 < len(indices) else math.inf
        spaced_double = (
            len(group) == 2
            and group[0]["c"] != group[1]["c"]
            and min(left_gap, right_gap) >= 0.8 * pulse
            and easy_double(group)
        )
        if fixed or spaced_double:
            result.extend(fixed or group)
            last_hand = result[-1]["c"]
        else:
            note = min(group, key=lambda n: (n["c"] == last_hand, n["y"], abs(n["x"] - (n["c"] + 1))))
            result.append(note)
            last_hand = note["c"]
    if phrase_swaps:
        return swap_easy_phrase_singles(
            timings,
            times,
            starts,
            result,
            required,
            minimum_gap,
            pulse,
            cue_weights,
            grid=level.get("legacyGridClock"),
            redistribute=phrase_budget,
            onset_scores=level.get("onsetScores"),
            rank_scores=level.get("rankScores"),
            boosted_rank=level.get("boostedRank", False),
            ranked_budget=level.get("rankedPhraseBudget", False),
            motif_rank=level.get("motifRank", False),
            budget_rank_scores=level.get("budgetRankScores"),
            phrase_search=level.get("rankedPhraseSearch", False),
            recurring_figures=level.get("rankedRecurringFigures", False),
        )
    return result


def easy_double(group):
    left, right = sorted(group, key=lambda n: n["c"])
    directions = {left["d"], right["d"]}
    coordinated = directions <= {0, 4, 5} or directions <= {1, 6, 7} or (left["d"], right["d"]) == (2, 3)
    return coordinated and not check_group(group)


def fit_easy_note_budget(level, retained, budget, protected, minimum_gap, pulse):
    """Fit the reference pulse budget using eligible attacks and safe spacing."""
    if len(retained) == budget:
        return retained
    timings = groups(level["notes"])
    beat_by_id = {note["id"]: beat for beat, group in timings.items() for note in group}
    kept = defaultdict(list)
    for note in retained:
        kept[beat_by_id[note["id"]]].append(note)
    times = {beat: level["clock"].seconds(beat) for beat in timings}
    beats = sorted(kept)
    fixed = set(protected) | level["anchorIds"]
    windows, accents = easy_sparse_cues(level, list(timings.items()))
    rank = level.get("rankScores", {})
    mix = level.get("onsetScores", {})
    count = len(retained)

    def quality(beat):
        return rank.get(beat, 0) + 0.20 * mix.get(beat, 0) - 0.08 * easy_pulse_rank(beat)

    def gaps(beat, index, following):
        left = times[beat] - times[beats[index - 1]] if index else math.inf
        right = times[beats[following]] - times[beat] if following < len(beats) else math.inf
        return left, right

    if count > budget:
        # Preserve phrase edges when fitting a rhythm from a sparse parent.
        edges = {beats[0], beats[-1], *accents}
        for left, right in zip(timings, list(timings)[1:]):
            if right - left >= 2.5 and times[right] - times[left] >= 1.25 * pulse:
                edges.update((left, right))
        for beat in sorted(kept, key=quality):
            while count > budget and len(kept[beat]) > 1:
                removable = [n for n in kept[beat] if n["id"] not in fixed]
                if not removable:
                    break
                kept[beat].remove(max(removable, key=lambda n: (n["y"], abs(n["x"] - n["c"] - 1))))
                count -= 1
        for beat in sorted(kept, key=quality):
            if count <= budget:
                break
            if beat in edges or any(n["id"] in fixed for n in kept[beat]):
                continue
            index = bisect_left(beats, beat)
            left, right = gaps(beat, index, index + 1)
            if left + right > 2 * pulse:
                continue
            count -= len(kept.pop(beat))
            beats.pop(index)
    else:
        candidates = [
            beat
            for beat in timings
            if beat not in kept and not any(start - 0.0001 <= beat <= end + 0.0001 for start, end in windows)
        ]
        while count < budget:
            options = []
            for beat in candidates:
                index = bisect_left(beats, beat)
                left, right = gaps(beat, index, index)
                if min(left, right) >= minimum_gap:
                    score = quality(beat) + 0.25 * min(1, min(left, right) / pulse)
                    options.append((score, beat, index))
            if not options:
                break
            _, beat, index = max(options)
            previous_hand = kept[beats[index - 1]][-1]["c"] if index else None
            kept[beat] = [
                min(timings[beat], key=lambda n: (n["c"] == previous_hand, n["y"], abs(n["x"] - n["c"] - 1)))
            ]
            beats.insert(index, beat)
            candidates.remove(beat)
            count += 1
        for beat in sorted(kept, key=quality, reverse=True):
            if count >= budget:
                break
            group = timings[beat]
            if len(kept[beat]) != 1 or len(group) != 2 or group[0]["c"] == group[1]["c"]:
                continue
            index = bisect_left(beats, beat)
            if min(gaps(beat, index, index + 1)) >= 0.8 * pulse and easy_double(group):
                kept[beat] = group
                count += 1
    return sorted((n for group in kept.values() for n in group), key=lambda n: (n["b"], n["c"]))


def best_guess_notes(level, sections, streams, profile, protected_ids=(), *, budget_reference=None):
    """Thin low-salience grid subdivisions and dense doubles within each section."""
    ns = level["notes"]
    fixed = set(protected_ids) | level["anchorIds"]
    protected = set(fixed)
    if "automaticTimingPulseSeconds" in profile:
        return easy_timing_notes(
            level,
            protected,
            profile["automaticTimingMinimumGapSeconds"],
            profile["automaticTimingPulseSeconds"],
            phrase_accents=profile.get("automaticTimingPhraseAccents", False),
            phrase_swaps=profile.get("automaticTimingPhraseSwaps", False),
            phrase_budget=profile.get("automaticTimingPhraseBudget", False),
        )
    for stream in streams:
        protected.update(
            stream["noteIds"] if profile["minimumSourceNoteRatio"] >= 0.8 else stream["noteIds"][:2]
        )
    floor = profile["minimumSourceNoteRatio"]
    target = (floor + profile["maximumSourceNoteRatio"]) / 2
    reference_beats = [n["b"] for n in budget_reference["notes"]] if budget_reference else None
    retained = []
    for section in sections:
        gs = list(
            groups([n for n in ns if section["startBeat"] <= n["b"] < section["endBeatExclusive"]]).values()
        )
        if not gs:
            continue
        count = sum(map(len, gs))
        reference_count = (
            count
            if reference_beats is None
            else (
                bisect_left(reference_beats, section["endBeatExclusive"])
                - bisect_left(reference_beats, section["startBeat"])
            )
        )
        budget = min(count, max(math.ceil(reference_count * floor), round(reference_count * target), 1))
        removed = set()

        # The ends establish the phrase. Prefer leaving attacks with a rest, chords and whole beats.
        def salience(i):
            g = gs[i]
            beat = g[0]["b"]
            on_beat = abs(beat - round(beat)) < 0.0001
            half_beat = abs(beat * 2 - round(beat * 2)) < 0.0002
            before = level["clock"].gap(gs[i - 1][0]["b"], beat)
            after = level["clock"].gap(beat, gs[i + 1][0]["b"])
            rank = level.get("rankScores", {}).get(round(beat, 5), 0)
            linked = round(beat, 5) in level.get("linkAccentBeats", ())
            return (3 * on_beat + half_beat + len(g) - 1 + min(1, max(before, after)) + 0.75 * rank + 0.35 * linked, i)

        for i in sorted(range(1, len(gs) - 1), key=salience):
            g = gs[i]
            if count - len(g) < budget or any(n["id"] in protected for n in g):
                continue
            if floor >= 0.8 and (len(g) == 1 or abs(g[0]["b"] - round(g[0]["b"])) < 0.0001):
                continue
            removed.update(n["id"] for n in g)
            count -= len(g)
        if count > budget:
            for i, g in enumerate(gs[1:-1], start=1):
                if count <= budget:
                    break
                if len(g) < 2 or any(n["id"] in removed for n in g):
                    continue
                hands = Counter(n["c"] for n in g)
                for n in sorted(g, key=lambda n: (-hands[n["c"]], n["c"] == i % 2, n["y"]), reverse=False):
                    if count <= budget or len(g) - sum(v["id"] in removed for v in g) <= 1:
                        break
                    if n["id"] in protected:
                        continue
                    removed.add(n["id"])
                    count -= 1
        for group in gs:
            kept = [n for n in group if n["id"] not in removed]
            if profile["maximumSourceNoteRatio"] <= 0.5 and len(kept) > 1:
                # A section boundary can preserve an attack without keeping a
                # whole same-hand stack in a beginner rhythm.
                for hand in (0, 1):
                    hand_notes = [n for n in kept if n["c"] == hand]
                    if hand_notes:
                        retained.extend(
                            [n for n in hand_notes if n["id"] in fixed]
                            or [min(hand_notes, key=lambda n: (n["y"], abs(n["x"] - (hand + 1))))]
                        )
            else:
                retained.extend(kept)
    return retained
