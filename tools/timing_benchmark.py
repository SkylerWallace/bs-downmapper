"""Score and export automatic Easy timing proposals for local reference charts."""

import os
import shutil
import subprocess
import sys
from collections import Counter
from pathlib import Path
from tempfile import TemporaryDirectory
from time import monotonic

from analyze_standard_spreads import TOL, groups
from beatmap_io import load, read_level, sha, standard_entries, write


ROOT = Path(__file__).resolve().parents[1]
COUNT_FIELDS = (
    "authored_beats",
    "generated_beats",
    "matched_beats",
    "authored_notes",
    "generated_notes",
    "matched_notes",
)
PLACEMENT_ERRORS = (
    "No geometric direction domain",
    "No joint geometry",
    "No supported direction path",
    "No source-relative geometry",
    "No legal placement",
)


def command(script, args, log, env=None):
    with log.open("w", encoding="utf-8") as stream:
        return subprocess.run(
            [sys.executable, str(ROOT / "tools" / script), *map(str, args)],
            cwd=ROOT,
            stdout=stream,
            stderr=subprocess.STDOUT,
            env=env,
        ).returncode


def error_message(log):
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
    for line in reversed(lines):
        if line.startswith("DOWNMAP_ERROR:"):
            return line.removeprefix("DOWNMAP_ERROR:").strip()
    for line in reversed(lines):
        if line.startswith(
            ("ValueError: ", "PlacementDeadEnd: ", "DirectionDeadEnd: ", "AssertionError: ", "Error: ")
        ):
            return line.split(": ", 1)[1]
    return next((line for line in reversed(lines) if line.strip()), "No diagnostics")


def remove_side_walls(rhythm):
    level = read_level(rhythm)
    path, data = rhythm / level["filename"], level["raw"]
    key = "_obstacles" if level["version"].startswith("2.") else "obstacles"
    obstacles = data.get(key, [])

    def side_wall(wall):
        if key == "_obstacles":
            return wall["_type"] == 0 and wall["_width"] < 4
        if level["version"].startswith("4."):
            wall = data["obstaclesData"][wall.get("i", 0)]
        return wall.get("y", 0) == 0 and wall.get("h", 0) >= 3 and wall.get("w", 0) < 4

    data[key] = [wall for wall in obstacles if not side_wall(wall)]
    removed = len(obstacles) - len(data[key])
    if removed:
        write(path, data)
    return removed


def rates(counts):
    return {
        key: counts[matched] / counts[denominator] if counts[denominator] else None
        for key, matched, denominator in (
            ("beat_compliance", "matched_beats", "authored_beats"),
            ("beat_precision", "matched_beats", "generated_beats"),
            ("note_compliance", "matched_notes", "authored_notes"),
            ("note_precision", "matched_notes", "generated_notes"),
        )
    }


def score(authored, proposed):
    a = [(beat, len(notes)) for beat, notes in groups(authored).items()]
    g = [(beat, len(notes)) for beat, notes in groups(proposed).items()]
    i = j = matched_beats = matched_notes = 0
    missing, extra = [], []
    while i < len(a) and j < len(g):
        gap = a[i][0] - g[j][0]
        if abs(gap) <= TOL + 1e-10:
            matched_beats += 1
            matched_notes += min(a[i][1], g[j][1])
            i += 1
            j += 1
        elif gap < 0:
            missing.append(a[i][0])
            i += 1
        else:
            extra.append(g[j][0])
            j += 1
    counts = dict(
        zip(COUNT_FIELDS, (len(a), len(g), matched_beats, len(authored), len(proposed), matched_notes))
    )
    return {
        **counts,
        **rates(counts),
        "authored_timing_groups": a,
        "generated_timing_groups": g,
        "missed_beats": missing + [beat for beat, _ in a[i:]],
        "extra_beats": extra + [beat for beat, _ in g[j:]],
    }


