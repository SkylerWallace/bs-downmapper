"""Build ChroMapper review maps from independent stem onset peaks."""

import argparse
from bisect import bisect_left, bisect_right, insort
from copy import deepcopy
import math
from pathlib import Path
import re
import shutil
from tempfile import TemporaryDirectory

from audio_onsets import HOP, ROOT, SAMPLE_RATE, audio_file, onset_envelope, scnet_model_hash, separate_scnet
from beatmap_io import DIFFICULTY_RANKS, load, read_level, sha, write


STEM_DIFFICULTIES = (
    ("Easy", "Vocals", "vocals"),
    ("Normal", "Leads (SCNet Other)", "other"),
    ("Hard", "Percussion", "drums"),
    ("Expert", "Bass", "bass"),
)
DETECTOR_REVISION = "r06"
BAND_FFT = 1024


def pick_onsets(librosa, envelope, duration, **settings):
    peaks = librosa.onset.onset_detect(onset_envelope=envelope, sr=SAMPLE_RATE, hop_length=HOP, **settings)
    seconds = librosa.frames_to_time(peaks, sr=SAMPLE_RATE, hop_length=HOP)
    return [
        dict(seconds=float(time), strength=float(envelope[frame]))
        for time, frame in zip(seconds, peaks)
        if 0 <= time < duration
    ]


def gate_onsets(events, signal, sample_rate, floor_dbfs, radius):
    import numpy as np

    floor_power = 10 ** (floor_dbfs / 10)
    width = round(radius * sample_rate)
    active = []
    for event in events:
        center = round(event["seconds"] * sample_rate)
        window = signal[max(0, center - width) : center + width]
        if window.size and np.mean(window * window) >= floor_power:
            active.append(event)
    return active


def separated_onsets(times, candidates, separation):
    """Find candidates sufficiently far from the stronger detector pass."""
    additions = []
    for event in candidates:
        time = event["seconds"]
        i = bisect_left(times, time)
        if all(abs(time - other) >= separation for other in times[max(0, i - 1) : i + 1]):
            additions.append(event)
    return additions


def consolidate_onsets(primary, candidates, minimum_gap):
    """Keep the first distinct candidate and suppress its closely spaced echoes."""
    times = [event["seconds"] for event in primary]
    kept = primary[:]
    for event in candidates:
        time = event["seconds"]
        i = bisect_left(times, time)
        gap = minimum_gap(time)
        if all(abs(time - other) >= gap for other in times[max(0, i - 1) : i + 1]):
            insort(times, time)
            kept.append(event)
    return sorted(kept, key=lambda event: event["seconds"])


def band_envelope(librosa, signal, low, high, max_size):
    mel = librosa.feature.melspectrogram(
        y=signal, sr=SAMPLE_RATE, hop_length=HOP, n_fft=BAND_FFT, n_mels=48, fmin=low, fmax=high
    )
    return librosa.onset.onset_strength(
        S=librosa.power_to_db(mel), sr=SAMPLE_RATE, hop_length=HOP, n_fft=BAND_FFT, max_size=max_size
    )


def strum_envelope(spectrum, frequencies):
    import numpy as np

    spectrum = spectrum[(frequencies >= 1000) & (frequencies <= 8000)]
    amplitude = np.abs(spectrum)
    phase = np.angle(spectrum)
    # Predict each bin's phase from its previous motion. A fresh attack
    # breaks that prediction even when distortion holds the level steady.
    predicted = amplitude[:, 1:-1] * np.exp(1j * (2 * phase[:, 1:-1] - phase[:, :-2]))
    residual = np.abs(spectrum[:, 2:] - predicted)
    rising = amplitude[:, 2:] >= amplitude[:, 1:-1]
    return np.pad((residual * rising).mean(axis=0), (2, 0))


