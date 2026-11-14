"""A shared, monotone timing order built from the unpruned Standard reference."""

from collections import Counter, defaultdict
import math

from analyze_standard_spreads import groups, nearest
from shared_timing import phrase_indices, prefer_linked_repeats


MODEL_ID = "nested-priority-v1"


def priority_order(reference, plans, scores, mandatory):
    """Fold existing difficulty proposals into one order, growing from Easy to Expert+."""
    notes = {note["id"]: note for note in reference["notes"]}
    phrases = phrase_indices(reference, (note["b"] for note in notes.values()))
    beat_by_id = {note["id"]: beat for beat, members in groups(notes.values()).items() for note in members}
    order = sorted(mandatory, key=lambda i: (notes[i]["b"], i))
    used = set(order)
    budgets = {}
    for tier, proposal in plans.items():
        preferred = {note["id"] for note in proposal}
        target = max(len(order), len(preferred))
        budgets[tier] = target
        remaining = target - len(order)
        if not remaining:
            continue
        wanted = Counter(phrases[notes[i]["b"]] for i in preferred)
        present = Counter(phrases[notes[i]["b"]] for i in order)
        pools = defaultdict(list)
        for i in notes.keys() - used:
            pools[phrases[notes[i]["b"]]].append(i)
        additions = []
        kept_beats = {beat_by_id[i] for i in order}
        for phrase, pool in pools.items():
            pool.sort(key=lambda i: (
                i not in preferred, beat_by_id[i] in kept_beats, -scores[tier][beat_by_id[i]],
                notes[i]["b"], i,
            ))
            if tier == "Normal":
                # Easy may already have spent part of Normal's budget on doubles.
                # Fill out the rhythm before adding another hit at a kept time.
                seen = set(kept_beats)
                singles, additional = [], []
                for i in pool:
                    beat = beat_by_id[i]
                    (additional if beat in seen else singles).append(i)
                    seen.add(beat)
                pool = singles + additional
            deficit = max(0, wanted[phrase] - present[phrase])
            additions.extend(
                (index >= deficit, (index + 1) / max(1, deficit), i not in preferred,
                 -scores[tier][beat_by_id[i]], i)
                for index, i in enumerate(pool)
            )
        incoming = [row[-1] for row in sorted(additions)[:remaining]]
        order.extend(incoming)
        used.update(incoming)
    order.extend(sorted(notes.keys() - used, key=lambda i: (-scores["Expert"][beat_by_id[i]], notes[i]["b"], i)))
    return order, budgets


def match_repeat_priorities(reference, order, budgets, scores, contexts, mandatory, repeat_data):
    """Exchange positions across one budget cutoff, keeping one order for all budgets."""
    if not repeat_data[2]:
        return order
    order = list(order)
    notes = {note["id"]: note for note in reference["notes"]}
    beat_by_id = {note["id"]: beat for beat, members in groups(notes.values()).items() for note in members}
    positions = {note_id: index for index, note_id in enumerate(order)}
    tiers = list(budgets)
    lower = 0
    for index, tier in enumerate(tiers):
        cutoff = budgets[tier]
        upper = budgets[tiers[index + 1]] if index + 1 < len(tiers) else len(order)
        quality = scores[tier]

        def can_swap(donor, incoming):
            old, new = donor["id"], incoming["id"]
            # Strong local preferences can mark an intentional rhythmic variation.
            return (
                lower <= positions[old] < cutoff <= positions[new] < upper
                and quality[beat_by_id[old]] - quality[beat_by_id[new]] <= .05
            )

        def exchange(donor, incoming):
            old, new = donor["id"], incoming["id"]
            a, b = positions[old], positions[new]
            order[a], order[b] = new, old
            positions[old], positions[new] = b, a

        if lower < cutoff < upper:
            prefer_linked_repeats(
                contexts[tier][0], [notes[i] for i in order[:cutoff]], mandatory, tier, quality, repeat_data,
                can_swap=can_swap, on_swap=exchange,
            )
        lower = cutoff
    return order


def select_prefix(reference, order, eligible, budget, protected, required_beats, *, source_is_reference):
    """Filter one stable order by saved parents and include mandatory saved lower beats."""
    reference_groups = groups(reference["notes"])
    reference_beats = list(reference_groups)
    eligible_groups = groups(eligible)
    eligible_beats = list(eligible_groups)
    by_id = {note["id"]: note for note in eligible}
    if source_is_reference:
        available = [by_id[i] for i in order if i in by_id]
    else:
        # Explicit timing charts retain their own hands and doubles. Order each
        # group by the first reference priority of the same hand when available.
        positions = {i: position for position, i in enumerate(order)}
        hand_positions = {}
        for beat, members in reference_groups.items():
            hand_positions[beat] = {
                hand: sorted(positions[note["id"]] for note in members if note["c"] == hand)
                for hand in (0, 1)
            }
        ranked = []
        for beat, members in eligible_groups.items():
            reference_beat = nearest(beat, reference_beats)
            hand_order = hand_positions.get(reference_beat, {0: [], 1: []})
            seen = Counter()
            for note in sorted(members, key=lambda n: (n["c"], n["id"])):
                hand = note["c"]
                index = seen[hand]
                options = hand_order[hand]
                rank = options[index] if index < len(options) else (
                    min(positions[n["id"]] for n in reference_groups[reference_beat])
                    if reference_beat is not None and index == 0 else len(order)
                )
                ranked.append((rank, beat, index, note["id"], note))
                seen[hand] += 1
        available = [row[-1] for row in sorted(ranked)]
    by_beat = {note["id"]: beat for beat, members in eligible_groups.items() for note in members}
    first_at = {}
    for note in available:
        first_at.setdefault(by_beat[note["id"]], note["id"])
    required = {nearest(beat, eligible_beats) for beat in required_beats}
    if None in required:
        raise ValueError("A saved lower difficulty needs a timing missing from the eligible higher charts.")
    mandatory = set(protected) | {first_at[beat] for beat in required}
    if not mandatory <= by_id.keys():
        raise ValueError("A protected timing is missing from the eligible higher charts.")
    remaining = max(0, budget - len(mandatory))
    selected = [note for note in available if note["id"] in mandatory]
    selected.extend([note for note in available if note["id"] not in mandatory][:remaining])
    if not selected:
        raise ValueError("The nested timing model found no eligible notes.")
    return sorted(selected, key=lambda n: (n["b"], n["c"], n["id"]))


def fraction_budget(reference, fraction):
    return max(1, math.floor(fraction * len(reference["notes"]) + .5))
