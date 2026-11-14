"""Generate reusable onset scores for the authored source beats of a song."""

import argparse
import math
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
from uuid import uuid4

from analyze_standard_spreads import groups
from beatmap_io import load, read_level, sha, write


ROOT = Path(__file__).resolve().parents[1]
SCNET_SHA256 = "98228931494151762a1c4ab1ec7899a894b1f81fd4a509921a9d2f9bacc50845"
STEMS = ("drums", "bass", "other", "vocals")
SAMPLE_RATE = 22050
HOP = 128
MIX_ANALYSIS = "band-flux-and-energy-v1"


def audio_file(level):
    info = level["info"]
    return level["folder"] / info.get("audio", {}).get("songFilename", info.get("_songFilename"))


def source_signature(level):
    clock = level["clock"]
    return {
        "audio": sha(audio_file(level)),
        "info": sha(level["folder"] / "Info.dat"),
        "chart": sha(level["folder"] / level["filename"]),
        "tempo": [clock.starts, clock.times, clock.rates],
    }


def read_onset_scores(level, path, mode):
    cache = load(path)
    if cache.get("schema") != 1 or cache.get("source") != source_signature(level):
        raise ValueError("Onset cache does not match the current source song and difficulty.")
    scores = dict(cache["scores"].get(mode, []))
    if scores.keys() != groups(level["notes"]).keys() or any(
        not math.isfinite(score) or not 0 <= score <= 1 for score in scores.values()
    ):
        raise ValueError(f"Onset cache has no valid {mode} scores for every source beat.")
    return scores


def onset_envelope(librosa, signal):
    return librosa.onset.onset_strength(y=signal, sr=SAMPLE_RATE, hop_length=HOP, n_fft=1024, max_size=3)


def sample_strength(librosa, np, signal, seconds):
    envelope = onset_envelope(librosa, signal)
    scale = float(np.percentile(envelope, 98))
    if scale <= 1e-8:
        return np.zeros(len(seconds))
    frames = librosa.frames_to_time(np.arange(len(envelope)), sr=SAMPLE_RATE, hop_length=HOP)
    strengths = []
    for time in seconds:
        first = np.searchsorted(frames, time - 0.035)
        end = np.searchsorted(frames, time + 0.045, side="right")
        distance = frames[first:end] - time
        local = np.minimum(envelope[first:end] / scale, 1) * np.exp(-((distance / 0.028) ** 2))
        strengths.append(float(local.max(initial=0)))
    return np.array(strengths)


def mel_band_edges(np, count):
    """Slaney mel scale from 35 Hz to 10 kHz, with a linear low band."""
    linear_step = 200 / 3
    log_step = np.log(6.4) / 27
    scales = np.linspace(35 / linear_step, 15 + np.log(10) / log_step, count)
    edges = scales * linear_step
    high = scales >= 15
    edges[high] = 1000 * np.exp(log_step * (scales[high] - 15))
    return edges