def locally_scaled_envelope(envelope, seconds, floor_fraction):
    import numpy as np
    from scipy.ndimage import maximum_filter1d

    frames = max(1, round(seconds * SAMPLE_RATE / HOP))
    peak = maximum_filter1d(envelope, size=frames)
    # The song-wide floor keeps very quiet stem leakage from being boosted.
    floor = floor_fraction * np.quantile(envelope, 0.98) + 1e-6
    return np.minimum(1, envelope / np.maximum(peak, floor))


def local_strength(envelope, time):
    first = max(0, round((time - 0.017) * SAMPLE_RATE / HOP))
    end = round((time + 0.017) * SAMPLE_RATE / HOP) + 1
    return envelope[first:end].max(initial=0)


def percussion_gap(confirmed, envelope, signal, time):
    import numpy as np

    # Fast passages justify closer hits. Elsewhere a nearby peak needs a
    # clear attack, so a ringing cymbal tail does not become another hit.
    nearby = bisect_right(confirmed, time + 0.5) - bisect_left(confirmed, time - 0.5)
    if nearby >= 5:
        return 0.055
    if local_strength(envelope, time) >= 0.85:
        before = signal[max(0, round((time - 0.052) * SAMPLE_RATE)) : round((time - 0.017) * SAMPLE_RATE)]
        attack = signal[max(0, round((time - 0.015) * SAMPLE_RATE)) : round((time + 0.021) * SAMPLE_RATE)]
        if before.size and attack.size and np.mean(attack * attack) >= np.mean(before * before):
            return 0.055
    return 0.085


def select_lead_attacks(librosa, duration, events, primary, secondary, phase, mel, amplitude, frequencies):
    import numpy as np
    from scipy.ndimage import maximum_filter1d, uniform_filter1d

    hop_seconds = HOP / SAMPLE_RATE
    context = round(1 / hop_seconds)
    band_delay = BAND_FFT // (2 * HOP)
    level = np.linalg.norm(amplitude[(frequencies >= 170) & (frequencies < 8000)], axis=0)
    sustain = uniform_filter1d(level / (maximum_filter1d(level, size=context) + 1e-6), size=context)
    rise = np.maximum(0, np.diff(amplitude, prepend=amplitude[:, :1], axis=1))
    bands = []
    for low, high in ((170, 550), (550, 1600), (1600, 3800), (3800, 8000)):
        flux = np.linalg.norm(rise[(frequencies >= low) & (frequencies < high)], axis=0)
        peak = maximum_filter1d(flux, size=context)
        bands.append(flux / np.maximum(peak, 0.06 * np.quantile(flux, 0.97) + 1e-9))
    treble = np.minimum(bands[2], bands[3])
    joint = 0.8 * np.sort(bands, axis=0)[-2] + 0.2 * phase
    mel = np.pad(mel[band_delay:], (0, min(band_delay, len(mel))))
    mel = mel / (maximum_filter1d(mel, size=context) + 1e-7)

    def nearby_peak(envelope, frame, radius=3):
        return envelope[max(0, frame - radius) : frame + radius + 1].max(initial=0)

    primary_times = {event["seconds"] for event in primary}
    band_times = {event["seconds"] for event in secondary}
    attacks = []
    for event in events:
        time = event["seconds"]
        frame = round(time / hop_seconds)
        phase_only = time not in primary_times and time not in band_times
        if time in band_times:
            # Apply more of the Mel-window compensation when a sustained
            # signal makes spectral peaks lag the leading attack.
            fraction = min(1, max(0, (sustain[frame] - 0.48) / 0.17))
            frame = max(0, frame - round(band_delay * fraction))
        elif time in primary_times and sustain[frame] >= 0.60:
            frame = max(0, frame - 2)
        phase_peak = nearby_peak(phase, frame)
        if sustain[frame] < 0.59 and (
            phase_peak < 0.235 or (phase_peak < 0.48 and nearby_peak(joint, frame) < 0.34)
        ):
            continue
        if phase_only and sustain[frame] < 0.57 and phase_peak < 0.78 and nearby_peak(treble, frame) < 0.52:
            continue
        attacks.append((frame, phase_only, dict(event, seconds=frame * hop_seconds)))

    retained = []
    for index, (frame, phase_only, event) in enumerate(attacks):
        neighbors = attacks[max(0, index - 1) : index] + attacks[index + 1 : index + 2]
        # Reject a weak, uncorroborated phase peak beside a clearer attack.
        if (
            phase_only
            and nearby_peak(mel, frame) < 0.62
            and any(
                abs(other - frame) <= round(0.078 / hop_seconds)
                and (
                    nearby_peak(treble, other) > nearby_peak(treble, frame) + 0.02
                    or nearby_peak(phase, other) >= nearby_peak(phase, frame) + 0.09
                )
                for other, _, _ in neighbors
            )
        ):
            continue
        retained.append(event)

    times = [event["seconds"] for event in retained]
    candidates = pick_onsets(
        librosa,
        phase,
        duration,
        normalize=False,
        delta=0.12,
        pre_max=3,
        post_max=3,
        pre_avg=8,
        post_avg=9,
        wait=7,
    )
    recovery = []
    for event in candidates:
        time = event["seconds"]
        frame = round(time / hop_seconds)
        index = bisect_left(times, time)
        if not 0 < index < len(times):
            continue
        before, after = times[index - 1 : index + 1]
        # A weak strum in sustained audio needs two-band attack support and
        # space between confirmed hits. This keeps ringing tails out of runs.
        if (
            sustain[frame] >= 0.67
            and event["strength"] >= 0.53
            and after - before >= 0.205
            and min(time - before, after - time) >= 0.073
            and nearby_peak(treble, frame, radius=2) >= 0.50
        ):
            recovery.append(event)
    recovery.sort(key=lambda event: -event["strength"])
    return consolidate_onsets(retained, recovery, lambda _: 0.065)


