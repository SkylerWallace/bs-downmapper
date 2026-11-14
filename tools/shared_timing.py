"""Build a fixed-budget nested rhythm from the shared model's beat scores."""

from bisect import bisect_left
from collections import Counter, defaultdict
import math

from analyze_standard_spreads import groups, nearest
from rhythm_inputs import (
    apply_merges,
    best_guess_notes,
    easy_sparse_cues,
    merge_nearby_singles,
    refine_easy_pulse,
)


MIN_GAP = {"Expert": 0.07, "Hard": 0.105, "Normal": 0.17, "Easy": 0.33}
PULSE = {"Expert": 0.24, "Hard": 0.30, "Normal": 0.44, "Easy": 0.74}
DISTINCT_GROWTH = {"Expert": 0.0, "Hard": 0.54, "Normal": 0.71, "Easy": 1.0}
PHRASE_BEATS = 48


def phrase_indices(level, beats):
    clock = level["clock"]
    grid = level.get("legacyGridClock") or clock
    return {b: math.floor((grid.beat_at_seconds(clock.seconds(b)) + 0.0001) / PHRASE_BEATS) for b in beats}


def prefer_linked_repeats(
    level, selected, protected, tier, scores, repeat_data, *, required_beats=(), can_swap=None, on_swap=None
):
    """Trade close calls inside one repeat occurrence, with an unchanged phrase and hand budget."""
    reference, sections, links = repeat_data
    if not links:
        return selected
    source = groups(level["notes"])
    available = list(source)
    by_id = {note["id"]: beat for beat, notes in source.items() for note in notes}
    kept = defaultdict(list)
    for note in selected:
        kept[by_id[note["id"]]].append(note)
    original = set(kept)
    fixed_ids = set(protected) | level["anchorIds"]
    fixed_beats = {nearest(beat, available) for beat in required_beats}
    if tier == "Easy":
        windows, accents = easy_sparse_cues(level, list(source.items()))
        fixed_beats.update(accents)
    else:
        windows = []
    times = {b: level["clock"].seconds(b) for b in source}
    phrase = phrase_indices(level, available)
    bounds = defaultdict(list)
    for beat in original:
        bounds[phrase[beat]].append(beat)
    fixed_beats.update(beat for beats in bounds.values() for beat in (min(beats), max(beats)))
    occurrence = {}
    for index, section in enumerate(sections):
        first = bisect_left(reference, section["start"] - 1e-5)
        end = bisect_left(reference, section["end"])
        for b in reference[first:end]:
            occurrence[b] = index

    support, local_score, peer_groups = {}, {}, {}
    for link in links:
        peers = {nearest(reference[i], available): reference[i] for i in link}
        peers.pop(None, None)
        if len(peers) < 2:
            continue
        votes = sum(beat in original for beat in peers)
        for beat, ref in peers.items():
            if ref in occurrence:
                support[beat] = (votes - (beat in original)) / (len(peers) - 1)
                peer_groups[beat] = tuple(peer for peer in peers if peer != beat)
                local_score[beat] = scores[ref]
                occurrence[beat] = occurrence[ref]

    donors = defaultdict(list)
    for beat in original & support.keys():
        if beat not in fixed_beats and len(kept[beat]) == 1 and kept[beat][0]["id"] not in fixed_ids:
            donors[occurrence[beat], phrase[beat]].append(beat)
    swaps = []
    for beat in support.keys() - original:
        if support[beat] < 0.6 or any(start <= beat <= end for start, end in windows):
            continue
        for old in donors[occurrence[beat], phrase[beat]]:
            agreement_gain = support[beat] - support[old]
            evidence = local_score[beat] - local_score[old]
            gain = evidence + 0.18 * agreement_gain
            if (
                agreement_gain >= 0.5 and evidence >= -0.12 and gain >= 0.06
                and abs(times[beat] - times[old]) <= PULSE[tier]
            ):
                swaps.append((gain, beat, old))

    ordered = sorted(kept)
    edited = set()
    per_occurrence = Counter()
    for _, beat, old in sorted(swaps, key=lambda move: (-move[0], move[1], move[2])):
        area = occurrence[beat]
        if beat in edited or old in edited or per_occurrence[area] >= 2:
            continue
        added_peers = peer_groups[beat]
        removed_peers = peer_groups[old]
        live_support = sum(peer in kept for peer in added_peers) / len(added_peers)
        live_cost = sum(peer in kept for peer in removed_peers) / len(removed_peers)
        live_gain = live_support - live_cost
        live_score = local_score[beat] - local_score[old] + 0.18 * live_gain
        if live_support < 0.6 or live_gain < 0.5 or live_score < 0.06:
            continue
        note = next((n for n in source[beat] if n["c"] == kept[old][0]["c"]), None)
        donor = kept[old][0]
        if note is None or (can_swap is not None and not can_swap(donor, note)):
            continue
        incoming_beat, outgoing_beat = note["b"], kept[old][0]["b"]
        exact_phrases = phrase_indices(level, (incoming_beat, outgoing_beat))
        if exact_phrases[incoming_beat] != exact_phrases[outgoing_beat]:
            continue
        index = bisect_left(ordered, old)
        neighbors = ordered[:index] + ordered[index + 1 :]
        insertion = bisect_left(neighbors, beat)
        if not 0 < insertion < len(neighbors):
            continue
        before, after = times[neighbors[insertion - 1]], times[neighbors[insertion]]
        if min(times[beat] - before, after - times[beat]) < MIN_GAP[tier] - 1e-8:
            continue
        old_gap = max(times[old] - times[ordered[index - 1]], times[ordered[index + 1]] - times[old])
        if insertion == index:
            new_gap = max(times[beat] - before, after - times[beat])
        else:
            new_gap = times[ordered[index + 1]] - times[ordered[index - 1]]
        if new_gap > max(2 * PULSE[tier], 1.15 * old_gap) + 1e-8:
            continue
        kept[beat] = [note]
        del kept[old]
        if on_swap is not None:
            on_swap(donor, note)
        neighbors.insert(insertion, beat)
        ordered = neighbors
        edited.update((beat, old))
        per_occurrence[area] += 1
    return [note for beat in ordered for note in kept[beat]]


