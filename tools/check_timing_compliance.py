"""Measure automatic or saved timings against local authored Standard charts."""

import argparse
from pathlib import Path
from statistics import mean

from beatmap_io import load, read_level, sha, write
from chromapper_export import (
    DIFFICULTIES,
    ENGINE,
    NESTED_MODEL,
    TARGETS,
    automatic_timings,
    higher_timings,
    prepare_automatic_source,
    profile_for,
    saved_standard_timings,
    timing_conflicts,
)
from rhythm_inputs import apply_merges
from timing_benchmark import score


def catalog_keys():
    maps = ENGINE / "maps"
    paths = [maps / "test-candidates.json", maps / "test-candidates.local.json"]
    return {entry["key"] for path in paths if path.is_file() for entry in load(path)["maps"]}


def evaluate(source, difficulty, model, candidate, *, priority_cache=None):
    saved = saved_standard_timings(source)
    higher = higher_timings(saved, difficulty)
    if difficulty not in saved or not higher:
        return None
    authored = read_level(source, difficulty)
    if candidate is None:
        nested = model == NESTED_MODEL
        highest = next(iter(higher))
        timing_source = highest if nested else next(reversed(higher))
        chart = read_level(source, timing_source)
        profile = profile_for(difficulty, None)
        level, protected = prepare_automatic_source(chart, higher, profile, stable_reference=nested)
        below = DIFFICULTIES[DIFFICULTIES.index(difficulty) + 1:]
        required = {beat for rank in below for beat in saved.get(rank, ())} if nested else set()
        source_hash = sha(source / level["filename"])
        proposal = apply_merges(
            level,
            *automatic_timings(
                level,
                profile,
                protected,
                timing_model=model,
                difficulty=difficulty,
                budget_reference=chart if highest == timing_source else read_level(source, highest),
                required_beats=required,
                priority_cache=priority_cache,
            ),
        )
        reference = saved if nested else higher
    else:
        proposal = read_level(candidate, difficulty)["notes"]
        timing_source = None
        source_hash = None
        reference = saved_standard_timings(candidate)
        if not higher_timings(reference, difficulty):
            raise ValueError(f"Saved {difficulty} needs a higher Standard chart to verify timing nesting.")
    metrics = score(authored["notes"], proposal)
    proposed = [beat for beat, _ in metrics["generated_timing_groups"]]
    conflicts = timing_conflicts(reference, difficulty, proposed)
    return {
        "timing_source": timing_source,
        "timing_source_sha256": source_hash,
        "authored_sha256": sha(source / authored["filename"]),
        **metrics,
        "hierarchy_conflicts": conflicts,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, default=ENGINE / "maps/source")
    parser.add_argument(
        "--candidate-root", type=Path, help="Score saved candidate spreads organized by map key."
    )
    parser.add_argument("--map", action="append", help="Evaluate this catalog key. Repeat for multiple maps.")
    parser.add_argument("--difficulty", action="append", choices=TARGETS)
    parser.add_argument(
        "--timing-model", choices=[NESTED_MODEL], default=NESTED_MODEL
    )
    parser.add_argument("--output", type=Path, default=ENGINE / "maps/reports/timing-compliance.json")
    parser.add_argument(
        "--baseline", type=Path, help="Compare against a prior report on the same authored charts."
    )
    args = parser.parse_args()
    known = catalog_keys()
    keys = sorted(
        set(args.map)
        if args.map
        else {key for key in known if (args.source_root / key / "Info.dat").is_file()}
    )
    if not keys or any(key not in known or Path(key).name != key or key in (".", "..") for key in keys):
        parser.error("Choose catalog keys and place at least one unmodified map under --source-root.")
    if any(not (args.source_root / key / "Info.dat").is_file() for key in keys):
        parser.error("Each selected map needs its original Info.dat under --source-root/<key>.")

    rows = {}
    priority_cache = {}
    for key in keys:
        for difficulty in dict.fromkeys(args.difficulty or TARGETS):
            candidate = args.candidate_root / key if args.candidate_root is not None else None
            row = evaluate(args.source_root / key, difficulty, args.timing_model, candidate,
                           priority_cache=priority_cache)
            if row is None:
                print(f"{key} {difficulty}: no authored pair", flush=True)
                continue
            rows[f"{key}/{difficulty}"] = row
            print(
                f"{key} {difficulty}: {row['beat_compliance']:.2%} beat compliance, "
                f"{len(row['extra_beats'])} extra hits, {row['generated_notes']} notes",
                flush=True,
            )
    if not rows:
        parser.error("No authored difficulty pairs were scored.")
    average = mean(row["beat_compliance"] for row in rows.values())
    report = {
        "mode": "saved_spread" if args.candidate_root else "proposal_from_authored_higher",
        "timing_model": None if args.candidate_root else args.timing_model,
        "mean_beat_compliance": average,
        "extra_beats": sum(len(row["extra_beats"]) for row in rows.values()),
        "rows": rows,
    }
    if args.baseline:
        baseline = load(args.baseline)
        if baseline["mode"] != report["mode"] or baseline["rows"].keys() != rows.keys():
            parser.error("The baseline must use the same evaluation mode and map/difficulty pairs.")
        for key, row in rows.items():
            before = baseline["rows"][key]
            if before["authored_sha256"] != row["authored_sha256"]:
                parser.error(f"The authored reference changed for {key}.")
            if before.get("timing_source_sha256") != row["timing_source_sha256"]:
                parser.error(f"The timing source changed for {key}.")
            row["compliance_delta_pp"] = 100 * (row["beat_compliance"] - before["beat_compliance"])
            row["extra_beats_delta"] = len(row["extra_beats"]) - len(before["extra_beats"])
            row["note_count_delta"] = row["generated_notes"] - before["generated_notes"]
        report["compliance_delta_pp"] = 100 * (average - baseline["mean_beat_compliance"])
    write(args.output, report)
    print(f"Mean beat compliance {average:.2%}. Report saved to {args.output}.")
    if args.baseline:
        print(f"Change from baseline {report['compliance_delta_pp']:+.2f} percentage points.")
    if any(row["hierarchy_conflicts"] for row in rows.values()):
        raise SystemExit("The report contains timing hierarchy conflicts.")


if __name__ == "__main__":
    main()
