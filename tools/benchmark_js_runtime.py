"""Compare portable JavaScript runtimes on the same automatic Standard spreads."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import os
from pathlib import Path
import shutil
import subprocess
import sys
from time import perf_counter

from beatmap_io import load, read_level, sha
from benchmark_nested_spreads import stage_expert_plus
from chromapper_export import TARGETS, saved_standard_timings, timing_conflicts
from mapping_intent import rhythm_signature
from timing_benchmark import atomic_json, error_message, score


def run_spread(reference, song, report, engine, python, path, timeout, fixed_rhythms):
    expert_plus = read_level(reference, "ExpertPlus")
    if fixed_rhythms:
        shutil.copytree(fixed_rhythms, song)
        assert sha(song / expert_plus["filename"]) == sha(reference / expert_plus["filename"])
    else:
        stage_expert_plus(reference, song, expert_plus)
    report.mkdir(parents=True, exist_ok=True)
    plan = report / "plan.json"
    atomic_json(plan, {
        "infoSha256": sha(song / "Info.dat"),
        "targets": [
            {
                "difficulty": tier, "foundation": "ExpertPlus", "mode": "authored", "rhythm": tier,
                "targetSha256": sha(song / read_level(song, tier)["filename"]),
            } if fixed_rhythms else {
                "difficulty": tier, "foundation": "ExpertPlus", "mode": "best-guess", "targetSha256": "missing",
            }
            for tier in TARGETS
        ],
    })
    log = report / "generation.log"
    env = {**os.environ, "PATH": path, "LOCALAPPDATA": str(report), "BS_DOWNMAP_MODEL_DIR": str(report / "models")}
    start = perf_counter()
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.Popen(
            [str(python), str(engine / "tools/chromapper_export.py"), "--source", str(song), "--batch-plan", str(plan)],
            cwd=engine, stdout=stream, stderr=subprocess.STDOUT, env=env,
        )
        try:
            exit_code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, check=False)
            else:
                process.kill()
            process.wait()
            return {"seconds": perf_counter() - start, "failure": f"Timed out after {timeout} seconds"}
    elapsed = perf_counter() - start
    if exit_code:
        return {"seconds": elapsed, "failure": error_message(log)}
    saved = saved_standard_timings(song)
    tiers = {}
    runs = report / "bs-downmap" / "runs"
    for tier in TARGETS:
        chart = read_level(song, tier)
        authored = read_level(reference, tier)
        run, = runs.glob(f"*- Downmap {tier} *")
        approved = read_level(run / "rhythm-input", load(run / "rhythm-input.json")["sourceDifficulty"])
        assert rhythm_signature(chart["notes"]) == rhythm_signature(approved["notes"])
        assert not timing_conflicts(saved, tier, saved[tier])
        metrics = score(authored["notes"], chart["notes"])
        tiers[tier] = {
            "chartSha256": sha(song / chart["filename"]),
            "rhythmSha256": sha(approved["folder"] / approved["filename"]),
            "reports": {
                name: sha(file) for file in run.glob("*.json")
                if (name := file.name).endswith("-parity.json") or name in (
                    "placement-policy.json", "placement-search.json", "direction-search.json",
                )
            },
            "notes": metrics["generated_notes"],
            "beatCompliance": metrics["beat_compliance"],
            "extraHits": len(metrics["extra_beats"]),
            "parity": {
                name: {field: data[field] for field in ("errors", "warnings")}
                for file in run.glob("*-parity.json")
                if (name := file.stem) and (data := load(file))
            },
        }
    return {"seconds": elapsed, "tiers": tiers}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--maps", type=Path, required=True)
    parser.add_argument("--engine", type=Path, required=True)
    parser.add_argument("--python", type=Path, default=Path(sys.executable))
    parser.add_argument("--reports", type=Path, required=True)
    parser.add_argument("--candidates", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--key", action="append", help="Limit the benchmark to these curated maps.")
    parser.add_argument("--fixed-rhythms", type=Path, help="Optional folder containing approved Standard spreads.")
    args = parser.parse_args()
    report_root = args.reports.resolve()
    summary = report_root / "summary.json"
    results = load(summary) if summary.is_file() else {}
    candidates = args.candidates.resolve()
    engine = args.engine.resolve(strict=True)
    path = os.environ["PATH"]
    node = engine / ".tools" / "node-runtime"
    if (node / "node.exe").is_file():
        path = str(node) + os.pathsep + path
    catalog = load(Path(__file__).resolve().parents[1] / "maps/test-candidates.json")
    keys = [
        row["key"] for row in catalog["maps"]
        if row["usage"] == "paired-reference" and (not args.key or row["key"] in args.key)
    ]
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {
            pool.submit(
                run_spread, args.maps.resolve() / key, candidates / key, report_root / key, engine,
                args.python.resolve(strict=True), path, args.timeout,
                args.fixed_rhythms.resolve() / key if args.fixed_rhythms else None,
            ): key
            for key in keys if key not in results
        }
        for future in as_completed(futures):
            key = futures[future]
            try:
                result = future.result()
            except (AssertionError, OSError, ValueError) as error:
                result = {"failure": repr(error)}
            results[key] = result
            atomic_json(summary, results)
            print(f"{key} | {result.get('seconds', 0):.1f}s | {result.get('failure', 'full spread exported')}", flush=True)
    if any("failure" in result for result in results.values()):
        sys.exit(1)


if __name__ == "__main__":
    main()
