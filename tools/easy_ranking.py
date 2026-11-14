"""Shared features and inference for the Easy timing ranker."""

from bisect import bisect_left, bisect_right

import numpy as np

from analyze_standard_spreads import groups
from audio_onsets import audio_file, mix_timing_scores
from rank_motifs import (
    MOTIF_FEATURES,
    add_motif_features,
    guard_motif_confidence,
    motif_peers,
    pool_motif_confidence,
)
from rhythm_inputs import easy_accent_grid, easy_phrase_cue_weights, easy_pulse_rank


FEATURES = (
    "grid_even",
    "grid_odd",
    "grid_half",
    "grid_offbeat",
    "second_in_bar",
    "third_in_bar",
    "fourth_in_bar",
    "seconds_per_beat",
    "beats_before",
    "beats_after",
    "seconds_before",
    "seconds_after",
    "timings_before",
    "timings_after",
    "timings_in_context",
    "phrase_progress",
    "song_progress",
    "source_notes",
    "two_hands",
    "hand_change",
    "first_chord_in_pair",
    "protected",
    "cue_weight",
    "accent_grid",
    "repeat_count",
    "mix_strength",
    "mix_above_local_median",
    "baseline_kept",
    "baseline_before",
    "baseline_after",
    "baseline_local_count",
)


def source_features(level, protected, mix, baseline, profile):
    timings = list(groups(level["notes"]).items())
    raw = [beat for beat, _ in timings]
    times = [level["clock"].seconds(beat) for beat in raw]
    grid = level.get("legacyGridClock")
    beats = [grid.beat_at_seconds(time) for time in times] if grid else raw
    pulse = profile["automaticTimingPulseSeconds"]
    starts = [0] + [
        i
        for i in range(1, len(raw))
        if raw[i] - raw[i - 1] >= 2.5 and times[i] - times[i - 1] >= 1.25 * pulse
    ]
    cues = easy_phrase_cue_weights(timings, starts)
    accents = easy_accent_grid(timings, beats, times, starts, pulse)
    hands = [{note["c"] for note in group} for _, group in timings]
    baseline_beats = set(groups(baseline))
    kept = [beat for beat, source in zip(beats, raw) if source in baseline_beats]
    protected = protected | level["anchorIds"]
    result = []
    for start, end in zip(starts, starts[1:] + [len(raw)]):
        duration = max(0.001, times[end - 1] - times[start])
        for i in range(start, end):
            beat = beats[i]
            before = min(4, beat - beats[i - 1]) if i > start else 4
            after = min(4, beats[i + 1] - beat) if i + 1 < end else 4
            left = bisect_left(beats, beat - 2, start, i)
            right = bisect_right(beats, beat + 2, i, end)
            left_context = bisect_left(beats, beat - 8, start, i)
            right_context = bisect_right(beats, beat + 8, i, end)
            repeats = 0
            for offset in (-16, -8, -4, 4, 8, 16):
                expected = beat + offset
                j = bisect_left(beats, expected - 0.025, start + 1, end - 1) if end - start > 2 else end
                if j < end - 1 and beats[j] <= expected + 0.025:
                    repeats += (
                        abs(before - min(4, beats[j] - beats[j - 1])) <= 0.125
                        and abs(after - min(4, beats[j + 1] - beats[j])) <= 0.125
                        and len(hands[i]) == len(hands[j])
                    )
            previous = bisect_left(kept, beat)
            following = bisect_right(kept, beat)
            rank = easy_pulse_rank(beat)
            result.append(
                [
                    *(rank == p for p in range(4)),
                    *(round(beat) % 4 == p and abs(beat - round(beat)) <= 0.04 for p in range(1, 4)),
                    level["clock"].gap(raw[i], raw[i] + 1) if not grid else grid.gap(beat, beat + 1),
                    before,
                    after,
                    min(3, times[i] - times[i - 1]) if i > start else 3,
                    min(3, times[i + 1] - times[i]) if i + 1 < end else 3,
                    i - left,
                    right - i - 1,
                    right_context - left_context - 1,
                    (times[i] - times[start]) / duration,
                    (times[i] - times[0]) / max(0.001, times[-1] - times[0]),
                    len(timings[i][1]),
                    len(hands[i]) == 2,
                    i > start and len(hands[i]) == len(hands[i - 1]) == 1 and hands[i] != hands[i - 1],
                    i + 1 < end
                    and len(hands[i]) == len(hands[i + 1]) == 2
                    and before >= 1.2
                    and after <= 1.0001,
                    any(note["id"] in protected for note in timings[i][1]),
                    cues.get(i, 0),
                    accents.get(i, 0),
                    repeats,
                    mix[raw[i]],
                    mix[raw[i]] - np.median([mix[b] for b in raw[left_context:right_context]]),
                    raw[i] in baseline_beats,
                    min(4, beat - kept[previous - 1]) if previous else 4,
                    min(4, kept[following] - beat) if following < len(kept) else 4,
                    bisect_right(kept, beat + 4) - bisect_left(kept, beat - 4),
                ]
            )
    return raw, beats, np.asarray(result, dtype=float)


def source_mix(level):
    import soundfile as sf

    audio, sample_rate = sf.read(audio_file(level), dtype="float32", always_2d=True)
    raw, _, scores = mix_timing_scores(level, audio, sample_rate)
    return dict(zip(raw, scores))


def predict_timing_scores(path, level, protected, mix, baseline, profile, *, uses_audio=True):
    from joblib import load
    from sklearn import __version__ as sklearn_version
    from threadpoolctl import threadpool_limits
    import warnings

    # Only bundled project assets may be loaded by the caller.
    from sklearn.exceptions import InconsistentVersionWarning

    with warnings.catch_warnings():
        warnings.simplefilter("error", InconsistentVersionWarning)
        try:
            model = load(path)
        except InconsistentVersionWarning as error:
            raise ValueError("Install the ranker dependencies from tools/onset-requirements.txt.") from error
    if model["sklearn"] != sklearn_version or model["features"] != FEATURES + MOTIF_FEATURES:
        raise ValueError(
            "The bundled Easy timing model does not match this engine or its Python dependencies."
        )
    if model.get("uses_audio", True) != uses_audio:
        raise ValueError("The bundled Easy timing model does not match its catalog entry.")
    raw, beats, features = source_features(level, protected, mix, baseline, profile)
    with threadpool_limits(limits=1):
        motifs = add_motif_features(beats, features, FEATURES)
        ranked = model["motif"].predict_proba(motifs)[:, 1]
        pooled = pool_motif_confidence(ranked, motif_peers(beats, features, FEATURES, width=2))
        fallback = model["fallback"].predict_proba(features)[:, 1]
    rank, budget = guard_motif_confidence(beats, pooled, fallback, [mix[b] for b in raw])
    return dict(zip(raw, rank)), dict(zip(raw, budget))