def easy_draft(reference, profile, protected, sections, streams, scores, mix):
    """Use the same ranker's Easy scores to forecast accents before pruning higher tiers."""
    level = dict(
        reference,
        onsetScores=mix,
        rankScores=scores,
        budgetRankScores=scores,
        boostedRank=True,
        motifRank=True,
        rankedPhraseBudget=True,
        rankedPhraseSearch=True,
        rankedRecurringFigures=True,
    )
    notes = best_guess_notes(level, sections, streams, profile, protected)
    merges = merge_nearby_singles(level, notes, sections, streams, protected)
    notes = refine_easy_pulse(
        level,
        notes,
        merges,
        protected,
        profile["automaticTimingMinimumGapSeconds"],
        profile["automaticTimingPulseSeconds"],
    )
    return list(groups(apply_merges(level, notes, merges)))


def phrase_selection(source, seed, fixed, quality, times, gap, growth):
    """Re-rank a phrase within its original note count and retain its protected heads."""
    quota = sum(map(len, seed.values()))
    wanted = len(seed) + round((quota - len(seed)) * growth)
    chosen = sorted(fixed)
    wanted = max(len(chosen), min(wanted, quota - sum(len(g) - 1 for g in fixed.values())))
    ordered = sorted(source, key=lambda b: (-quality[b], -len(source[b]), b))
    capacity = sum(len(source[b]) for b in chosen)
    for beat in ordered:
        if len(chosen) >= wanted and capacity >= quota:
            break
        if beat in fixed or len(chosen) >= quota - sum(len(g) - 1 for g in fixed.values()):
            continue
        index = bisect_left(chosen, beat)
        if index and times[beat] - times[chosen[index - 1]] < gap - 1e-8:
            continue
        if index < len(chosen) and times[chosen[index]] - times[beat] < gap - 1e-8:
            continue
        chosen.insert(index, beat)
        capacity += len(source[beat])
    if capacity < quota or len(chosen) < wanted:
        return seed
    kept = {beat: fixed.get(beat, source[beat][:1])[:] for beat in chosen}
    remaining = quota - sum(map(len, kept.values()))
    for beat in sorted(chosen, key=lambda b: (len(seed.get(b, ())) < 2, -quality[b], b)):
        spares = [n for n in source[beat] if n not in kept[beat]]
        kept[beat].extend(spares[:remaining])
        remaining -= min(remaining, len(spares))
        if not remaining:
            break
    return kept


def trade_phrase_notes(kept, source, quality, times, phrase, fixed_ids, fixed_beats, draft, tier):
    """Exchange weak editable notes for spaced attacks, with a local density cap."""
    counts = Counter()
    for beat, notes in kept.items():
        counts[phrase[beat]] += len(notes)
    original = counts.copy()
    room = {area: max(1, math.ceil(count * 0.12)) for area, count in counts.items()}
    pulse = PULSE[tier]
    gap = MIN_GAP[tier]
    for _ in range(max(1, math.floor(sum(original.values()) * 0.04))):
        ordered = sorted(kept)
        additions, removals = [], []
        for beat, group in source.items():
            area = phrase[beat]
            if beat in kept or area not in original or counts[area] >= original[area] + room[area]:
                continue
            i = bisect_left(ordered, beat)
            if not 0 < i < len(ordered):
                continue
            prev, next_beat = ordered[i - 1], ordered[i]
            left, right = times[beat] - times[prev], times[next_beat] - times[beat]
            if not gap - 1e-8 <= min(left, right) <= 1.75 * pulse:
                continue
            paired = beat in draft and any(b in draft for b in (prev, next_beat))
            bridge = 0.12 * min(left, right) / max(left, right) * min(1, (left + right) / (2 * pulse))
            hand = kept[prev][-1]["c"]
            note = min(group, key=lambda n: (n["c"] == hand, n["y"], n["id"]))
            additions.append((quality[beat] + bridge + 0.08 * paired, beat, note))
        for i, beat in enumerate(ordered[1:-1], 1):
            area = phrase[beat]
            if counts[area] <= max(1, original[area] - room[area]):
                continue
            notes = kept[beat]
            editable = [n for n in notes if n["id"] not in fixed_ids]
            if not editable:
                continue
            if len(notes) > 1:
                cost = 0.43 + 0.52 * quality[beat]
            else:
                if beat in fixed_beats or (tier != "Easy" and beat in draft):
                    continue
                left = times[beat] - times[ordered[i - 1]]
                right = times[ordered[i + 1]] - times[beat]
                if left + right > max(2 * pulse, 1.35 * max(left, right)) + 1e-8:
                    continue
                cost = quality[beat] + 0.13 * min(1, min(left, right) / pulse) + 0.08 * (beat in draft)
            removals.append((cost, beat, max(editable, key=lambda n: (n["y"], n["id"]))))
        if not additions or not removals:
            break
        value, incoming, new = max(additions, key=lambda item: (item[0], -item[1]))
        cost, outgoing, old = min(removals, key=lambda item: (item[0], item[1]))
        if value - cost < 0.18:
            break
        kept[outgoing].remove(old)
        if not kept[outgoing]:
            del kept[outgoing]
        kept[incoming] = [new]
        counts[phrase[outgoing]] -= 1
        counts[phrase[incoming]] += 1
    return kept