def benchmark(source, output, details, work, row, saved_timings=None, *, onset_cache=None, onset_mode="mix"):
    info = load(source / "Info.dat")
    row["title"] = info["song"]["title"] if "song" in info else info["_songName"]
    missing = {"ExpertPlus", "Easy"} - standard_entries(info).keys()
    if missing:
        row.update(status="skipped", error="Missing Standard " + ", ".join(sorted(missing)))
        return
    authored = read_level(source, "Easy")["notes"]
    staging = work / "exports"
    staging.mkdir()
    log = details / "stock.log"
    args = [
        "--source",
        source,
        "--source-difficulty",
        "ExpertPlus",
        "--difficulty",
        "Easy",
        "--rhythm-mode",
        "best-guess",
        "--output-root",
        staging,
        "--independent-timings",
    ]
    if onset_cache is not None:
        args.extend(["--onset-cache", onset_cache, "--onset-mode", onset_mode])
    else:
        args.extend(["--onset-mode", "chart"])
    code = command("chromapper_export.py", args, log, {**os.environ, "LOCALAPPDATA": str(work)})
    run = next((work / "bs-downmap" / "runs").glob("*"), None)
    if run and (run / "placement-search.json").is_file():
        shutil.copyfile(run / "placement-search.json", details / "stock-placement-search.json")
    if run and (run / "joint-link-repairs.json").is_file():
        shutil.copyfile(run / "joint-link-repairs.json", details / "stock-joint-link-repairs.json")
    rhythm = run / "rhythm-input" if run else None
    if rhythm is None or not (rhythm / "Info.dat").is_file():
        row.update(status="failed", error=error_message(log))
        return
    rhythm_level = read_level(rhythm)
    proposed = rhythm_level["notes"]
    row["rhythm_njs_events"] = len(rhythm_level["raw"].get("njsEvents", []))
    row.update(score(authored, proposed))
    row["status"] = "preview_only"
    row["stock_error"] = error_message(log) if code else ""
    if saved_timings is not None:
        row["saved_timing_verified"] = row["generated_timing_groups"] == saved_timings
        if not row["saved_timing_verified"]:
            row.update(status="timing_changed", error="Automatic timing differs from saved proposal")
            return
    candidate = next(staging.iterdir()) if not code else None
    build_dir = run
    if code and any(error in row["stock_error"] for error in PLACEMENT_ERRORS):
        removed = remove_side_walls(rhythm)
        if removed:
            row["retry_side_walls_removed"] = removed
            retry = work / "retry"
            for step, args in (
                (
                    "build",
                    [
                        "--source",
                        source,
                        "--source-difficulty",
                        "ExpertPlus",
                        "--rhythm-source",
                        rhythm,
                        "--rhythm-difficulty",
                        "ExpertPlus",
                        "--difficulty",
                        "Easy",
                        "--repair-joint-links",
                        "--title-suffix",
                        "Automatic Easy benchmark",
                    ],
                ),
                ("freeze", []),
            ):
                log = details / f"side-wall-{step}.log"
                if command("downmap.py", [step, *args, "--output", retry], log):
                    row["retry_error"] = error_message(log)
                    break
            else:
                candidate = retry / "candidate"
                build_dir = retry
            if (retry / "placement-search.json").is_file():
                shutil.copyfile(retry / "placement-search.json", details / "side-wall-placement-search.json")
            if (retry / "joint-link-repairs.json").is_file():
                shutil.copyfile(
                    retry / "joint-link-repairs.json", details / "side-wall-joint-link-repairs.json"
                )
    if candidate is not None:
        exported = read_level(candidate, "Easy")
        signature = lambda notes: Counter((n["b"], n["c"]) for n in notes)
        if signature(exported["notes"]) != signature(proposed):
            row["error"] = "Export did not preserve the automatic timing and hand counts"
            return
        destination = output / "candidates" / row["song_id"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists():
            # Recover an export moved into place just before an interrupted checkpoint.
            if read_level(destination, "Easy")["raw"] != exported["raw"]:
                raise FileExistsError(f"Existing candidate differs from rebuilt chart: {destination}")
        else:
            candidate.rename(destination)
        draft = load(build_dir / "draft.json")
        repairs = draft.get("emptyDomainRepairs", [])
        link_repairs = draft.get("jointLinkRepairs", [])
        row.update(
            domain_repair_count=len(repairs),
            domain_repairs=repairs,
            removed_joint_links=sum(len(r["removedLinks"]) for r in link_repairs),
            removed_visibility_links=sum(
                len(r["removedLinks"]) for r in link_repairs if "releasedForVisibility" in r
            ),
        )
        row.update(
            status="exported" if not code else "exported_without_side_walls",
            candidate=str(destination.relative_to(output)),
        )


def atomic_json(path, data):
    temporary = path.with_suffix(".tmp")
    write(temporary, data)
    temporary.replace(path)


def aggregate(scored):
    counts = {field: sum(row[field] for row in scored) for field in COUNT_FIELDS}
    return {"songs": len(scored), **counts, **rates(counts)}


def run_song(output, key, source, saved_timings=None, *, onset_cache=None, onset_mode="mix"):
    started = monotonic()
    details = output / "details" / key
    row = {"song_id": key, "source": str(source.relative_to(ROOT))}
    try:
        # Each conversion owns its scratch directory, logs, and candidate.
        with TemporaryDirectory(prefix="working-", dir=output) as directory:
            benchmark(
                source,
                output,
                details,
                Path(directory),
                row,
                saved_timings,
                onset_cache=onset_cache,
                onset_mode=onset_mode,
            )
    except (ValueError, KeyError, IndexError, OSError) as error:
        row.update(status="preview_only" if "matched_beats" in row else "failed", error=str(error))
    row["duration_seconds"] = round(monotonic() - started, 2)
    return row