def detect_stem_onsets(signal, sample_rate, stem):
    import librosa

    duration = len(signal) / sample_rate
    mono = librosa.resample(signal, orig_sr=sample_rate, target_sr=SAMPLE_RATE)
    primary = pick_onsets(librosa, onset_envelope(librosa, mono), duration)
    if stem == "vocals":
        return gate_onsets(primary, signal, sample_rate, -45, 0.025)
    if stem not in ("other", "drums"):
        return primary

    guitar = stem == "other"
    low, high = (160, 6500) if guitar else (4500, 10000)
    envelope = band_envelope(librosa, mono, low, high, 1 if guitar else 3)
    # Guitar needs a more sensitive mid-band pass for repeated strokes.
    # A shorter high-band reference window picks up softer, fast hi-hats.
    average = 12 if guitar else 7
    secondary = pick_onsets(
        librosa,
        envelope,
        duration,
        delta=0.04,
        pre_max=4,
        post_max=2,
        pre_avg=average,
        post_avg=average + 1,
        wait=3,
    )
    if guitar:
        mel = envelope
        spectrum = librosa.stft(mono, n_fft=512, hop_length=HOP)
        frequencies = librosa.fft_frequencies(sr=SAMPLE_RATE, n_fft=512)
        envelope = locally_scaled_envelope(strum_envelope(spectrum, frequencies), 1.5, 0.2)
        gate_signal, floor, radius = mono, -40, 0.025
    else:
        from scipy.signal import butter, sosfiltfilt

        high_band = sosfiltfilt(butter(4, 3500, "highpass", fs=SAMPLE_RATE, output="sos"), mono)
        secondary = gate_onsets(secondary, high_band, SAMPLE_RATE, -50, 0.018)
        envelope = locally_scaled_envelope(band_envelope(librosa, mono, 6500, 10500, 1), 0.6, 0.25)
        gate_signal, floor, radius = high_band, -44, 0.018
    secondary = separated_onsets(
        [event["seconds"] for event in primary], secondary, 0.035 if guitar else 0.045
    )
    retained = sorted(primary + secondary, key=lambda event: event["seconds"])
    confirmed = [event["seconds"] for event in retained]
    average = 8 if guitar else 7
    fine = pick_onsets(
        librosa,
        envelope,
        duration,
        normalize=False,
        delta=0.2 if guitar else 0.18,
        pre_max=3,
        post_max=3,
        pre_avg=average,
        post_avg=average + 1,
        wait=7,
    )
    fine = gate_onsets(fine, gate_signal, SAMPLE_RATE, floor, radius)
    fine = separated_onsets(confirmed, fine, 0.045)
    if guitar:
        retained = consolidate_onsets(primary, secondary, lambda _: 0.05)
        fine = [event for event in fine if local_strength(envelope, event["seconds"]) >= 0.6]
        events = consolidate_onsets(retained, fine, lambda _: 0.055)
        return select_lead_attacks(
            librosa, duration, events, primary, secondary, envelope, mel, abs(spectrum), frequencies
        )
    return consolidate_onsets(retained, fine, lambda time: percussion_gap(confirmed, envelope, mono, time))


