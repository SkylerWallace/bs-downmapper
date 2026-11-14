"""Deferred v1 pruning experiment. Not imported by the active generator."""

from analyze_standard_spreads import groups
from audit_placements import check_group


def prune_proposals(ns, sections, streams, profile):
    """Evaluate count granularity first. Never replace syncopation with a grid.

    v1 admits only collapsing aligned same-hand stacks: unique timing groups
    are protected. Unknown instrumental salience is a reason to preserve.
    """
    retained = list(ns)
    decisions = []
    for stream in streams:
        decisions.append(
            {
                "startBeat": stream["startBeat"],
                "operation": "preserve_regular_stream_timing",
                "consideredRhythmReductionFraction": 0.5,
                "accepted": False,
                "reason": "halving exceeds the per-section mild-pruning envelope; simplify geometry instead",
                "coverage": "Other coherent subsets are not yet evaluated. This is not proof that halving is the only musical option.",
            }
        )
    source_count = len(ns)
    for beat, g in groups(ns).items():
        sec = next(s for s in sections if s["startBeat"] <= beat < s["endBeatExclusive"])
        for c in [0, 1]:
            hand = [n for n in g if n["c"] == c]
            if len(hand) < 2:
                continue
            compatible = not any(f["rule"] in ["G02", "G03", "G04"] for f in check_group(hand))
            anchor = min(hand, key=lambda n: (n["y"], abs(n["x"] - (1 if c == 0 else 2))))
            removed = [n for n in hand if n is not anchor]
            in_section = [n for n in retained if sec["startBeat"] <= n["b"] < sec["endBeatExclusive"]]
            budget_ok = (
                len(in_section) - len(removed) >= sec["notes"] * (1 - profile["maximumSectionPruneFraction"])
                and len(retained) - len(removed) >= source_count * profile["minimumSourceNoteRatio"]
            )
            accept = compatible and budget_ok
            decisions.append(
                {
                    "beat": beat,
                    "hand": c,
                    "operation": "collapse_aligned_stack",
                    "removedIds": [n["id"] for n in removed],
                    "accepted": accept,
                    "reason": "retain timing and hand while reducing emphasis objects"
                    if accept
                    else "not aligned or exceeds mild-pruning envelope",
                }
            )
            if accept:
                ids = {n["id"] for n in removed}
                retained = [n for n in retained if n["id"] not in ids]
    assert set(groups(retained)) == set(groups(ns))
    return retained, decisions
