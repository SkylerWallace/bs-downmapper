"""Compare chart-only, mix-weighted and SCNet-assisted Standard Easy timing."""

import argparse
import csv
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from statistics import mean

from beatmap_io import load, read_level, sha
from timing_benchmark import ROOT, aggregate, atomic_json, run_song, score
from chromapper_export import automatic_timing_context, automatic_timings, profile_for
from rhythm_inputs import apply_merges


MODES = ("current", "mix", "stems")


def benchmark_song(key, source, onset_cache, output, modes):
    profile = profile_for("Easy", None)
    level, protected = automatic_timing_context(read_level(source, "ExpertPlus"), profile)
    source_beats = {note["b"] for note in level["notes"]}
    authored = read_level(source, "Easy")["notes"]
    expected_count = None
    rows = []
    for mode in modes:
        cache = onset_cache if mode != "current" else None
        retained, merges = automatic_timings(level, profile, protected, onset_cache=cache, onset_mode=mode)
        notes = apply_merges(level, retained, merges)
        if expected_count is None:
            expected_count = len(notes)
        if len(notes) != expected_count:
            raise ValueError(f"{key} {mode} changed the generated note count")
        if any(note["b"] not in source_beats for note in notes):
            raise ValueError(f"{key} {mode} introduced a timing outside the source chart")
        proposal = score(authored, notes)
        timings = proposal["generated_timing_groups"]
        destination = output / mode
        details = destination / "details" / key
        details.mkdir(parents=True, exist_ok=True)
        result = details / "result.json"
        input_hash = sha(cache) if cache else None
        previous = load(result) if result.exists() else None
        if previous and previous["status"].startswith("exported"):
            row = previous
            if (
                row["onset_cache_sha256"] != input_hash
                or row["authored_timing_groups"]
                != [list(group) for group in proposal["authored_timing_groups"]]
                or row["generated_timing_groups"] != [list(group) for group in timings]
            ):
                raise ValueError(f"{key} {mode} changed inputs or timings. Choose a new output folder.")
        else:
            row = run_song(destination, key, source, timings, onset_cache=cache, onset_mode=mode)
            row.update(
                mode=mode,
                onset_cache_sha256=input_hash,
                expected_note_count=expected_count,
                source_timings_verified=True,
            )
            atomic_json(result, row)
        rows.append(row)
        print(f"{key} {mode}: {row['status']}, {row.get('matched_beats', '-')} matching beats", flush=True)
    return rows


def publish(output, rows, modes):
    rows.sort(key=lambda row: (row["song_id"], MODES.index(row["mode"])))
    fields = (
        "song_id",
        "title",
        "mode",
        "status",
        "authored_beats",
        "generated_beats",
        "matched_beats",
        "extra_beat_count",
        "beat_compliance",
        "beat_precision",
        "authored_notes",
        "generated_notes",
        "matched_notes",
        "note_compliance",
        "expected_note_count",
        "source_timings_verified",
        "saved_timing_verified",
        "stock_error",
        "retry_error",
        "error",
    )
    with (output / "comparison.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fields, extrasaction="ignore")
        writer.writeheader()
        for row in rows:
            writer.writerow(
                {**row, "extra_beat_count": len(row["extra_beats"]) if "extra_beats" in row else None}
            )
    verified = {}
    for mode in modes:
        cohort = [row for row in rows if row["mode"] == mode]
        verified[mode] = {row["song_id"] for row in cohort if row.get("saved_timing_verified")}
    common = set.intersection(*verified.values())
    summary = {}
    for mode in modes:
        cohort = [row for row in rows if row["mode"] == mode]
        scored = [row for row in cohort if row["song_id"] in common]
        summary[mode] = {
            **aggregate(scored),
            "mean_map_beat_compliance": mean(row["beat_compliance"] for row in scored) if scored else None,
            "exported": sum(row["status"].startswith("exported") for row in cohort),
            "extra_beat_count": sum(len(row["extra_beats"]) for row in scored),
            "note_counts_verified": sum(
                row["generated_notes"] == row["expected_note_count"] for row in scored
            ),
        }
    atomic_json(output / "summary.json", summary)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--maps", type=Path, required=True)
    parser.add_argument("--onsets", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--song", action="append", help="Optional map folder name")
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument(
        "--include-stems", action="store_true", help="Compare stem scores when present in the caches."
    )
    args = parser.parse_args()
    if args.workers < 1:
        parser.error("--workers must be positive")
    songs = {
        path.name: path.resolve() for path in sorted(args.maps.iterdir()) if (path / "Info.dat").is_file()
    }
    keys = args.song or list(songs)
    if not keys or any(key not in songs for key in keys):
        parser.error("Select existing song folders with Info.dat")
    if any(not songs[key].is_relative_to(ROOT) for key in keys):
        parser.error("Benchmark source maps must be inside the project workspace")
    output = args.output.resolve()
    onsets = args.onsets.resolve()
    rows = []
    modes = MODES if args.include_stems else MODES[:2]
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        jobs = [
            pool.submit(benchmark_song, key, songs[key], onsets / f"{key}.json", output, modes)
            for key in dict.fromkeys(keys)
        ]
        for job in as_completed(jobs):
            rows.extend(job.result())
            publish(output, rows, modes)


if __name__ == "__main__":
    main()
