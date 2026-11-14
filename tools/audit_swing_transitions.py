"""Experimental same-hand transition diagnostics; thresholds are project hypotheses."""

import argparse, math
from pathlib import Path
from collections import Counter
from analyze_standard_spreads import load, write, notes, groups
from beat_clock import BeatClock

ANGLES = {0: 90, 1: 270, 2: 180, 3: 0, 4: 135, 5: 45, 6: 225, 7: 315}


def wrap(a):
    return (a + 180) % 360 - 180


def rotation_lower_bound(a, b):
    if a["d"] == 8 or b["d"] == 8:
        return None
    return abs(wrap(ANGLES[b["d"]] + b.get("a", 0) - ANGLES[a["d"]] - a.get("a", 0) - 180))


def allowance(dt):
    # Provisional Expert screening budgets; not measured human capabilities.
    return 0 if dt <= 0.125 else 45 if dt <= 0.25 else 90 if dt <= 0.5 else 135


def transitions(ns, audio, bombs=None):
    out = []
    clock = BeatClock(120, audio)
    for c in [0, 1]:
        seq = list(groups([n for n in ns if n["c"] == c]).values())
        for a, b in zip(seq, seq[1:]):
            before = a[-1]
            after = b[0]
            dt = clock.gap(before["b"], after["b"])
            r = rotation_lower_bound(before, after)
            reset = any(before["b"] < x <= after["b"] for x in (bombs or []))
            move = math.hypot(after["x"] - before["x"], after["y"] - before["y"])
            out.append(
                {
                    "hand": c,
                    "fromBeat": before["b"],
                    "toBeat": after["b"],
                    "gapSeconds": dt,
                    "gapBeats": after["b"] - before["b"],
                    "alternatingParityRotationLowerBoundDegrees": r,
                    "provisionalAllowanceDegrees": allowance(dt),
                    "rotationOverBudget": None if r is None else r > allowance(dt),
                    "rotationRateLowerBoundDegPerSecond": None if r is None else r / dt,
                    "gridTravelCells": move,
                    "gridTravelCellsPerSecond": move / dt,
                    "bombContext": reset,
                    "state": "dot_ambiguous"
                    if r is None
                    else "reset_context_review"
                    if reset or dt > 0.75
                    else "time_rotation_review"
                    if r > allowance(dt)
                    else "within_provisional_budget",
                    "notes": [before, after],
                }
            )
    return out


def horizontal_runs(ns, audio):
    found = []
    clock = BeatClock(120, audio)
    for c in [0, 1]:
        run = []

        def flush():
            if len(run) >= 4:
                found.append(
                    {
                        "rule": "T02",
                        "hand": c,
                        "startBeat": run[0]["b"],
                        "endBeat": run[-1]["b"],
                        "notes": list(run),
                        "reason": "sustained horizontal same-hand stream; Expert audience review independent of rotation",
                    }
                )

        for g in groups([n for n in ns if n["c"] == c]).values():
            n = g[0]
            gap = clock.gap(run[-1]["b"], n["b"]) if run else 0
            if n["d"] not in [2, 3] or (run and gap > 0.30):
                flush()
                run = []
            if n["d"] in [2, 3]:
                run.append(n)
        flush()
    return found


def run(folder, difficulty):
    info = load(folder / "Info.dat")
    meta = next(
        m
        for m in info["difficultyBeatmaps"]
        if m["characteristic"] == "Standard" and m["difficulty"] == difficulty
    )
    data = load(folder / meta["beatmapDataFilename"])
    audio = load(folder / info["audio"]["audioDataFilename"])
    ns = notes(data)
    ts = transitions(ns, audio, [e.get("b", 0) for e in data.get("bombNotes", [])])
    return {
        "model": "time-aware-transition-proxy v0.1",
        "difficulty": difficulty,
        "parameters": {
            "allowanceTableSecondsDegrees": [[0.125, 0], [0.25, 45], [0.5, 90], ["over .5", 135]],
            "horizontalRunMinimumGroups": 4,
            "horizontalMaxGapSeconds": 0.3,
        },
        "counts": dict(Counter(t["state"] for t in ts)),
        "transitions": ts,
        "horizontalRuns": horizontal_runs(ns, audio),
        "limitations": [
            "rotation lower bound assumes alternating parity and shortest path; not inferred wrist rotation",
            "cannot distinguish 90 vs 270 degree route or determine curvature without reliable hand-state history",
            "grid travel is not actual controller displacement",
            "time budgets uncalibrated; not acceptance criteria",
            "horizontal direction alone does not establish collision",
        ],
    }


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", type=Path)
    ap.add_argument("difficulty")
    ap.add_argument("output", type=Path)
    a = ap.parse_args()
    r = run(a.folder, a.difficulty)
    write(a.output, r)
    print(r["counts"], "horizontal runs", len(r["horizontalRuns"]))
