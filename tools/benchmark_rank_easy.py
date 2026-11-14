"""Train and export leave-one-song-out Standard Easy timing rankings."""

import argparse
from bisect import bisect_left, bisect_right
from concurrent.futures import ThreadPoolExecutor, as_completed
import csv
from pathlib import Path
from statistics import mean

from joblib import dump
import numpy as np
from scipy.stats import rankdata
from sklearn import __version__ as sklearn_version
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler

from analyze_standard_spreads import TOL, groups
from audio_onsets import read_onset_scores
from beatmap_io import load, read_level, sha
from timing_benchmark import ROOT, aggregate, atomic_json, run_song, score
from chromapper_export import automatic_timing_context, automatic_timings, profile_for
from easy_ranking import FEATURES, source_features
from rank_motifs import (
    MOTIF_FEATURES,
    add_motif_features,
    guard_motif_confidence,
    motif_peers,
    pool_motif_confidence,
)
from rhythm_inputs import EASY_BOOSTED_RANK_WEIGHT, EASY_MOTIF_RANK_WEIGHT, EASY_RANK_WEIGHT, apply_merges


PAIR_RADIUS = 2.0
REGULARIZATION_C = 0.1
BOOSTED_PARAMETERS = dict(
    max_iter=115,
    max_leaf_nodes=15,
    learning_rate=0.07,
    l2_regularization=5,
    min_samples_leaf=35,
    early_stopping=False,
    random_state=42,
)


def timing_labels(raw, authored):
    targets = list(groups(authored))
    kept = []
    for beat in raw:
        index = bisect_left(targets, beat - TOL - 1e-10)
        kept.append(index < len(targets) and targets[index] <= beat + TOL + 1e-10)
    return kept


def preferences(raw, beats, features, authored):
    kept = timing_labels(raw, authored)
    pairs = []
    for i, beat in enumerate(beats):
        if kept[i]:
            first = bisect_left(beats, beat - PAIR_RADIUS)
            last = bisect_right(beats, beat + PAIR_RADIUS)
            pairs.extend(features[i] - features[j] for j in range(first, last) if not kept[j])
    if not pairs:
        raise ValueError("A training chart has no contrasting timings within the pair radius.")
    return np.asarray(pairs)


def train_fold(held_out, features, pairs):
    training = sorted(pairs.keys() - {held_out})
    scaler = StandardScaler().fit(np.concatenate([features[key] for key in training]))
    differences = np.concatenate([pairs[key] for key in training]) / scaler.scale_
    # Balance the contribution of whole songs and present each pair in both orders.
    weights = np.concatenate([np.full(len(pairs[key]), 1 / len(pairs[key])) for key in training])
    weights /= weights.mean()
    model = LogisticRegression(C=REGULARIZATION_C, fit_intercept=False, max_iter=500)
    model.fit(
        np.concatenate((differences, -differences)),
        np.repeat((1, 0), len(differences)),
        sample_weight=np.tile(weights, 2),
    )
    logits = model.decision_function(scaler.transform(features[held_out]))
    ranking = (rankdata(logits) - 1) / max(1, len(logits) - 1)
    record = {
        "held_out": held_out,
        "training_songs": training,
        "pairs_per_song": {key: len(pairs[key]) for key in training},
        "features": FEATURES,
        "feature_mean": scaler.mean_.tolist(),
        "feature_scale": scaler.scale_.tolist(),
        "coefficients": model.coef_[0].tolist(),
        "sklearn": sklearn_version,
        "pair_radius_beats": PAIR_RADIUS,
        "regularization_c": REGULARIZATION_C,
        "solver_rank_weight": EASY_RANK_WEIGHT,
    }
    return ranking.tolist(), record


def fit_boosted(features, labels, training):
    weights = np.concatenate([np.full(len(labels[key]), 1 / len(labels[key])) for key in training])
    weights /= weights.mean()
    model = HistGradientBoostingClassifier(**BOOSTED_PARAMETERS)
    model.fit(
        np.concatenate([features[key] for key in training]),
        np.concatenate([labels[key] for key in training]),
        sample_weight=weights,
    )
    return model


def train_boosted_fold(held_out, features, labels, feature_names=FEATURES):
    training = sorted(labels.keys() - {held_out})
    model = fit_boosted(features, labels, training)
    # Preserve confidence differences between closely ranked beats.
    ranking = model.predict_proba(features[held_out])[:, 1]
    record = {
        "held_out": held_out,
        "training_songs": training,
        "candidates_per_song": {key: len(labels[key]) for key in training},
        "features": feature_names,
        "sklearn": sklearn_version,
        "parameters": BOOSTED_PARAMETERS,
        "solver_rank_weight": EASY_BOOSTED_RANK_WEIGHT,
    }
    return ranking.tolist(), record, model


