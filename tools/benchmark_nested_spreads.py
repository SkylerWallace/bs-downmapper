"""Compare parent-relative and Expert+ budgets on complete Standard cascades."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
import csv
import os
from pathlib import Path
import shutil
import signal
from statistics import mean
import subprocess
import sys

from bookmark_files import copy_bookmarks
from beatmap_io import load, read_level, sha, standard_entries, write
from chromapper_export import (
    ENGINE,
    TARGETS,
    NESTED_MODEL,
    automatic_timing_context,
    automatic_timings,
    higher_timings,
    profile_for,
    restrict_timing_source,
    saved_standard_timings,
    timing_conflicts,
)
from mapping_intent import rhythm_signature
from rhythm_inputs import apply_merges
from timing_benchmark import atomic_json, error_message, score


METHODS = ("parent", "expert-plus")


def stage_expert_plus(source, destination, expert):
    """Copy only the Expert+ chart and its assets to an isolated test song."""
    if (destination / "Info.dat").is_file():
        return
    info = load(source / "Info.dat")
    entry = standard_entries(info)["ExpertPlus"]
    assets = {expert["filename"]}
    if "difficultyBeatmaps" in info:
        info["difficultyBeatmaps"] = [entry]
        assets.update(
            (
                entry.get("lightshowDataFilename"),
                info.get("coverImageFilename"),
                info.get("songPreviewFilename"),
                info["audio"]["songFilename"],
                info["audio"]["audioDataFilename"],
            )
        )
    else:
        info["_difficultyBeatmapSets"] = [
            {"_beatmapCharacteristicName": "Standard", "_difficultyBeatmaps": [entry]}
        ]
        assets.update((info["_songFilename"], info["_coverImageFilename"]))
    destination.mkdir(parents=True, exist_ok=True)
    for name in sorted(assets - {None, ""}):
        if Path(name).name != name:
            raise ValueError(f"Non-flat package filename {name!r}.")
        shutil.copyfile(source / name, destination / name)
    copy_bookmarks(source, destination)
    atomic_json(destination / "Info.dat", info)


def generate(song, difficulty, parent, edits, details, timeout):
    args = [
        sys.executable,
        str(ENGINE / "tools/chromapper_export.py"),
        "--source",
        str(song),
        "--source-difficulty",
        "ExpertPlus",
        "--difficulty",
        difficulty,
        "--rhythm-difficulty",
        parent,
        "--rhythm-mode",
        "best-guess",
        "--timing-edits",
        str(edits),
        "--in-place",
        "--expected-info-sha256",
        sha(song / "Info.dat"),
        "--expected-target-sha256",
        "missing",
    ]
    log = details / f"{difficulty}.log"
    with log.open("w", encoding="utf-8") as stream:
        with subprocess.Popen(
            args,
            cwd=ENGINE,
            stdout=stream,
            stderr=subprocess.STDOUT,
            env={**os.environ, "LOCALAPPDATA": str(details)},
            start_new_session=os.name != "nt",
        ) as process:
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    subprocess.run(
                        ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                        stdout=subprocess.DEVNULL,
                        stderr=subprocess.DEVNULL,
                        check=False,
                    )
                else:
                    os.killpg(process.pid, signal.SIGKILL)
                if process.poll() is None:
                    process.kill()
                raise ValueError(f"Export timed out after {timeout} seconds. See {log.name}.")
    if code:
        raise ValueError(error_message(log))


def run_spread(key, source, output, method, model, timeout):
    expert = read_level(source, "ExpertPlus")
    song = output / "candidates" / method / key
    details = output / "details" / method / key
    details.mkdir(parents=True, exist_ok=True)
    stage_expert_plus(source, song, expert)
    if sha(song / expert["filename"]) != sha(source / expert["filename"]):
        raise ValueError(f"{key}: staged Expert+ differs from its original.")
    rows = {}
    parent = "ExpertPlus"
    failure = None
    for difficulty in TARGETS:
        row = {"status": "blocked", "timing_source": parent}
        rows[difficulty] = row
        result = details / f"{difficulty}.json"
        if failure:
            row["error"] = f"Blocked by {failure} export."
            atomic_json(result, row)
            continue
        try:
            authored = read_level(source, difficulty)
            saved = saved_standard_timings(song)
            level = restrict_timing_source(read_level(song, parent), higher_timings(saved, difficulty))
            profile = profile_for(difficulty, None)
            level, protected = automatic_timing_context(level, profile)
            retained, merges = automatic_timings(
                level,
                profile,
                protected,
                timing_model=model,
                difficulty=difficulty,
                budget_reference=expert if method == "expert-plus" else None,
            )
            proposal = apply_merges(level, retained, merges)
            row["proposed_notes"] = len(proposal)
            if difficulty not in saved:
                kept = {note["id"] for note in retained}
                edits = details / f"{difficulty}-edits.json"
                proposed_edits = {
                    "sourceSha256": sha(song / level["filename"]),
                    "removedNoteIds": [n["id"] for n in level["notes"] if n["id"] not in kept],
                    "merges": merges,
                }
                if edits.is_file() and load(edits) != proposed_edits:
                    raise ValueError("Timing edits differ from the saved proposal.")
                write(edits, proposed_edits)
                generate(song, difficulty, parent, edits, details, timeout)
            chart = read_level(song, difficulty)
            if rhythm_signature(chart["notes"]) != rhythm_signature(proposal):
                raise ValueError("Saved chart differs from its timing proposal. Use a fresh output folder.")
            saved = saved_standard_timings(song)
            if timing_conflicts(saved, difficulty, saved[difficulty]):
                raise ValueError("Saved chart has timing hierarchy conflicts.")
            row.update(
                score(authored["notes"], chart["notes"]),
                status="exported",
                authored_sha256=sha(source / authored["filename"]),
            )
        except (ValueError, OSError, KeyError) as error:
            row.update(status="failed", error=str(error))
            failure = difficulty
        atomic_json(result, row)
        parent = difficulty
        detail = (
            f"{row['matched_beats']}/{row['authored_beats']} matches, {row['generated_notes']} notes"
            if row["status"] == "exported"
            else row["error"]
        )
        print(f"{key} | {method} | {difficulty} | {row['status']} | {detail}", flush=True)
    return rows


def publish(output, keys, results):
    comparison = []
    summary = {"requested_maps": len(keys), "completed_spreads": {}, "tiers": {}}
    for method in METHODS:
        spreads = [rows for (key, mode), rows in results.items() if mode == method]
        summary["completed_spreads"][method] = {
            "processed": len(spreads),
            "full_exports": sum(
                all(row["status"] == "exported" for row in rows.values()) for rows in spreads
            ),
            "statuses": dict(Counter(row["status"] for rows in spreads for row in rows.values())),
        }
    for difficulty in TARGETS:
        tier = []
        for key in keys:
            old, new = (results.get((key, method), {}).get(difficulty) for method in METHODS)
            if old is None or new is None:
                continue
            row = {"map": key, "difficulty": difficulty}
            for method, result in zip(METHODS, (old, new)):
                row.update(
                    {
                        f"{method}_status": result["status"],
                        f"{method}_error": result.get("error"),
                        f"{method}_compliance": result.get("beat_compliance"),
                        f"{method}_extra_hits": len(result["extra_beats"])
                        if "extra_beats" in result
                        else None,
                        f"{method}_notes": result.get("generated_notes"),
                    }
                )
            if old["status"] == new["status"] == "exported":
                if old["authored_sha256"] != new["authored_sha256"]:
                    raise ValueError(f"{key} {difficulty}: authored reference changed.")
                row["compliance_delta_pp"] = 100 * (new["beat_compliance"] - old["beat_compliance"])
                row["extra_hits_delta"] = len(new["extra_beats"]) - len(old["extra_beats"])
                tier.append(row)
            comparison.append(row)
        summary["tiers"][difficulty] = {"paired_exports": len(tier)}
        if tier:
            summary["tiers"][difficulty].update(
                {
                    "mean_compliance_delta_pp": mean(row["compliance_delta_pp"] for row in tier),
                    "extra_hits_delta": sum(row["extra_hits_delta"] for row in tier),
                    **{
                        f"{method}_mean_compliance": mean(row[f"{method}_compliance"] for row in tier)
                        for method in METHODS
                    },
                    **{
                        f"{method}_extra_hits": sum(row[f"{method}_extra_hits"] for row in tier)
                        for method in METHODS
                    },
                }
            )
    atomic_json(output / "summary.json", summary)
    if comparison:
        fields = list(dict.fromkeys(key for row in comparison for key in row))
        temporary = output / "comparison.csv.tmp"
        with temporary.open("w", newline="", encoding="utf-8-sig") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            writer.writerows(comparison)
        temporary.replace(output / "comparison.csv")


def retry_inputs(previous_runs, output, keys):
    """Seed completed tiers from a prior run without changing its saved charts."""
    retained, pending = {}, []
    for key in keys:
        for method in METHODS:
            previous = next(
                (
                    run
                    for run in previous_runs
                    if all((run / "details" / method / key / f"{tier}.json").is_file() for tier in TARGETS)
                ),
                None,
            )
            if previous is None:
                raise ValueError(f"No prior results for {key} with {method} budgets.")
            details = previous / "details" / method / key
            rows = {tier: load(details / f"{tier}.json") for tier in TARGETS}
            if all(row["status"] == "exported" for row in rows.values()):
                retained[key, method] = rows
                for tier, row in rows.items():
                    atomic_json(output / "details" / method / key / f"{tier}.json", row)
                continue
            origin = previous / "candidates" / method / key
            destination = output / "candidates" / method / key
            if origin.is_dir() and not destination.exists():
                shutil.copytree(origin, destination)
            new_details = output / "details" / method / key
            new_details.mkdir(parents=True, exist_ok=True)
            for edit in details.glob("*-edits.json"):
                if not (new_details / edit.name).is_file():
                    shutil.copyfile(edit, new_details / edit.name)
            pending.append((key, method))
    return retained, pending


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--timeout", type=int, default=240, help="Maximum seconds per tier export.")
    parser.add_argument(
        "--map", action="append", help="Test only this curated key. Repeat to select several."
    )
    parser.add_argument(
        "--retry-from",
        type=Path,
        action="append",
        help="Reuse prior exports and retry unfinished spreads. List the latest run first.",
    )
    args = parser.parse_args()
    catalog = load(ENGINE / "maps/test-candidates.json")
    curated = {entry["key"] for entry in catalog["maps"] if entry["usage"] == "paired-reference"}
    keys = sorted(set(args.map) if args.map else curated)
    source_root, output = args.source_root.resolve(), args.output.resolve()
    if not set(keys) <= curated or args.workers < 1 or args.timeout < 1:
        parser.error("Select curated keys and positive worker and timeout values.")
    if output == source_root or source_root in output.parents or output in source_root.parents:
        parser.error("Choose a benchmark output folder separate from the source maps.")
    missing = [key for key in keys if not (source_root / key / "Info.dat").is_file()]
    if missing:
        parser.error("Missing local source maps: " + ", ".join(missing))
    output.mkdir(parents=True, exist_ok=True)
    model = NESTED_MODEL
    print(f"{len(keys)} songs × {len(METHODS)} budget methods × {len(TARGETS)} tiers", flush=True)
    results, jobs = {}, [(key, method) for key in keys for method in METHODS]
    if args.retry_from:
        previous = [path.resolve(strict=True) for path in args.retry_from]
        if any(run == output or run in output.parents or output in run.parents for run in previous):
            parser.error("Use a separate output folder for a retry.")
        results, jobs = retry_inputs(previous, output, keys)
        print(
            f"Retained {len(results)} completed spreads. Retrying {len(jobs)} unfinished spreads.", flush=True
        )
        publish(output, keys, results)
    with ThreadPoolExecutor(max_workers=args.workers) as workers:
        pending = {
            workers.submit(run_spread, key, source_root / key, output, method, model, args.timeout): (
                key,
                method,
            )
            for key, method in jobs
        }
        for future in as_completed(pending):
            key, method = pending[future]
            try:
                results[key, method] = future.result()
            except (ValueError, OSError, KeyError) as error:
                print(f"{key} | {method} | setup failed | {error}", flush=True)
                results[key, method] = {d: {"status": "blocked", "error": str(error)} for d in TARGETS}
            publish(output, keys, results)
    print(f"Finished. Results: {output / 'summary.json'}", flush=True)


if __name__ == "__main__":
    main()
