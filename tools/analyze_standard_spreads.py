"""Descriptive, non-destructive comparison of Standard spreads."""

import json
from collections import Counter, defaultdict
from itertools import permutations
from bisect import bisect_left

DIFFS = ["Easy", "Normal", "Hard", "Expert", "ExpertPlus"]
TOL = 0.0001


def load(p):
    return json.loads(p.read_text(encoding="utf-8-sig"))


def write(p, d):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(d, indent=2), encoding="utf8")


def notes(data):
    return sorted(
        [
            {
                "b": float(e.get("b", 0)),
                **{k: data["colorNotesData"][e.get("i", 0)].get(k, 0) for k in ["x", "y", "c", "d", "a"]},
            }
            for e in data["colorNotes"]
        ],
        key=lambda n: (n["b"], n["c"], n["x"], n["y"], n["d"]),
    )


def groups(ns):
    result = defaultdict(list)
    anchor = None
    for n in sorted(ns, key=lambda n: n["b"]):
        beat = round(n["b"], 5)
        if anchor is None or beat - anchor > TOL:
            anchor = beat
        result[anchor].append(n)
    return dict(result)


def nearest(b, keys):
    i = bisect_left(keys, b)
    candidates = keys[max(0, i - 1) : i + 1]
    return (
        min(candidates, key=lambda x: abs(x - b))
        if candidates and min(abs(x - b) for x in candidates) <= TOL
        else None
    )


def seconds(b, audio):
    regions = audio["bpmData"]
    rate = audio["songFrequency"]
    r = next((r for r in regions if r.get("sb", 0) <= b <= r["eb"]), regions[0] if b < 0 else regions[-1])
    sb = r.get("sb", 0)
    si = r.get("si", 0)
    return (si + (b - sb) * (r["ei"] - si) / (r["eb"] - sb)) / rate


def peak(times, window):
    j = 0
    best = 0
    for i, t in enumerate(times):
        while j < len(times) and times[j] < t + window - 1e-9:
            j += 1
        best = max(best, j - i)
    return best / window


def metrics(ns, data, audio, meta):
    gs = groups(ns)
    count = len(ns)
    times = sorted(seconds(n["b"], audio) for n in ns)
    pct = lambda v: round(100 * v / count, 3) if count else 0
    return {
        "notes": count,
        "timingGroups": len(gs),
        "notesPerSecondFullAudio": round(count / (audio["songSampleCount"] / audio["songFrequency"]), 3),
        "peak1sNotesPerSecond": peak(times, 1),
        "peak2sNotesPerSecond": peak(times, 2),
        "peak4sNotesPerSecond": peak(times, 4),
        "topRowPct": pct(sum(n["y"] == 2 for n in ns)),
        "middleRowPct": pct(sum(n["y"] == 1 for n in ns)),
        "diagonalPct": pct(sum(n["d"] in [4, 5, 6, 7] for n in ns)),
        "dotPct": pct(sum(n["d"] == 8 for n in ns)),
        "oppositeSidePct": pct(
            sum((n["c"] == 0 and n["x"] >= 2) or (n["c"] == 1 and n["x"] <= 1) for n in ns)
        ),
        "bothHandTimingGroups": sum(len(set(n["c"] for n in g)) == 2 for g in gs.values()),
        "multiNoteSameHandGroups": sum(sum(n["c"] == c for n in g) > 1 for g in gs.values() for c in [0, 1]),
        "bombs": len(data.get("bombNotes", [])),
        "walls": len(data.get("obstacles", [])),
        "arcs": len(data.get("arcs", [])),
        "chains": len(data.get("chains", [])),
        "njs": meta["noteJumpMovementSpeed"],
        "jumpOffset": meta["noteJumpStartBeatOffset"],
    }


def pair(a, b):
    """Maximum same-time/same-hand matching, minimizing attribute disagreement.

    This is correspondence, not an assertion of the mapper's edit history.
    """
    ag = groups(a)
    bg = groups(b)
    keys = sorted(ag)
    counts = Counter()
    details = []
    for beat, target in bg.items():
        sourcebeat = nearest(beat, keys)
        source = ag.get(sourcebeat, [])
        if sourcebeat is not None:
            keys.remove(sourcebeat)
        if sourcebeat is None:
            counts["targetTimingGroupsAbsentInSource"] += 1
        for color in [0, 1]:
            aa = [n for n in source if n["c"] == color]
            bb = [n for n in target if n["c"] == color]
            matched = []
            if aa and bb:
                # In this corpus each hand's simultaneous groups are small.
                if len(aa) <= len(bb):
                    candidates = (list(zip(aa, perm)) for perm in permutations(bb, len(aa)))
                else:
                    candidates = (list(zip(perm, bb)) for perm in permutations(aa, len(bb)))

                def cost(ps):
                    return sum(
                        (x["x"] != y["x"] or x["y"] != y["y"]) + (x["d"] != y["d"] or x["a"] != y["a"])
                        for x, y in ps
                    )

                matched = min(candidates, key=cost)
            counts["sameTimeSameHandPairs"] += len(matched)
            counts["targetNotesWithoutSameTimeSameHandPair"] += len(bb) - len(matched)
            for x, y in matched:
                moved = (x["x"], x["y"]) != (y["x"], y["y"])
                redirected = (x["d"], x["a"]) != (y["d"], y["a"])
                label = (
                    "movedAndRedirected"
                    if moved and redirected
                    else "movedOnly"
                    if moved
                    else "redirectedOnly"
                    if redirected
                    else "unchanged"
                )
                counts[label] += 1
                details.append({"source": x, "target": y, "classification": label})
    counts["sourceNotesWithoutSameTimeSameHandPair"] = len(a) - counts["sameTimeSameHandPairs"]
    counts["sourceNotes"] = len(a)
    counts["targetNotes"] = len(b)
    counts["targetTimingGroups"] = len(bg)
    counts["targetTimingGroupsPresentInSource"] = len(bg) - counts["targetTimingGroupsAbsentInSource"]
    return dict(counts), details
