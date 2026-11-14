"""Refine nested timing proposals using pulse evidence from the budget reference."""

import math
from bisect import bisect_left, bisect_right
from collections import defaultdict

from analyze_standard_spreads import groups, nearest
from rhythm_inputs import apply_merges, easy_double, easy_sparse_cues


def fit_last_easy_double(level, retained, merges, protected, budget, scores):
    """Fill a one-note shortfall at a coordinated double with room on both sides."""
    if len(retained) != budget - 1:
        return retained
    locked = protected | level["anchorIds"] | {m[key] for m in merges for key in ("noteId", "targetId")}
    played = groups(apply_merges(level, retained, merges))
    source = groups(level["notes"])
    beats = list(played)
    clock = level["clock"]
    choices = []
    for i, beat in enumerate(beats):
        group = source.get(beat, ())
        if len(played[beat]) != 1 or len(group) != 2 or not easy_double(group):
            continue
        if group[0]["c"] == group[1]["c"] or any(n["id"] in locked for n in group):
            continue
        before = clock.gap(beats[i - 1], beat) if i else math.inf
        after = clock.gap(beat, beats[i + 1]) if i + 1 < len(beats) else math.inf
        if min(before, after) >= 0.50:
            spare = next(n for n in group if n["id"] != played[beat][0]["id"])
            choices.append((scores.get(beat, 0), beat, spare))
    return [*retained, max(choices)[-1]] if choices else retained


def refine_nested_timing(level, retained, merges, protected, reference, evidence, tier):
    """Trade eligible notes within a phrase without increasing its timing density budget."""
    timings = groups(level["notes"])
    clock = level["clock"]
    grid = level.get("legacyGridClock") or clock
    times = {beat: clock.seconds(beat) for beat in timings}
    grid_beats = {beat: grid.beat_at_seconds(time) for beat, time in times.items()}
    scores, mix, pulse_beats = evidence
    refs = sorted(pulse_beats)
    ref_notes = groups(reference["notes"])
    locked = set(protected) | level["anchorIds"] | {m[k] for m in merges for k in ("noteId", "targetId")}
    kept = defaultdict(list)
    beat_by_id = {n["id"]: b for b, group in timings.items() for n in group}
    for note in retained:
        kept[beat_by_id[note["id"]]].append(note)
    if not kept:
        return retained
    order = sorted(kept)
    fixed = {order[0], order[-1]}
    # Keep attacks bordering genuine rests in the Expert+ source.
    for left, right in zip(ref_notes, list(ref_notes)[1:]):
        if right - left >= 2.5 and clock.gap(left, right) >= 0.95:
            fixed.update((left, right))
    blocked = set()
    if tier == "Easy":
        windows, accents = easy_sparse_cues(level, list(timings.items()))
        fixed.update(accents)
        blocked = {b for b in timings if any(lo <= b <= hi for lo, hi in windows)}
    for beat, group in timings.items():
        if any(n["id"] in locked for n in group):
            fixed.add(beat)

    def phase(beat):
        b = grid_beats[beat]
        if abs(b - round(b)) < 0.065:
            return 1
        if abs(b * 2 - round(b * 2)) < 0.09:
            return 0.40
        if abs(b * 3 - round(b * 3)) < 0.06:
            return 0.30
        return 0

    def evidence_score(b):
        skeleton = nearest(b, refs) is not None
        s = scores.get(round(b, 5), 0)
        a = mix.get(round(b, 5), 0)
        if tier == "Normal":
            return 0.95 * s + 0.30 * a + 0.46 * skeleton + 0.46 * phase(b)
        return 1.20 * s + 0.40 * a + 0.34 * skeleton + 0.38 * phase(b)

    quality = {b: evidence_score(b) for b in timings}
    minimum_gap = 0.34 if tier == "Easy" else 0.24
    pulse = 0.74 if tier == "Easy" else 0.44
    radius = 0.49 if tier == "Easy" else 0.33

    def spacing(left, middle, right):
        return times[middle] - times[left], times[right] - times[middle]

    def regularity(gaps):
        return sum(min(1, abs(gap - pulse) / pulse) ** 2 for gap in gaps)

    candidates = sorted(timings)
    for beat in order[1:-1]:
        if beat in fixed or beat not in kept or len(kept[beat]) != 1:
            continue
        order = sorted(kept)
        index = bisect_left(order, beat)
        if index == 0 or index == len(order) - 1:
            continue
        left, right = order[index - 1], order[index + 1]
        old_gaps = spacing(left, beat, right)
        limit = min(minimum_gap, min(old_gaps))
        old_note = kept[beat][0]
        new = []
        for target in candidates[bisect_right(candidates, left) : bisect_left(candidates, right)]:
            if (
                target in kept
                or target in fixed
                or target in blocked
                or abs(times[target] - times[beat]) > radius
            ):
                continue
            gaps = spacing(left, target, right)
            if min(gaps) < limit - 1e-8:
                continue
            group = [n for n in timings[target] if n["c"] == old_note["c"]]
            if not group:
                continue
            gain = quality[target] - quality[beat] - 0.20 * (regularity(gaps) - regularity(old_gaps))
            if gain > 0.17:
                new.append((gain, target, group))
        if new:
            _, target, group = max(new)
            kept[target] = [min(group, key=lambda n: (n["y"], abs(n["x"] - n["c"] - 1)))]
            del kept[beat]
    # A double can donate its second note to a stronger nearby eligible attack.
    donors = defaultdict(list)
    for beat, group in kept.items():
        if len(group) == 2 and beat not in fixed:
            donors[math.floor(grid_beats[beat] / 8)].append(beat)
    for beat in sorted(timings, key=quality.get, reverse=True):
        if beat in kept or beat in blocked or beat in fixed:
            continue
        block = math.floor(grid_beats[beat] / 8)
        if not donors[block]:
            continue
        order = sorted(kept)
        index = bisect_left(order, beat)
        if not 0 < index < len(order):
            continue
        gaps = spacing(order[index - 1], beat, order[index])
        if min(gaps) < minimum_gap - 1e-8:
            continue
        for donor in sorted(donors[block], key=quality.get):
            if quality[beat] < 0.42 + 0.45 * quality[donor]:
                break
            match = [(old, new) for old in kept[donor] for new in timings[beat] if new["c"] == old["c"]]
            if not match:
                continue
            old, new = min(
                match, key=lambda pair: (pair[1]["y"], abs(pair[1]["x"] - pair[1]["c"] - 1), pair[1]["id"])
            )
            kept[donor].remove(old)
            kept[beat] = [new]
            donors[block].remove(donor)
            break
    return sorted((n for group in kept.values() for n in group), key=lambda n: (n["b"], n["c"]))
