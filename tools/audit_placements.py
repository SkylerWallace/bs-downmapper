"""Read-only geometry screening. Flags are review candidates, not collision proofs."""

import argparse, math, json, hashlib, csv, itertools
from pathlib import Path
from itertools import combinations
from collections import Counter
from analyze_standard_spreads import load, write, notes, groups

VECTORS = {0: (0, 1), 1: (0, -1), 2: (-1, 0), 3: (1, 0), 4: (-1, 1), 5: (1, 1), 6: (-1, -1), 7: (1, -1)}


def segment(n, extent=1.25):
    dx, dy = VECTORS[n["d"]]
    angle = math.radians(n.get("a", 0))
    dx, dy = dx * math.cos(angle) - dy * math.sin(angle), dx * math.sin(angle) + dy * math.cos(angle)
    length = math.hypot(dx, dy)
    dx /= length
    dy /= length
    return ((n["x"] - extent * dx, n["y"] - extent * dy), (n["x"] + extent * dx, n["y"] + extent * dy))


def distance(p, a, b):
    dx = b[0] - a[0]
    dy = b[1] - a[1]
    den = dx * dx + dy * dy
    t = max(0, min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / den)) if den else 0
    return math.hypot(p[0] - a[0] - t * dx, p[1] - a[1] - t * dy)


def segment_distance(a, b, c, d):
    cross = lambda u, v: u[0] * v[1] - u[1] * v[0]
    r = (b[0] - a[0], b[1] - a[1])
    s = (d[0] - c[0], d[1] - c[1])
    den = cross(r, s)
    q = (c[0] - a[0], c[1] - a[1])
    if abs(den) > 1e-9 and 0 <= cross(q, s) / den <= 1 and 0 <= cross(q, r) / den <= 1:
        return 0
    return min(distance(a, c, d), distance(b, c, d), distance(c, a, b), distance(d, a, b))


def check_group(group):
    findings = []
    for a, b in combinations(group, 2):
        same_hand = a["c"] == b["c"]
        detail = {"notes": [a, b]}
        if (a["x"], a["y"]) == (b["x"], b["y"]):
            findings.append({"rule": "G03", "reason": "same-time duplicate cell", **detail})
        if a["d"] == 8 or b["d"] == 8:
            findings.append({"rule": "G04", "reason": "dot swing direction unresolved", **detail})
            continue
        sa, sb = segment(a), segment(b)
        if not same_hand:
            gap = segment_distance(*sa, *sb)
            if gap <= 0.2:
                findings.append(
                    {
                        "rule": "G01",
                        "reason": "opposite-hand pre/cut/follow-through envelopes meet",
                        "proxyDistance": gap,
                        **detail,
                    }
                )
        else:
            # Multiple simultaneous cubes should admit one coherent cut path.
            dx, dy = sa[1][0] - sa[0][0], sa[1][1] - sa[0][1]
            normal = abs((b["x"] - a["x"]) * dy - (b["y"] - a["y"]) * dx) / math.hypot(dx, dy)
            ex, ey = sb[1][0] - sb[0][0], sb[1][1] - sb[0][1]
            aligned = (dx * ex + dy * ey) / (math.hypot(dx, dy) * math.hypot(ex, ey)) >= math.cos(
                math.radians(0.001)
            )
            if not aligned or normal > 0.35:
                findings.append(
                    {
                        "rule": "G02",
                        "reason": "same-hand group lacks a common arrow-aligned line",
                        "perpendicularSeparation": normal,
                        **detail,
                    }
                )
    return findings


def audit(path):
    data = load(path)
    if not data.get("version", "").startswith("4."):
        raise ValueError("Current audit supports v4 only")
    findings = [{"beat": b, **f} for b, g in groups(notes(data)).items() for f in check_group(g)]
    return {
        "source": str(path),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "model": "static simultaneous grid envelopes v0.1",
        "parameters": {
            "extentBeforeAndAfterCells": 1.25,
            "nearEnvelopeDistanceCells": 0.2,
            "sameHandPerpendicularToleranceCells": 0.35,
        },
        "status": "screening_only",
        "counts": dict(Counter(f["rule"] for f in findings)),
        "findings": findings,
        "coverage": [
            "simultaneous opposite-hand envelope proximity",
            "simultaneous same-hand line mismatch",
            "same-time duplicate cells",
            "unresolved dot pairs",
        ],
        "notCovered": [
            "controller/arm/head 3D trajectories",
            "sequential transition collisions",
            "true hitboxes and scoring arcs",
            "bombs/walls/occlusion",
            "arcs/chains",
            "complete biomechanical parity",
        ],
    }


def enumerate_pairs(output):
    output.parent.mkdir(parents=True, exist_ok=True)
    cells = list(itertools.product(range(4), range(3)))
    counts = Counter()
    rows = 0
    with output.open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(
            f, fieldnames=["kind", "c0", "x0", "y0", "d0", "c1", "x1", "y1", "d1", "screeningRules"]
        )
        writer.writeheader()
        for kind, c0, c1, pairs in [
            ("two_hands", 0, 1, itertools.product(cells, repeat=2)),
            ("same_left_hand", 0, 0, itertools.combinations(cells, 2)),
            ("same_right_hand", 1, 1, itertools.combinations(cells, 2)),
        ]:
            for p, q in pairs:
                for d0, d1 in itertools.product(range(8), repeat=2):
                    ns = [
                        {"b": 0, "c": c0, "x": p[0], "y": p[1], "d": d0},
                        {"b": 0, "c": c1, "x": q[0], "y": q[1], "d": d1},
                    ]
                    rules = sorted(set(x["rule"] for x in check_group(ns)))
                    counts.update(rules)
                    writer.writerow(
                        {
                            "kind": kind,
                            "c0": c0,
                            "x0": p[0],
                            "y0": p[1],
                            "d0": d0,
                            "c1": c1,
                            "x1": q[0],
                            "y1": q[1],
                            "d1": d1,
                            "screeningRules": "|".join(rules),
                        }
                    )
                    rows += 1
    return {"configurations": rows, "countsByRule": dict(counts)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("map", type=Path, nargs="?")
    ap.add_argument("output", type=Path, nargs="?")
    ap.add_argument("--enumerate", type=Path)
    args = ap.parse_args()
    if args.enumerate:
        print(json.dumps(enumerate_pairs(args.enumerate)))
        return
    if not args.map or not args.output:
        ap.error("map and output required without --enumerate")
    r = audit(args.map)
    write(args.output, r)
    print(json.dumps(r["counts"]))


if __name__ == "__main__":
    main()
