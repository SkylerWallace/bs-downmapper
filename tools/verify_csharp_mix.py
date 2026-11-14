"""Compare the managed onset scorer on the same decoded, resampled PCM."""

import argparse
import math
from pathlib import Path
import struct
import subprocess
from unittest.mock import patch

import numpy as np
import soundfile as sf
import soxr

from analyze_standard_spreads import groups
from audio_onsets import SAMPLE_RATE, audio_file, mix_beat_weights
from beatmap_io import load, read_level, write
from chromapper_export import nested_reference_plan
from nested_priority import select_prefix
from timing_benchmark import score


def native_mix(probe, samples, seconds):
    samples = np.asarray(samples, dtype="<f4")
    seconds = np.asarray(seconds, dtype="<f8")
    process = subprocess.run(
        ["dotnet", str(probe), "--mix"],
        input=struct.pack("<ii", len(samples), len(seconds)) + samples.tobytes() + seconds.tobytes(),
        capture_output=True, check=True,
    )
    result = np.frombuffer(process.stdout, dtype="<f8")
    if result.shape != seconds.shape or not np.isfinite(result).all():
        raise ValueError("The native scorer returned invalid onset scores.")
    return result


def read_mix(level):
    audio, rate = sf.read(audio_file(level), dtype="float32", always_2d=True)
    mono = audio.mean(axis=1)
    if rate != SAMPLE_RATE:
        length = math.ceil(len(mono) * (SAMPLE_RATE / rate))
        mono = soxr.resample(mono, rate, SAMPLE_RATE, quality="HQ")[:length]
        mono = np.pad(mono, (0, length - len(mono)))
    return mono, rate


def compare(source, probe):
    reference = read_level(source, "ExpertPlus")
    pcm, rate = read_mix(reference)
    offset = math.ceil(reference["info"].get("_songTimeOffset", 0) * rate) / rate
    scores = {}

    def mix(level, native):
        beats = tuple(groups(level["notes"]))
        if beats not in scores:
            seconds = [level["clock"].seconds(beat) + offset for beat in beats]
            scores[beats] = (mix_beat_weights(np, pcm, seconds), native_mix(probe, pcm, seconds))
        return dict(zip(beats, scores[beats][native].tolist()))

    def priorities(native):
        with patch("shared_timing_model.source_mix", lambda level: mix(level, native)):
            return nested_reference_plan(reference)

    old, new = priorities(False), priorities(True)
    tiers = {}
    for tier in old[1]:
        authored = read_level(source, tier)["notes"]
        selections = [
            select_prefix(reference, order, reference["notes"], budgets[tier], locked, (), source_is_reference=True)
            for order, budgets, locked in (old, new)
        ]
        metrics = [score(authored, notes) for notes in selections]
        tiers[tier] = {
            "identical": [note["id"] for note in selections[0]] == [note["id"] for note in selections[1]],
            "notes": [item["generated_notes"] for item in metrics],
            "beatCompliance": [item["beat_compliance"] for item in metrics],
            "extraHits": [len(item["extra_beats"]) for item in metrics],
        }
    errors = np.concatenate([np.abs(a - b) for a, b in scores.values()])
    return {
        "scoredTimings": len(errors),
        "maximumAbsoluteError": float(errors.max(initial=0)),
        "meanAbsoluteError": float(errors.mean()),
        "priorityOrderMatches": old == new,
        "tiers": tiers,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--maps", type=Path, required=True)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    np.testing.assert_array_equal(native_mix(args.probe, np.zeros(SAMPLE_RATE), [.25, .5]), [0, 0])
    signal = np.zeros(SAMPLE_RATE, dtype=np.float32)
    signal[SAMPLE_RATE // 2] = 1
    times = [.1, .5, .9]
    np.testing.assert_allclose(native_mix(args.probe, signal, times), mix_beat_weights(np, signal, times), atol=1e-5)
    catalog = load(Path(__file__).resolve().parents[1] / "maps/test-candidates.json")
    report = {}
    for item in catalog["maps"]:
        if item["usage"] == "paired-reference":
            key = item["key"]
            report[key] = compare(args.maps / key, args.probe)
            write(args.output, report)
            print(key, report[key]["maximumAbsoluteError"], report[key]["priorityOrderMatches"], flush=True)


if __name__ == "__main__":
    main()
