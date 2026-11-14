"""Export trusted bundled scikit-learn rankers as little-endian tree models."""

import argparse
from pathlib import Path
import struct

from joblib import load
from sklearn import __version__ as sklearn_version
from sklearn.ensemble import HistGradientBoostingClassifier


MODELS = {
    "shared/shared-percentage-v1": ("ranker",),
}


def export_model(model, destination):
    if (
        not isinstance(model, HistGradientBoostingClassifier)
        or list(model.classes_) != [False, True]
        or any(len(stage) != 1 for stage in model._predictors)
    ):
        raise ValueError("Only binary histogram gradient boosting rankers are supported.")
    with destination.open("wb") as stream:
        stream.write(struct.pack("<4sidi", b"BDT1", model.n_features_in_,
                                 model._baseline_prediction.item(), len(model._predictors)))
        for (predictor,) in model._predictors:
            nodes = predictor.nodes
            stream.write(struct.pack("<i", len(nodes)))
            for index, node in enumerate(nodes):
                if node["is_leaf"]:
                    stream.write(struct.pack("<id", -1, float(node["value"])))
                    continue
                if node["is_categorical"] or not index < min(node["left"], node["right"]):
                    raise ValueError("Expected forward numeric tree branches.")
                stream.write(struct.pack(
                    "<idiiB", int(node["feature_idx"]), float(node["num_threshold"]),
                    int(node["left"]), int(node["right"]), int(node["missing_go_to_left"]),
                ))


def export_rankers(output):
    source = Path(__file__).resolve().parents[1] / "models"
    output.mkdir(parents=True, exist_ok=True)
    for asset_name, keys in MODELS.items():
        asset = load((source / asset_name).with_suffix(".joblib"))
        if asset["sklearn"] != sklearn_version:
            raise ValueError(f"Use the training environment to export {asset_name}.")
        for key in keys:
            export_model(asset[key], output / f"{Path(asset_name).name}-{key}.bdt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    export_rankers(parser.parse_args().output)