def select_shared_timings(level, seed, protected, tier, scores, mix, draft, reference_beats):
    """Select source IDs only, preserving the seed's song budget and all protected notes."""
    timings = groups(level["notes"])
    seed_ids = {n["id"] for n in seed}
    locked = set(protected) | level["anchorIds"]
    kept = {b: [n for n in notes if n["id"] in seed_ids] for b, notes in timings.items()}
    kept = {b: notes for b, notes in kept.items() if notes}
    if not kept or not locked <= seed_ids:
        raise ValueError("The shared timing budget could not retain all protected notes.")
    clock = level["clock"]
    times = {b: clock.seconds(b) for b in timings}
    phrase = phrase_indices(level, timings)
    fixed_beats = {min(kept), max(kept)}
    if tier == "Easy":
        windows, accents = easy_sparse_cues(level, list(timings.items()))
        fixed_beats.update(accents & kept.keys())
        timings = {
            b: notes
            for b, notes in timings.items()
            if b in kept or b in accents or not any(start <= b <= end for start, end in windows)
        }
    quality, trade_quality, matched_draft = {}, {}, set()
    lower_weights = {
        "Expert": {"Hard": 0.135, "Normal": 0.09, "Easy": 0.045},
        "Hard": {"Normal": 0.50, "Easy": 0.22},
        "Normal": {"Easy": 0.55},
        "Easy": {},
    }[tier]
    available = defaultdict(dict)
    seeds = defaultdict(dict)
    for b, notes in timings.items():
        reference = nearest(b, reference_beats)
        if reference is None:
            raise ValueError("A shared timing candidate is missing from the budget reference.")
        on_draft = nearest(reference, draft) is not None
        if on_draft:
            matched_draft.add(b)
        lower = sum(weight * scores[target][reference] for target, weight in lower_weights.items())
        rank = scores[tier][reference]
        linked = 0.12 if b in level.get("linkAccentBeats", ()) else 0
        quality[b] = rank + lower + linked + (0.67 if tier == "Easy" else 0.10) * (b in kept)
        if tier == "Easy":
            quality[b] += 0.90 * (0.65 * rank + 0.40 * on_draft)
        elif tier != "Expert":
            quality[b] += 0.90 * on_draft
        trade_quality[b] = rank + linked + 0.12 * lower + 0.16 * on_draft + 0.13 * (b in kept) + 0.10 * mix[reference]
        available[phrase[b]][b] = sorted(notes, key=lambda n: (n["y"], abs(n["x"] - n["c"] - 1), n["id"]))
        if b in kept:
            seeds[phrase[b]][b] = kept[b]
    result = {}
    for area, original in seeds.items():
        first, last = min(original), max(original)
        candidates = {b: group for b, group in available[area].items() if first <= b <= last}
        fixed = {
            b: original[b]
            if b in fixed_beats or b in (first, last)
            else [n for n in group if n["id"] in locked]
            for b, group in original.items()
            if b in fixed_beats or b in (first, last) or any(n["id"] in locked for n in group)
        }
        result.update(
            phrase_selection(
                candidates, original, fixed, quality, times, MIN_GAP[tier], DISTINCT_GROWTH[tier]
            )
        )
    # A trade can shorten a long gap, but the incoming beat still has to clear both neighbors.
    locked_beats = fixed_beats | {b for group in seeds.values() for b in (min(group), max(group))}
    result = trade_phrase_notes(
        result, timings, trade_quality, times, phrase, locked, locked_beats, matched_draft, tier
    )
    selected = [n for _, notes in sorted(result.items()) for n in sorted(notes, key=lambda n: n["c"])]
    if len(selected) != len(seed) or not locked <= {n["id"] for n in selected}:
        raise ValueError("Shared timing selection changed the budget or lost a protected note.")
    return selected
