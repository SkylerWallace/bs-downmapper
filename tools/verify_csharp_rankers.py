"""Compare C# inference and selected timings against scikit-learn on curated maps."""

import argparse
from pathlib import Path
import struct
import subprocess

import numpy as np

from beatmap_io import load, read_level, write
from chromapper_export import nested_reference_plan
from export_csharp_rankers import export_rankers
from nested_priority import select_prefix
from shared_timing_model import condition, predict


class CompareScores:
    def __init__(self, executable, models):
        self.executable = executable
        self.models = models
        self.rows = 0
        self.max_error = 0

    def compare(self, model, features, expected):
        features = np.asarray(features, dtype="<f8", order="C")
        process = subprocess.run(
            ["dotnet", str(self.executable), str(self.models / f"{model}.bdt")],
            input=struct.pack("<i", len(features)) + features.tobytes(), capture_output=True,
        )
        if process.returncode:
            raise RuntimeError(process.stderr.decode("utf-8", errors="replace"))
        scores = np.frombuffer(process.stdout, dtype="<f8")
        np.testing.assert_allclose(scores, expected, rtol=0, atol=1e-14)
        self.rows += len(scores)
        self.max_error = max(self.max_error, float(np.max(np.abs(scores - expected), initial=0)))
        return scores


def verify_map(source, scorer, engine):
    reference = read_level(source, "ExpertPlus")
    asset = engine / "models/shared/shared-percentage-v1.joblib"

    def native_shared(beats, features, fractions):
        expected = predict(asset, beats, features, fractions)
        return {
            tier: dict(zip(beats, scorer.compare(
                "shared-percentage-v1-ranker", condition(features, fraction), list(expected[tier].values()),
            )))
            for tier, fraction in fractions.items()
        }

    old = nested_reference_plan(reference, score_reference=predict_for(asset))
    new = nested_reference_plan(reference, score_reference=native_shared)
    if old != new:
        raise AssertionError(f"{source.name}: nested timing priorities changed")
    order, budgets, protected = new
    results = {
        f"nested/{tier}": len(select_prefix(
            reference, order, reference["notes"], count, protected, (), source_is_reference=True,
        ))
        for tier, count in budgets.items()
    }

    return results


def predict_for(asset):
    return lambda beats, features, fractions: predict(asset, beats, features, fractions)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--maps", type=Path, required=True)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    engine = Path(__file__).resolve().parents[1]
    model_dir = args.output / "rankers"
    export_rankers(model_dir)
    scorer = CompareScores(args.probe, model_dir)
    catalog = load(engine / "maps/test-candidates.json")
    report = {"maps": {}}
    for item in catalog["maps"]:
        if item["usage"] != "paired-reference":
            continue
        key = item["key"]
        report["maps"][key] = verify_map(args.maps / key, scorer, engine)
        report.update(rows=scorer.rows, maxAbsoluteProbabilityError=scorer.max_error)
        write(args.output / "comparison.json", report)
        print(key, "matched", flush=True)


if __name__ == "__main__":
    main()