def timing_chart(level, onsets, sample_rate):
    # V2 stores note times on the song-BPM grid, including maps with a
    # separate variable-tempo editor grid in their legacy custom data.
    offset = math.ceil(level["info"].get("_songTimeOffset", 0) * sample_rate) / sample_rate
    chart = {"_version": "2.0.0", "_events": [], "_obstacles": [], "_notes": []}
    tempo_keys = ("_BPMChanges", "_bpmChanges")
    chart.update({key: level["raw"][key] for key in tempo_keys if key in level["raw"]})
    custom = level["raw"].get("_customData", {})
    custom_tempo = {key: custom[key] for key in tempo_keys if key in custom}
    if custom_tempo:
        chart["_customData"] = custom_tempo
    for event in onsets:
        time = event["seconds"] - offset
        if time < 0:
            continue
        hand = len(chart["_notes"]) % 2
        chart["_notes"].append(
            {
                "_time": level["clock"].beat_at_seconds(time),
                "_lineIndex": 1 + hand,
                "_lineLayer": 0,
                "_type": hand,
                "_cutDirection": 8,
            }
        )
    return chart


def prepare_stems(level, song, runtime, weights, reuse):
    import numpy as np
    import soundfile as sf

    audio_hash = sha(audio_file(level))
    if reuse:
        previous = load(reuse / "onsets.json")
        if previous["sourceAudioSha256"] != audio_hash:
            raise ValueError("Saved stems belong to a different recording.")
        return Path(previous.get("stemDirectory", reuse / "stems")), audio_hash, previous["scnetModelSha256"]

    model_hash = scnet_model_hash(weights)
    print("  Separating four stems", flush=True)
    audio, sample_rate = sf.read(audio_file(level), dtype="float32", always_2d=True)
    stems_dir = song / "stems"
    stems_dir.mkdir()
    with TemporaryDirectory(prefix="scnet-", dir=song) as directory:
        work = Path(directory)
        separate_scnet(audio, sample_rate, work, runtime, weights)
        for _, label, stem in STEM_DIFFICULTIES:
            signal = np.fromfile(work / f"{stem}.f32", "<f4")
            sf.write(stems_dir / f"{label}.wav", signal, sample_rate, subtype="FLOAT")
    return stems_dir, audio_hash, model_hash


