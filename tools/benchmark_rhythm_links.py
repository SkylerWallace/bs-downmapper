"""Compare the shared timing pruner with and without repeat preference, holding out whole songs."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from statistics import mean

import numpy as np
from threadpoolctl import threadpool_limits

from analyze_standard_spreads import groups, load, nearest, write
from bookmark_files import bookmark_positions
from beatmap_io import read_level, sha
from chromapper_export import ENGINE, automatic_timing_context, automatic_timings, profile_for
from downmap import section_plan
from rhythm_inputs import retain_timing_links, timeline
from shared_timing import easy_draft, phrase_indices, prefer_linked_repeats, select_shared_timings
from shared_timing_model import TIERS, condition, fit_shared_ranker, reference_features
from timing_benchmark import score
from timing_link_scopes import bookmark_windows, editor_timing_links
from timing_sections import chart_timing_sections, musical_grid


DETECTORS = ("legacy", "rhythm_only", "long_rhythm", "guided", "enhanced")
VARIANTS = ("baseline", *DETECTORS)


def prepare_song(source, budgets, key, profiles, *, include_repeat_data=True):
    folder = source / key
    original = read_level(folder, "ExpertPlus")
    reference, protected = automatic_timing_context(original, profiles["Easy"])
    sections, streams = section_plan(reference["notes"], reference["bpm"], profiles["Easy"], reference["clock"])
    beats, mix, features, easy_seed = reference_features(
        reference, protected, sections, streams, profiles["Easy"]
    )
    authored = {tier: read_level(folder, tier)["notes"] for tier in TIERS}
    training_features, labels = [], []
    for tier, notes in authored.items():
        path = budgets / key / ("fixed-Easy.json" if tier == "Easy" else f"{tier}.json")
        training_features.append(condition(features, load(path)["generated_notes"] / len(original["notes"])))
        timings = list(groups(notes))
        labels.extend(nearest(b, timings) is not None for b in beats)
    song = dict(
        original=original, reference=reference, protected=protected, sections=sections, streams=streams,
        beats=beats, mix=mix, features=features, authored=authored, easy_count=len(easy_seed),
        training=np.concatenate(training_features), labels=np.asarray(labels),
        source_sha256=sha(folder / original["filename"]),
    )
    if not include_repeat_data:
        return song
    timings = groups(reference["notes"])
    assert list(timings) == list(beats)
    end = timeline(reference)["endBeat"]
    to_grid, _ = musical_grid(reference)

    def editor_repeats(*, use_bookmarks, long_repeats):
        result = editor_timing_links(
            reference, timings, end, use_bookmarks=use_bookmarks, long_repeats=long_repeats,
            include_local=False, meter_aware=False
        )
        return result["sections"], result["markerLinks"]

    detectors = {
        "legacy": chart_timing_sections(reference, timings),
        "rhythm_only": chart_timing_sections(reference, timings, include_variations=True),
        "long_rhythm": editor_repeats(use_bookmarks=False, long_repeats=True),
        "guided": editor_repeats(use_bookmarks=True, long_repeats=False),
        "enhanced": editor_repeats(use_bookmarks=True, long_repeats=True),
    }
    raw_bookmarks = bool(bookmark_positions(reference["raw"], reference.get("folder"), reference.get("difficulty")))
    windows = bookmark_windows(reference["raw"], to_grid, to_grid(end))
    bookmark_status = "verified" if detectors["guided"] != detectors["rhythm_only"] else (
        "unusable" if raw_bookmarks else "absent"
    )
    detection = {
        name: {
            "ranges": len(ranges),
            "linked_markers": sum(map(len, links)),
            "long_ranges": sum(to_grid(r["end"]) - to_grid(r["start"]) >= 39 for r in ranges),
        }
        for name, (ranges, links) in detectors.items()
    }
    return dict(
        song,
        repeats={name: (beats, *result) for name, result in detectors.items()},
        detection=detection, bookmark_status=bookmark_status, bookmark_windows=len(windows),
    )


def baseline_spread(song, model, profiles):
    original = song["original"]
    total = len(original["notes"])
    fractions = {
        tier: (profiles[tier]["minimumSourceNoteRatio"] + profiles[tier]["maximumSourceNoteRatio"]) / 2
        for tier in TIERS
    }
    fractions["Easy"] = song["easy_count"] / total
    parent = original
    baseline = {}
    for tier in TIERS:
        profile = profiles[tier]
        level, protected = automatic_timing_context(parent, profile)
        seed, _ = automatic_timings(level, profile, protected, budget_reference=original)
        scores = {
            rank: dict(zip(song["beats"], model.predict_proba(condition(song["features"], fraction))[:, 1]))
            for rank, fraction in {**fractions, tier: len(seed) / total}.items()
        }
        draft = easy_draft(
            song["reference"], profiles["Easy"], song["protected"], song["sections"], song["streams"],
            scores["Easy"], song["mix"],
        )
        notes = select_shared_timings(level, seed, protected, tier, scores, song["mix"], draft, song["beats"])
        baseline[tier] = (level, protected, scores[tier], notes)
        parent = {**retain_timing_links(level, notes), "notes": notes}
    return baseline


def variation_scores(authored, proposed, repeat_data):
    reference, sections, links = repeat_data
    authored_beats, proposed_beats = list(groups(authored)), list(groups(proposed))
    counts = Counter()
    linked_indices = {index for link in links for index in link}
    for link in links:
        target = [nearest(reference[i], authored_beats) is not None for i in link]
        candidate = [nearest(reference[i], proposed_beats) is not None for i in link]
        if all(target) or not any(target):
            if all(target):
                counts["consistent_authored_hits"] += len(link)
                counts["consistent_authored_hits_retained"] += sum(candidate)
            continue
        counts["variation_groups"] += 1
        counts["variation_groups_exact"] += target == candidate
        counts["variation_hits"] += sum(target)
        counts["variation_hits_retained"] += sum(a and b for a, b in zip(target, candidate))
        counts["variation_omissions"] += target.count(False)
        counts["variation_omissions_preserved"] += sum(not a and not b for a, b in zip(target, candidate))
    for i, beat in enumerate(reference):
        if i in linked_indices or not any(s["start"] <= beat < s["end"] for s in sections):
            continue
        expected = nearest(beat, authored_beats) is not None
        actual = nearest(beat, proposed_beats) is not None
        counts["unmatched_authored_hits"] += expected
        counts["unmatched_authored_hits_retained"] += expected and actual
        counts["unmatched_extra_hits"] += actual and not expected
    return dict(counts)


def fold(key, songs, profiles):
    training_keys = sorted(songs.keys() - {key})
    model = fit_shared_ranker(
        np.concatenate([songs[k]["training"] for k in training_keys]),
        np.concatenate([songs[k]["labels"] for k in training_keys]),
    )
    song = songs[key]
    baseline = baseline_spread(song, model, profiles)
    rows = {}
    parents = {name: song["original"]["notes"] for name in VARIANTS}
    for index, tier in enumerate(TIERS):
        level, protected, scores, unlinked = baseline[tier]
        # Use the same unlinked quotas and protect baseline lower hits in every arm.
        lower_beats = {b for lower in TIERS[index + 1 :] for b in groups(baseline[lower][3])}
        authored = song["authored"][tier]
        unlinked_ids = {n["id"] for n in unlinked}
        rows[tier] = {}
        for name in VARIANTS:
            parent = parents[name]
            parent_ids = {n["id"] for n in parent}
            assert unlinked_ids <= parent_ids
            notes = unlinked if name == "baseline" else prefer_linked_repeats(
                {**level, "notes": parent}, unlinked, protected, tier, scores, song["repeats"][name],
                required_beats=lower_beats,
            )
            phrase = phrase_indices(level, (n["b"] for n in parent))

            def counts(notes):
                return Counter((phrase[note["b"]], note["c"]) for note in notes)

            assert counts(notes) == counts(unlinked)
            ids = {n["id"] for n in notes}
            assert ids <= parent_ids and (protected | level["anchorIds"]) <= ids
            parents[name] = notes
            result = score(authored, notes)
            # Every variant is scored on the same repeat groups, including the baseline.
            rows[tier][name] = {
                **{k: result[k] for k in ("beat_compliance", "authored_beats", "matched_beats", "generated_notes")},
                "extra_hits": len(result["extra_beats"]),
                "swaps": len(ids - unlinked_ids),
                **variation_scores(authored, notes, song["repeats"]["enhanced"]),
                "legacy_variations": variation_scores(authored, notes, song["repeats"]["legacy"]),
            }
    return {
        "training_keys": training_keys, "source_sha256": song["source_sha256"], "tiers": rows,
        "detection": song["detection"], "bookmark_status": song["bookmark_status"],
        "bookmark_windows": song["bookmark_windows"],
    }


def summarize(rows):
    report = {}
    for variant in VARIANTS:
        results = [row[variant] for row in rows]
        totals, legacy_variations = Counter(), Counter()
        for result in results:
            totals.update({k: v for k, v in result.items() if k != "legacy_variations"})
            legacy_variations.update(result["legacy_variations"])
        totals["beat_compliance"] = mean(result["beat_compliance"] for result in results)
        report[variant] = {**totals, "legacy_variations": dict(legacy_variations)}
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--budget-details", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--workers", type=int, default=4)
    args = parser.parse_args()
    keys = sorted(m["key"] for m in load(ENGINE / "maps/test-candidates.json")["maps"] if m["usage"] == "paired-reference")
    profiles = {tier: profile_for(tier, None) for tier in TIERS}
    with threadpool_limits(limits=1), ThreadPoolExecutor(max_workers=args.workers) as pool:
        songs = dict(zip(keys, pool.map(lambda key: prepare_song(args.source_root, args.budget_details, key, profiles), keys)))
        print(f"Prepared {len(songs)} songs.", flush=True)
        folds = {}
        for key, result in zip(keys, pool.map(lambda key: fold(key, songs, profiles), keys)):
            folds[key] = result
            write(args.output / "folds.json", folds)
            swaps = {name: sum(row[name]["swaps"] for row in result["tiers"].values()) for name in DETECTORS}
            print(f"{key} ({result['bookmark_status']}): {swaps}", flush=True)
    rows = {tier: [result["tiers"][tier] for result in folds.values()] for tier in TIERS}
    summary = {tier: summarize(tier_rows) for tier, tier_rows in rows.items()}
    summary["overall"] = summarize([row for tier_rows in rows.values() for row in tier_rows])
    summary["bookmark_status"] = {
        status: {
            "songs": len(subset),
            "metrics": summarize([row for result in subset for row in result["tiers"].values()]),
        }
        for status in ("verified", "unusable", "absent")
        if (subset := [result for result in folds.values() if result["bookmark_status"] == status])
    }
    write(args.output / "summary.json", summary)


if __name__ == "__main__":
    main()
