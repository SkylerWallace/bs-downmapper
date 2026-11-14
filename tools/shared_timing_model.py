"""Features and inference for the percentage-conditioned Standard timing ranker."""

from bisect import bisect_left
import warnings

import numpy as np
from joblib import load
from sklearn import __version__ as sklearn_version
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.exceptions import InconsistentVersionWarning
from threadpoolctl import threadpool_limits

from easy_ranking import FEATURES, source_features, source_mix
from rank_motifs import MOTIF_FEATURES, add_motif_features
from rhythm_inputs import best_guess_notes


TIERS = ("Expert", "Hard", "Normal", "Easy")
MODEL_ID = "shared-percentage-v1"
SCHEMA = (
    FEATURES
    + MOTIF_FEATURES
    + (
        "local_same_phase",
        "local_half_phase",
        "local_first_triplet",
        "local_second_triplet",
        "local_quarter_phase",
        "target_note_fraction",
    )
)


def reference_features(level, protected, sections, streams, easy_profile):
    """Extract beat evidence on the budget reference before any higher-tier pruning."""
    mix = source_mix(level)
    seed = best_guess_notes(level, sections, streams, easy_profile, protected)
    raw, grid, features = source_features(level, protected, mix, seed, easy_profile)
    phase = np.asarray(grid) - np.floor(np.asarray(grid) + 0.0001)
    local = []
    for i, beat in enumerate(grid):
        nearby = phase[bisect_left(grid, beat - 8) : bisect_left(grid, beat + 8)]
        distance = np.abs(nearby - phase[i])
        local.append(
            [
                np.mean(np.minimum(distance, 1 - distance) < 0.055),
                *(np.mean(np.abs(nearby - offset) < 0.055) for offset in (0.5, 1 / 3, 2 / 3, 0.25)),
            ]
        )
    return raw, mix, np.column_stack((add_motif_features(grid, features, FEATURES), local)), seed


def condition(features, fraction):
    if not 0 < fraction <= 1:
        raise ValueError("A shared timing budget must be between 0 and 100 percent.")
    return np.column_stack((features, np.full(len(features), fraction)))


def fit_shared_ranker(features, labels):
    model = HistGradientBoostingClassifier(
        max_iter=145, max_leaf_nodes=25, learning_rate=0.075, min_samples_leaf=45,
        l2_regularization=2.0, monotonic_cst=[0] * (len(SCHEMA) - 1) + [1], random_state=44,
    )
    return model.fit(features, labels)


def predict(path, beats, features, fractions):
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", InconsistentVersionWarning)
            asset = load(path)
    except (OSError, InconsistentVersionWarning) as error:
        raise ValueError(
            "Install the bundled shared model and the pinned requirements.txt dependencies."
        ) from error
    if asset.get("sklearn") != sklearn_version or tuple(asset.get("features", ())) != SCHEMA:
        raise ValueError(
            "The bundled shared timing model does not match this engine or its Python dependencies."
        )
    with threadpool_limits(limits=1):
        return {
            tier: dict(zip(beats, asset["ranker"].predict_proba(condition(features, fraction))[:, 1]))
            for tier, fraction in fractions.items()
        }
