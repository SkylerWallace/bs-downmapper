"""Train the opt-in shared ranker on local curated Standard spreads."""

import argparse
from pathlib import Path

from joblib import dump
import numpy as np
from sklearn import __version__ as sklearn_version
from threadpoolctl import threadpool_limits

from analyze_standard_spreads import groups, nearest
from beatmap_io import load, read_level, sha
from chromapper_export import ENGINE, automatic_timing_context, profile_for
from downmap import section_plan
from shared_timing_model import MODEL_ID, SCHEMA, TIERS, condition, fit_shared_ranker, reference_features


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument(
        "--budget-details", required=True, type=Path, help="Fixed-count report folders by map key."
    )
    parser.add_argument("--output", type=Path, default=ENGINE / "models" / "shared" / f"{MODEL_ID}.joblib")
    args = parser.parse_args()
    keys = [
        m["key"]
        for m in load(ENGINE / "maps/test-candidates.json")["maps"]
        if m["usage"] == "paired-reference"
    ]
    profile = profile_for("Easy", None)
    features, labels, references = [], [], {}
    with threadpool_limits(limits=1):
        for key in sorted(keys):
            folder = args.source_root / key
            original = read_level(folder, "ExpertPlus")
            level, protected = automatic_timing_context(original, profile)
            sections, streams = section_plan(level["notes"], level["bpm"], profile, level["clock"])
            beats, _, matrix, _ = reference_features(level, protected, sections, streams, profile)
            references[key] = {"ExpertPlus": sha(folder / original["filename"])}
            for tier in TIERS:
                authored = read_level(folder, tier)
                authored_beats = list(groups(authored["notes"]))
                details = args.budget_details / key
                report = details / ("fixed-Easy.json" if tier == "Easy" else f"{tier}.json")
                count = load(report)["generated_notes"]
                features.append(condition(matrix, count / len(original["notes"])))
                labels.extend(nearest(beat, authored_beats) is not None for beat in beats)
                references[key][tier] = {"sha256": sha(folder / authored["filename"]), "target_notes": count}
            print(f"Prepared {key}", flush=True)
        model = fit_shared_ranker(np.concatenate(features), labels)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    dump(
        {
            "id": MODEL_ID,
            "sklearn": sklearn_version,
            "features": SCHEMA,
            "ranker": model,
            "training_references": references,
        },
        args.output,
        compress=3,
    )
    print(f"Saved {args.output} using {len(keys)} curated songs.", flush=True)


if __name__ == "__main__":
    main()
