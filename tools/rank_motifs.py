"""Source-only recurrence features and confidence pooling for Easy ranking."""

from bisect import bisect_right

import numpy as np
from scipy.spatial.distance import cdist

from rhythm_inputs import easy_pulse_rank


PEERS = 8
MOTIF_FEATURES = (
    "motif_support",
    "motif_nearest_distance",
    "motif_second_distance",
    "motif_third_distance",
    "motif_peer_notes",
    "motif_peer_two_hands",
    "motif_peer_mix_strength",
    "motif_peer_relative_mix",
    "motif_peer_baseline_kept",
)


def motif_peers(beats, features, feature_names, width):
    beats = np.asarray(beats)
    column = feature_names.index
    count = round(width * 24) + 1
    occupied = np.zeros((len(beats), count))
    chords = np.zeros_like(occupied)
    energy = np.zeros_like(occupied)
    for i, beat in enumerate(beats):
        first, last = np.searchsorted(beats, [beat - width - 0.01, beat + width + 0.01])
        slots = np.clip(np.round((beats[first:last] - beat) * 12).astype(int) + count // 2, 0, count - 1)
        occupied[i, slots] = 1
        chords[i, slots] = features[first:last, column("two_hands")]
        energy[i, slots] = features[first:last, column("mix_strength")]

    counts = occupied.sum(axis=1)
    tempo = features[:, column("seconds_per_beat")]
    strength = features[:, column("mix_strength")]
    indices = np.empty((len(beats), min(PEERS, len(beats))), dtype=int)
    distances = np.empty_like(indices, dtype=float)
    # Batching bounds the temporary distance matrices on long charts.
    for first in range(0, len(beats), 128):
        rows = slice(first, first + 128)
        shared = occupied[rows] @ occupied.T
        sizes = counts[rows, None] + counts[None, :]
        distance = 1 - shared / np.maximum(1, sizes - shared)
        distance += 0.16 * cdist(chords[rows], chords, "cityblock") / np.maximum(1, sizes)
        distance += 0.08 * cdist(energy[rows], energy, "cityblock") / np.maximum(1, sizes)
        distance += 0.2 * np.abs(strength[rows, None] - strength[None, :])
        gap = beats[rows, None] - beats[None, :]
        phase = np.abs(gap / 2 - np.round(gap / 2))
        distance[
            (np.abs(gap) < 2 * width) | (phase > 0.045) | (np.abs(tempo[rows, None] - tempo[None, :]) > 0.10)
        ] = np.inf
        peers = np.argsort(distance, axis=1)[:, :PEERS]
        indices[rows] = peers
        distances[rows] = np.take_along_axis(distance, peers, axis=1)
    return indices, distances


def add_motif_features(beats, features, feature_names):
    indices, distances = motif_peers(beats, features, feature_names, width=1)
    distances = np.minimum(distances, 1)
    weights = np.maximum(0, 1 - distances / 0.35)
    support = weights.sum(axis=1)
    columns = [
        feature_names.index(name)
        for name in ("source_notes", "two_hands", "mix_strength", "mix_above_local_median", "baseline_kept")
    ]
    peer_means = (features[:, columns][indices] * weights[:, :, None]).sum(axis=1)
    peer_means /= np.maximum(support[:, None], 1e-8)
    closest = distances[:, :3]
    closest = np.pad(closest, ((0, 0), (0, 3 - closest.shape[1])), constant_values=1)
    return np.column_stack((features, support, closest, peer_means))


def pool_motif_confidence(ranking, peers):
    indices, distances = peers
    scores = np.asarray(ranking)
    weights = np.maximum(0, 1 - distances / 0.18)
    support = weights.sum(axis=1)
    average = (weights * scores[indices]).sum(axis=1) / np.maximum(support, 1e-8)
    influence = 0.5 * np.minimum(1, support / 1.8)
    return ((1 - influence) * scores + influence * average).tolist()


def guard_motif_confidence(beats, motif, boosted, mix):
    """Resolve conflicting pickup and whole-beat scores with local evidence."""
    ranks = [easy_pulse_rank(beat) for beat in beats]
    disputed = set()
    for i, beat in enumerate(beats):
        if ranks[i] < 2:
            continue
        for j in range(i + 1, bisect_right(beats, beat + 0.55, i + 1)):
            if (
                ranks[j] < 2
                and motif[i] > motif[j]
                and boosted[j] - boosted[i] >= 0.12
                and mix[j] >= 0.55
                and mix[j] + 0.23 >= mix[i]
            ):
                disputed.update((i, j))
    timing = [boosted[i] if i in disputed else score for i, score in enumerate(motif)]
    # Budget transfers use their own confidence. A strong onset can recover
    # some confidence lost to the motif model before entering the transfer search.
    budget = [
        score + 0.5 * attack * max(0, base - score) if attack >= 0.55 else score
        for score, base, attack in zip(motif, boosted, mix)
    ]
    return timing, budget