def make_review_map(source, output, wip, runtime, weights, reuse=None):
    import soundfile as sf

    level = read_level(source)
    if not level["version"].startswith("2.") or "_songName" not in level["info"]:
        raise ValueError("The transient review exporter requires a legacy v2 source map.")
    print(f"\n{level['info']['_songName']}: building the transient review", flush=True)
    song = output / source.name
    package = song / "map"
    package.mkdir(parents=True)
    stems_dir, audio_hash, model_hash = prepare_stems(level, song, runtime, weights, reuse)
    recording = sf.info(audio_file(level))
    info = deepcopy(level["info"])
    info["_songName"] += f" [Transient Audit {DETECTOR_REVISION}]"
    info.pop("_customData", None)
    entries = []
    results = {}
    for difficulty, label, stem in STEM_DIFFICULTIES:
        wav = f"{label}.wav"
        signal, sample_rate = sf.read(stems_dir / wav, dtype="float32")
        if signal.ndim != 1 or sample_rate != recording.samplerate or len(signal) != recording.frames:
            raise ValueError(f"{label} must be mono and aligned to the source audio.")
        events = detect_stem_onsets(signal, sample_rate, stem)
        chart = timing_chart(level, events, sample_rate)
        count = len(chart["_notes"])
        if not count:
            raise ValueError(f"{label} yielded no onsets to review.")
        filename = f"{difficulty}Standard.dat"
        write(package / filename, chart)
        entries.append(
            {
                "_difficulty": difficulty,
                "_difficultyRank": DIFFICULTY_RANKS[difficulty],
                "_beatmapFilename": filename,
                "_noteJumpMovementSpeed": 12,
                "_noteJumpStartBeatOffset": 0,
                "_customData": {"_difficultyLabel": label},
            }
        )
        results[difficulty] = {
            "stem": label,
            "scnetSource": stem,
            "wav": wav,
            "markers": count,
            "peaks": events,
        }
        print(f"  {difficulty} | {label} | {count} timing markers", flush=True)

    info["_difficultyBeatmapSets"] = [
        {"_beatmapCharacteristicName": "Standard", "_difficultyBeatmaps": entries}
    ]
    for name in (info["_songFilename"], info["_coverImageFilename"]):
        shutil.copy2(source / name, package / name)
    write(package / "Info.dat", info)
    for difficulty, result in results.items():
        chart = read_level(package, difficulty)
        if len(chart["notes"]) != result["markers"] or chart["bombs"] or chart["walls"]:
            raise ValueError(f"{difficulty} failed the review map timing check.")
    name = re.sub(r"[^\w -]", "", info["_songName"]).strip()
    installed = wip / f"{name} ({source.name}, {output.name})"
    installed.mkdir()
    for path in sorted(package.iterdir()):
        if path.name != "Info.dat":
            shutil.copy2(path, installed / path.name)
    # Publish metadata last so ChroMapper discovers a complete map.
    shutil.copy2(package / "Info.dat", installed / "Info.dat")
    write(
        song / "onsets.json",
        {
            "sourceAudioSha256": audio_hash,
            "sourceChart": level["filename"],
            "sampleRate": SAMPLE_RATE,
            "hopSamples": HOP,
            "nFft": BAND_FFT,
            "scnetModelSha256": model_hash,
            "wipMap": str(installed),
            "detector": DETECTOR_REVISION,
            "stemDirectory": str(stems_dir),
            "difficulties": results,
        },
    )
    print(f"  ChroMapper map: {installed}\n  Stem audio: {stems_dir}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--wip", type=Path, required=True)
    parser.add_argument("--reuse-stems", type=Path, help="Read separated WAVs from a previous audit output")
    parser.add_argument("--scnet-runtime", type=Path, default=ROOT / ".tools/onset-node")
    parser.add_argument("--scnet-weights", type=Path, default=ROOT / ".tools/onset-model/scnet")
    args = parser.parse_args()
    output = args.output.resolve()
    wip = args.wip.resolve(strict=True)
    sources = [source.resolve(strict=True) for source in args.source]
    if not wip.is_dir() or any(
        output.is_relative_to(source) or wip.is_relative_to(source) for source in sources
    ):
        parser.error("Choose output and WIP directories outside the source maps.")
    runtime, weights = args.scnet_runtime.resolve(), args.scnet_weights.resolve()
    reuse = args.reuse_stems.resolve(strict=True) if args.reuse_stems else None
    output.mkdir(parents=True, exist_ok=True)
    for source in sources:
        make_review_map(source, output, wip, runtime, weights, reuse / source.name if reuse else None)
    print("\nFinished. Use ChroMapper note ticks to review each stem’s timing markers.", flush=True)


if __name__ == "__main__":
    main()