def prepare_song(key, source, onsets, baseline, profile, model="linear", *, without_audio=False):
    level, protected = automatic_timing_context(read_level(source, "ExpertPlus"), profile)
    cache = onsets / f"{key}.json"
    mix = read_onset_scores(level, cache, "mix")
    retained, merges = automatic_timings(level, profile, protected, onset_cache=cache)
    selected = apply_merges(level, retained, merges)
    reference = read_level(source, "Easy")
    authored = reference["notes"]
    baseline_result = load(baseline / "mix/details" / key / "result.json")
    proposed = score(authored, selected)
    for field in ("authored_timing_groups", "generated_timing_groups"):
        if [list(item) for item in proposed[field]] != baseline_result[field]:
            raise ValueError(f"{key} does not reproduce the saved mix-only baseline.")
    if without_audio:
        retained, merges = automatic_timings(level, profile, protected)
        selected = apply_merges(level, retained, merges)
        mix = dict.fromkeys(mix, 0.0)
    raw, beats, features = source_features(level, protected, mix, selected, profile)
    targets = (
        preferences(raw, beats, features, authored) if model == "linear" else timing_labels(raw, authored)
    )
    return raw, beats, features, targets, baseline_result, sha(source / reference["filename"])


def export_fold(key, source, cache, output, baseline, profile, mode="rank"):
    level, protected = automatic_timing_context(read_level(source, "ExpertPlus"), profile)
    retained, merges = automatic_timings(level, profile, protected, onset_cache=cache, onset_mode=mode)
    notes = apply_merges(level, retained, merges)
    budget = baseline["generated_notes"]
    if len(notes) != budget or {note["b"] for note in notes} - {note["b"] for note in level["notes"]}:
        raise ValueError(f"{key} ranked pruning changed the note budget or introduced a new timing.")
    timings = [(beat, len(group)) for beat, group in groups(notes).items()]
    details = output / "details" / key
    details.mkdir(parents=True, exist_ok=True)
    result = run_song(output, key, source, timings, onset_cache=cache, onset_mode=mode)
    result.update(expected_note_count=budget, source_timings_verified=True, rank_cache_sha256=sha(cache))
    atomic_json(details / "result.json", result)
    print(
        f"{key} | {result['status']} | {baseline['matched_beats']} baseline -> "
        f"{result.get('matched_beats', '-')} ranked matches | {budget} notes",
        flush=True,
    )
    return result


