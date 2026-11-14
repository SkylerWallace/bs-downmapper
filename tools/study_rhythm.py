"""Describe a human difficulty spread without assuming musical salience."""

import argparse, math
from pathlib import Path
from collections import Counter
from fractions import Fraction
from beatmap_io import read_level
from analyze_standard_spreads import groups, nearest, pair, write


def rhythm(ns, start, end):
    xs = [n for n in ns if start <= n["b"] < end]
    times = sorted(groups(xs))
    return {
        "notes": len(xs),
        "timingGroups": len(times),
        "times": times,
        "gapHistogramBeats": dict(
            Counter(str(Fraction(b - a).limit_denominator(48)) for a, b in zip(times, times[1:]))
        ),
        "handCounts": {str(c): sum(n["c"] == c for n in xs) for c in [0, 1]},
        "simultaneousGroups": sum(len(g) > 1 for g in groups(xs).values()),
        "notesDecoded": xs,
    }


def study(folder, ranges):
    source = read_level(folder, "ExpertPlus")
    reference = read_level(folder, "Expert")
    rows = []
    for start, end in ranges:
        a = rhythm(source["notes"], start, end)
        b = rhythm(reference["notes"], start, end)
        a["retainedRatio"] = 1
        b["retainedRatio"] = b["notes"] / a["notes"] if a["notes"] else None
        rows.append(
            {
                "startBeat": start,
                "endBeatExclusive": end,
                "source": a,
                "reference": b,
                "referenceTimingGroupsInSource": sum(nearest(t, a["times"]) is not None for t in b["times"]),
                "correspondence": pair(a["notesDecoded"], b["notesDecoded"])[0],
                "fourBeatObservations": [
                    {
                        "startBeat": x,
                        "source": rhythm(source["notes"], x, min(x + 4, end)),
                        "reference": rhythm(reference["notes"], x, min(x + 4, end)),
                    }
                    for x in [start + 4 * j for j in range(math.ceil((end - start) / 4))]
                ],
            }
        )
    return {
        "sourceFolder": str(folder),
        "mode": "supervised descriptive comparison; rhythm does not identify instruments",
        "overallNoteRatio": len(reference["notes"]) / len(source["notes"]),
        "ranges": rows,
    }


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--source", required=True, type=Path)
    p.add_argument("--range", action="append", required=True)
    p.add_argument("--output", required=True, type=Path)
    args = p.parse_args()
    report = study(args.source, [tuple(map(float, r.split(":"))) for r in args.range])
    write(args.output, report)
    for row in report["ranges"]:
        print(
            row["startBeat"],
            row["endBeatExclusive"],
            [(k, row[k]["notes"], row[k]["gapHistogramBeats"]) for k in ["source", "reference"]],
        )