def mix_spectra(np, signal, fft):
    from scipy.fft import rfft
    from scipy.signal import get_window

    padded = np.pad(signal, fft // 2)
    frames = np.lib.stride_tricks.sliding_window_view(padded, fft)[::HOP].T
    window = get_window("hann", fft).reshape(-1, 1)
    power = np.empty((fft // 2 + 1, frames.shape[1]), dtype=np.float32, order="F")
    rms = np.empty(frames.shape[1], dtype=np.float32)
    for start in range(0, frames.shape[1], 256):
        batch = frames[:, start : start + 256]
        spectrum = rfft(batch * window, axis=0).astype(np.complex64)
        power[:, start : start + batch.shape[1]] = np.abs(spectrum) ** 2
        rms[start : start + batch.shape[1]] = np.sqrt(np.mean(batch ** 2, axis=0))

    edges = mel_band_edges(np, 66)
    bins = np.fft.rfftfreq(fft, 1 / SAMPLE_RATE)
    rising = (bins - edges[:-2, None]) / (edges[1:-1] - edges[:-2])[:, None]
    falling = (edges[2:, None] - bins) / (edges[2:] - edges[1:-1])[:, None]
    filters = np.maximum(0, np.minimum(rising, falling)).astype(np.float32)
    filters *= (2 / (edges[2:] - edges[:-2]))[:, None]
    mel = np.einsum("ft,mf->mt", power, filters, optimize=True)
    decibels = 10 * np.log10(np.maximum(1e-10, mel))
    return np.maximum(decibels, decibels.max() - 80), rms, edges[1:-1]


def mix_beat_weights(np, signal, seconds):
    """Measure attack prominence around the authored candidate timings."""
    from scipy.ndimage import maximum_filter1d

    if not seconds:
        return np.zeros(0)
    decibels, rms, frequencies = mix_spectra(np, signal, 1024)
    flux = []
    for low, high in ((35, 250), (250, 2000), (2000, 10001)):
        band = decibels[(frequencies >= low) & (frequencies < high)]
        reference = maximum_filter1d(band, 3, axis=0)
        increase = np.maximum(0, band[:, 1:] - reference[:, :-1])
        flux.append(np.pad(increase.mean(axis=0), (1, 0)))
    flux = np.asarray(flux)
    frames = np.arange(len(rms)) * HOP / SAMPLE_RATE
    seconds = np.asarray(seconds)
    features = np.zeros((len(seconds), 6))
    for index, time in enumerate(seconds):
        first, end = np.searchsorted(frames, [time - 0.030, time + 0.040])
        if first == end:
            continue
        proximity = np.exp(-(((frames[first:end] - time) / 0.026) ** 2))
        features[index, :3] = np.max(flux[:, first:end] * proximity, axis=1)
        # The steepest short rise and the change from the preceding level
        # give separate cues for sharp and gradually building attacks.
        earlier = np.maximum(0, np.arange(first, end) - 2)
        features[index, 3] = np.max(np.maximum(0, rms[first:end] - rms[earlier]) * proximity)
        before = np.searchsorted(frames, time - 0.080)
        local_peak = float(rms[first:end].max())
        previous = float(np.median(rms[before:first])) if before < first else local_peak
        features[index, 4] = max(0, local_peak - previous)
        features[index, 5] = local_peak

    # Rank accents near one another while keeping a song-wide floor so
    # near-silence and encoding noise cannot become strong cues on their own.
    floor = np.maximum(0.25 * np.percentile(features, 95, axis=0), 1e-8)
    normalized = np.zeros_like(features)
    for index, time in enumerate(seconds):
        first, end = np.searchsorted(seconds, [time - 4, time + 4])
        local = np.percentile(features[first:end], 90, axis=0)
        normalized[index] = np.minimum(1, features[index] / np.maximum(floor, local))
    bands = normalized[:, :3]
    attack = 0.7 * bands.max(axis=1) + 0.3 * bands.mean(axis=1)
    return 0.60 * attack + 0.20 * normalized[:, 3] + 0.15 * normalized[:, 4] + 0.05 * normalized[:, 5]


def scnet_model_hash(weights):
    model = sha(weights / "scnet.int8.onnx")
    if model != SCNET_SHA256:
        raise ValueError("SCNet ONNX model checksum does not match the pinned model.")
    return model


def separate_scnet(audio, sample_rate, work, runtime, weights):
    """Write the same mono stem signals used by the onset scorer."""
    import numpy as np

    pcm = work / "mix.f32"
    stereo = np.repeat(audio, 2, axis=1) if audio.shape[1] == 1 else audio[:, :2]
    stereo.T.astype("<f4", copy=False).tofile(pcm)
    subprocess.run(
        [
            "node",
            str(ROOT / "tools/scnet_stems.mjs"),
            str(pcm),
            str(work),
            str(sample_rate),
            str(runtime),
            str(weights.parent),
        ],
        check=True,
    )


def mix_timing_scores(level, audio, sample_rate):
    import numpy as np
    import soxr

    beats = sorted(groups(level["notes"]))
    offset = math.ceil(level["info"].get("_songTimeOffset", 0) * sample_rate) / sample_rate
    seconds = [level["clock"].seconds(beat) + offset for beat in beats]
    mix = audio.mean(axis=1)
    if sample_rate != SAMPLE_RATE:
        length = math.ceil(len(mix) * (SAMPLE_RATE / sample_rate))
        mix = soxr.resample(mix, sample_rate, SAMPLE_RATE, quality="HQ")[:length]
        if len(mix) < length:
            mix = np.pad(mix, (0, length - len(mix)))
    return beats, seconds, mix_beat_weights(np, mix, seconds).tolist()


def build_onset_cache(level, output, scnet_runtime, weights):
    import numpy as np
    import soundfile as sf

    source = source_signature(level)
    model = scnet_model_hash(weights) if weights is not None else None
    if model:
        import librosa

    provenance = {
        "librosa": librosa.__version__ if model else None,
        "mix_analysis": MIX_ANALYSIS,
        "scnet_sha256": model,
        "scnet_step": 0.5 if model else None,
    }
    if output.is_file():
        cache = load(output)
        if cache.get("schema") == 1 and cache.get("source") == source and cache.get("analysis") == provenance:
            return

    audio, sample_rate = sf.read(audio_file(level), dtype="float32", always_2d=True)
    beats, seconds, mix_score = mix_timing_scores(level, audio, sample_rate)
    scores = {"mix": list(zip(beats, mix_score))}
    if model:
        output.parent.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(prefix="scnet-", dir=output.parent) as directory:
            work = Path(directory)
            separate_scnet(audio, sample_rate, work, scnet_runtime, weights)
            stem_scores = [
                sample_strength(
                    librosa,
                    np,
                    librosa.resample(
                        np.fromfile(work / f"{stem}.f32", "<f4"), orig_sr=sample_rate, target_sr=SAMPLE_RATE
                    ),
                    seconds,
                )
                for stem in STEMS
            ]
        stem_score = np.maximum.reduce(
            [weight * score for weight, score in zip((1, 0.85, 0.85, 0.65), stem_scores)]
        )
        scores["stems"] = list(zip(beats, (0.65 * np.asarray(mix_score) + 0.35 * stem_score).tolist()))

    temporary = output.with_suffix(f".{uuid4().hex}.tmp")
    write(temporary, {"schema": 1, "source": source, "analysis": provenance, "scores": scores})
    temporary.replace(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--source-difficulty", default="ExpertPlus")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--scnet-weights", type=Path)
    parser.add_argument("--scnet-runtime", type=Path, default=ROOT / ".tools/onset-node")
    args = parser.parse_args()
    build_onset_cache(
        read_level(args.source, args.source_difficulty),
        args.output.resolve(),
        args.scnet_runtime.resolve(),
        args.scnet_weights.resolve() if args.scnet_weights else None,
    )


if __name__ == "__main__":
    main()