def publish(output, rows, baselines, keys):
    paired = [row for row in rows if row.get("saved_timing_verified")]
    previous = [baselines[row["song_id"]] for row in paired]

    def summarize(results):
        return {
            **aggregate(results),
            "mean_map_beat_compliance": mean(r["beat_compliance"] for r in results) if results else None,
            "extra_beat_count": sum(len(r["extra_beats"]) for r in results),
        }

    base, ranked = summarize(previous), summarize(paired)
    full_baseline = summarize([baselines[key] for key in keys])
    delta = ranked["mean_map_beat_compliance"] - base["mean_map_beat_compliance"] if paired else None
    atomic_json(
        output / "summary.json",
        {
            "validation": "leave-one-song-out",
            "requested_folds": len(keys),
            "completed_folds": len(rows),
            "paired_folds": len(paired),
            "exported": sum(row["status"].startswith("exported") for row in rows),
            "baseline_all_requested": full_baseline,
            "baseline_paired": base,
            "ranked_paired": ranked,
            "mean_map_beat_compliance_delta": delta,
        },
    )
    with (output / "comparison.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(
            (
                "song_id",
                "title",
                "status",
                "note_budget",
                "ranked_notes",
                "baseline_matches",
                "ranked_matches",
                "baseline_beat_compliance",
                "ranked_beat_compliance",
                "baseline_extra_beats",
                "ranked_extra_beats",
                "error",
            )
        )
        for row in sorted(rows, key=lambda row: row["song_id"]):
            key = row["song_id"]
            original = baselines[key]
            writer.writerow(
                (
                    key,
                    original["title"],
                    row["status"],
                    row.get("expected_note_count"),
                    row.get("generated_notes"),
                    original["matched_beats"],
                    row.get("matched_beats"),
                    original["beat_compliance"],
                    row.get("beat_compliance"),
                    len(original["extra_beats"]),
                    len(row["extra_beats"]) if "extra_beats" in row else None,
                    row.get("error") or row.get("retry_error") or row.get("stock_error"),
                )
            )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("maps", "onsets", "baseline", "output"):
        parser.add_argument(f"--{name}", required=True, type=Path)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument(
        "--model", choices=("linear", "boosted", "motifs", "motifs-guarded"), default="linear"
    )
    parser.add_argument(
        "--phrase-budget", action="store_true", help="Enable ranked transfers between phrases."
    )
    parser.add_argument(
        "--phrase-search", action="store_true", help="Replan ranked singles and doubles within phrases."
    )
    parser.add_argument(
        "--recurring-figures", action="store_true", help="Preserve supported recurring source figures."
    )
    parser.add_argument(
        "--local-pulse", action="store_true", help="Refine attacks using local tempo and subdivision."
    )
    parser.add_argument(
        "--without-audio", action="store_true", help="Train and run Local Pulse with zero onset evidence."
    )
    parser.add_argument(
        "--compare-to", type=Path, help="Compare with an existing boosted or phrase allocation run."
    )
    parser.add_argument(
        "--song", action="append", help="Export only these folds, using the same training pool."
    )
    args = parser.parse_args()
    songs = {p.name: p.resolve() for p in sorted(args.maps.iterdir()) if (p / "Info.dat").is_file()}
    keys = list(dict.fromkeys(args.song or songs))
    if args.workers < 1 or len(songs) < 2 or set(keys) - songs.keys():
        parser.error("Use positive workers and select held-out songs from a pool of at least two maps.")
    if any(not song.is_relative_to(ROOT) for song in songs.values()):
        parser.error("Benchmark maps must be inside the project workspace.")
    if args.phrase_budget and args.model == "linear":
        parser.error("--phrase-budget requires a boosted or motif model.")
    if args.phrase_search and (args.model != "motifs-guarded" or not args.phrase_budget):
        parser.error("--phrase-search requires --model motifs-guarded --phrase-budget.")
    if args.recurring_figures and not args.phrase_search:
        parser.error("--recurring-figures requires --phrase-search.")
    if args.local_pulse and not args.recurring_figures:
        parser.error("--local-pulse requires --recurring-figures.")
    if args.without_audio and (not args.local_pulse or args.compare_to is None):
        parser.error("--without-audio requires --local-pulse and an audio-assisted --compare-to run.")
    if args.compare_to and not args.phrase_budget:
        parser.error("--compare-to is for a boosted phrase allocation benchmark.")
    output = args.output.resolve()
    profile = profile_for("Easy", None)
    output.mkdir(parents=True, exist_ok=True)
    (output / "folds").mkdir(exist_ok=True)
    raw, beats, features, targets, baselines, reference_hashes = {}, {}, {}, {}, {}, {}
    for key, song in songs.items():
        raw[key], beats[key], features[key], targets[key], baselines[key], reference_hashes[key] = (
            prepare_song(
                key, song, args.onsets, args.baseline, profile, args.model, without_audio=args.without_audio
            )
        )
        print(f"{key} | verified baseline, prepared {len(targets[key])} training examples", flush=True)
    caches = {}
    score_key, method = {
        "linear": ("rank", "pairwise-logistic-v1"),
        "boosted": ("rank-boosted", "histogram-boosted-v2"),
        "motifs": ("rank-motifs", "histogram-boosted-motifs-v1"),
        "motifs-guarded": ("rank-motifs", "histogram-boosted-motifs-guarded-v2"),
    }[args.model]
    mode = score_key + "-budget" if args.phrase_budget else score_key
    if args.local_pulse:
        mode = "rank-motifs-pulse-budget"
    elif args.recurring_figures:
        mode = "rank-motifs-figures-budget"
    elif args.phrase_search:
        mode = "rank-motifs-structured-budget"
    motif_model = args.model in ("motifs", "motifs-guarded")
    guarded = args.model == "motifs-guarded"
    training_features = (
        {key: add_motif_features(beats[key], values, FEATURES) for key, values in features.items()}
        if motif_model
        else features
    )
    feature_names = FEATURES + MOTIF_FEATURES if motif_model else FEATURES
    (output / "rank-weights").mkdir(exist_ok=True)
    for key in keys:
        cache = load(args.onsets / f"{key}.json")
        if args.without_audio:
            cache["scores"]["mix"] = [[beat, 0.0] for beat in raw[key]]
            cache["analysis"]["audio_ablation"] = "no-audio-training-or-inference"
        budget_ranking = None
        unguarded_ranking = None
        if args.model != "linear":
            ranking, record, model = train_boosted_fold(key, training_features, targets, feature_names)
            if motif_model:
                peers = motif_peers(beats[key], features[key], FEATURES, width=2)
                ranking = pool_motif_confidence(ranking, peers)
                record.update(
                    solver_rank_weight=EASY_MOTIF_RANK_WEIGHT,
                    timing_radius_beats=1.5,
                    motif_feature_radius_beats=1,
                    motif_pooling_radius_beats=2,
                )
                if guarded:
                    boosted, _, fallback = train_boosted_fold(key, features, targets)
                    fallback_path = output / "folds" / f"{key}-fallback.joblib"
                    dump(fallback, fallback_path, compress=3)
                    record["fallback_sha256"] = sha(fallback_path)
                    mix = dict(cache["scores"]["mix"])
                    unguarded_ranking = ranking
                    ranking, budget_ranking = guard_motif_confidence(
                        beats[key], ranking, boosted, [mix[beat] for beat in raw[key]]
                    )
            model_path = output / "folds" / f"{key}.joblib"
            dump(model, model_path, compress=3)
            record["model_sha256"] = sha(model_path)
        else:
            ranking, record = train_fold(key, features, targets)
        record["training_easy_sha256"] = {k: reference_hashes[k] for k in record["training_songs"]}
        record["evaluation_easy_sha256"] = reference_hashes[key]
        atomic_json(output / "folds" / f"{key}.json", record)
        cache["analysis"]["ranking"] = f"leave-one-song-out-{method}"
        cache["analysis"]["phrase_budget"] = args.phrase_budget
        if args.phrase_budget:
            cache["analysis"]["phrase_budget_rule"] = "neighbor-confidence-v2"
        if args.phrase_search:
            cache["analysis"]["phrase_search"] = "ranked-sequences-v1"
        if args.recurring_figures:
            cache["analysis"]["recurring_figures"] = "spaced-entrance-v2"
        if args.local_pulse:
            cache["analysis"]["local_pulse"] = "tempo-and-subdivision-v1"
        cache["scores"] = {
            "mix": cache["scores"]["mix"],
            score_key: [list(pair) for pair in zip(raw[key], ranking)],
        }
        if budget_ranking is not None and args.phrase_budget:
            cache["scores"]["rank-budget"] = [list(pair) for pair in zip(raw[key], budget_ranking)]
        if args.compare_to:
            prior_cache = load(args.compare_to / "rank-weights" / f"{key}.json")
            prior = load(args.compare_to / "details" / key / "result.json")
            expected_scores = ("mix",) if motif_model else ("mix", score_key)
            if args.phrase_search:
                expected_scores = ("mix", score_key, "rank-budget")
            if args.without_audio:
                if prior_cache["analysis"].get("local_pulse") != "tempo-and-subdivision-v1" or prior_cache[
                    "analysis"
                ].get("audio_ablation"):
                    raise ValueError(
                        "--without-audio needs the matching audio-assisted Local Pulse benchmark."
                    )
                expected_scores = ()
            if (
                prior_cache["source"] != cache["source"]
                or any(prior_cache["scores"].get(name) != cache["scores"][name] for name in expected_scores)
                or (
                    unguarded_ranking is not None
                    and not args.phrase_search
                    and prior_cache["scores"].get(score_key)
                    != [list(pair) for pair in zip(raw[key], unguarded_ranking)]
                )
                or not prior.get("saved_timing_verified")
                or not prior["status"].startswith("exported")
                or prior["authored_timing_groups"] != baselines[key]["authored_timing_groups"]
                or prior["generated_notes"] != baselines[key]["generated_notes"]
            ):
                raise ValueError(f"{key} does not match the required baseline inputs or note budget.")
            baselines[key] = prior
        caches[key] = output / "rank-weights" / f"{key}.json"
        atomic_json(caches[key], cache)
        print(f"{key} | trained on {len(record['training_songs'])} other songs", flush=True)
    rows = []
    with ThreadPoolExecutor(max_workers=args.workers) as workers:
        pending = {
            workers.submit(
                export_fold, key, songs[key], caches[key], output, baselines[key], profile, mode
            ): key
            for key in keys
        }
        for future in as_completed(pending):
            key = pending[future]
            try:
                rows.append(future.result())
            except (ValueError, KeyError, OSError) as error:
                rows.append({"song_id": key, "status": "failed", "error": str(error)})
                print(f"{key} | failed | {error}", flush=True)
            publish(output, rows, baselines, keys)
    print(f"Saved {len(rows)} fold results to {output}", flush=True)
    if not all(row.get("saved_timing_verified") for row in rows):
        raise SystemExit("Some folds could not be verified. See comparison.csv.")


if __name__ == "__main__":
    main()
