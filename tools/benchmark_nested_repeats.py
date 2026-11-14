"""Compare fixed-prefix repeat matching in whole-song held-out folds."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from statistics import mean

import numpy as np
from threadpoolctl import threadpool_limits

from analyze_standard_spreads import groups, load, nearest, write
from benchmark_rhythm_links import prepare_song, variation_scores
from chromapper_export import ENGINE, nested_reference_plan, profile_for
from nested_priority import select_prefix
from rhythm_inputs import timeline
from shared_timing import phrase_indices
from shared_timing_model import TIERS, condition, fit_shared_ranker
from timing_benchmark import score
from timing_link_scopes import editor_timing_links


ARMS = ("off", "on")


def evaluate_fold(key, songs):
    training = sorted(songs.keys() - {key})
    model = fit_shared_ranker(
        np.concatenate([songs[k]["training"] for k in training]),
        np.concatenate([songs[k]["labels"] for k in training]),
    )
    song = songs[key]
    reference = song["original"]
    notes = reference["notes"]
    timings = groups(notes)
    beats = list(timings)
    repeat = editor_timing_links(reference, timings, timeline(reference)["endBeat"], include_local=False)
    repeat_data = beats, repeat["sections"], repeat["markerLinks"]

    def predict(beats, features, fractions):
        return {
            tier: dict(zip(beats, model.predict_proba(condition(features, fraction))[:, 1]))
            for tier, fraction in fractions.items()
        }

    plans = {
        arm: nested_reference_plan(reference, score_reference=predict, repeat_matching=arm == "on")
        for arm in ARMS
    }
    off_order, off_budgets, off_locked = plans["off"]
    on_order, on_budgets, on_locked = plans["on"]
    assert off_budgets == on_budgets and off_locked == on_locked
    by_id = {note["id"]: note for note in notes}
    phrases = phrase_indices(reference, (note["b"] for note in notes))

    def position_budgets(order):
        return [(phrases[by_id[i]["b"]], by_id[i]["c"]) for i in order]

    # Matching the phrase and hand of every position preserves the counts of every prefix.
    assert len(set(on_order)) == len(notes) and set(off_order) == set(on_order)
    assert position_budgets(off_order) == position_budgets(on_order)
    assert all(off_order.index(i) == on_order.index(i) for i in off_locked)
    proposals = {tier: {} for tier in TIERS}
    for arm, (order, budgets, mandatory) in plans.items():
        parent = notes
        for tier in TIERS:
            result = select_prefix(
                reference, order, parent, budgets[tier], mandatory, (), source_is_reference=True
            )
            assert len(result) == budgets[tier]
            assert {n["id"] for n in result} <= {n["id"] for n in parent}
            proposals[tier][arm] = result
            kept = list(groups(result))
            parent = [n for n in notes if nearest(n["b"], kept) is not None]
        direct_easy = select_prefix(
            reference, order, notes, budgets["Easy"], mandatory, (), source_is_reference=True
        )
        assert direct_easy == proposals["Easy"][arm]

    rows = {}
    for tier, options in proposals.items():
        rows[tier] = {}
        authored = song["authored"][tier]
        baseline_ids = {n["id"] for n in options["off"]}
        for arm, selected in options.items():
            metrics = score(authored, selected)
            kept = list(groups(selected))
            disagreement = 0
            for group in repeat["markerLinks"]:
                count = sum(nearest(beats[i], kept) is not None for i in group)
                disagreement += count * (len(group) - count)
            rows[tier][arm] = {
                **{k: metrics[k] for k in ("beat_compliance", "authored_beats", "matched_beats", "generated_notes")},
                "extra_hits": len(metrics["extra_beats"]),
                "swaps": len({n["id"] for n in selected} - baseline_ids),
                "linked_pair_disagreements": disagreement,
                **variation_scores(authored, selected, repeat_data),
            }
    return {"training_keys": training, "source_sha256": song["source_sha256"], "tiers": rows}


def summarize(rows):
    summary = {}
    for arm in ARMS:
        totals = Counter()
        for row in rows:
            totals.update(row[arm])
        totals["beat_compliance"] = mean(row[arm]["beat_compliance"] for row in rows)
        summary[arm] = dict(totals)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--budget-details", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=4)
    args = parser.parse_args()
    keys = sorted(m["key"] for m in load(ENGINE / "maps/test-candidates.json")["maps"] if m["usage"] == "paired-reference")
    profiles = {tier: profile_for(tier, None) for tier in TIERS}

    def prepare(key):
        return prepare_song(args.source_root, args.budget_details, key, profiles, include_repeat_data=False)

    with threadpool_limits(limits=1), ThreadPoolExecutor(max_workers=args.workers) as pool:
        songs = dict(zip(keys, pool.map(prepare, keys)))
        print(f"Prepared {len(songs)} songs.", flush=True)
        folds = {}
        for key, result in zip(keys, pool.map(lambda key: evaluate_fold(key, songs), keys)):
            folds[key] = result
            write(args.output / "folds.json", folds)
            print(f"{key}: {sum(row['on']['swaps'] for row in result['tiers'].values())} swaps", flush=True)
    rows = {tier: [fold["tiers"][tier] for fold in folds.values()] for tier in TIERS}
    summary = {tier: summarize(values) for tier, values in rows.items()}
    summary["overall"] = summarize([row for values in rows.values() for row in values])
    write(args.output / "summary.json", summary)


if __name__ == "__main__":
    main()
