"""Read-only screening with explicit surrogate geometry; no physical certification."""

import argparse, math, hashlib, copy
from pathlib import Path
from collections import Counter
from bisect import bisect_left
from analyze_standard_spreads import load, write, notes, groups
from audit_placements import segment, distance, audit
from beat_clock import BeatClock
from jump_settings import hjd

DEFAULT = {
    "cellMeters": 0.6,
    "bottomRowMeters": 0.8,
    "eyeHeightMeters": 1.65,
    "hitPlaneMeters": 1.3,
    "bladeOffsetMeters": 0.95,
    "headRadiusMeters": 0.14,
    "controllerRadiusMeters": 0.10,
    "shoulderHalfWidthMeters": 0.2,
    "shoulderHeightMeters": 1.45,
    "armReachMeters": 0.85,
    "cutHalfDurationSeconds": 0.12,
    "gripTravelMeters": 0.22,
    "noteWidthMeters": 0.4,
    "visionSamples": 20,
    "minimumOcclusionSeconds": 0.12,
    "armStateSeconds": 0.75,
    "armRecoverySeconds": 0.45,
    "requiredClearLeadSeconds": 0.30,
    "torsoShiftMeters": 0.30,
}


def direction(n):
    from audit_placements import VECTORS

    if n["d"] == 8:
        return None
    x, y = VECTORS[n["d"]]
    a = math.radians(n.get("a", 0))
    x, y = x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)
    l = math.hypot(x, y)
    return x / l, y / l


def shoulder(n, p):
    return ((-1 if n["c"] == 0 else 1) * p["shoulderHalfWidthMeters"], p["shoulderHeightMeters"], 0)


def tip_to_grip(tip, anchor, p):
    # One feasible extension along shoulder->tip, not an inferred actual hand.
    reach = norm(tip, anchor)
    fraction = max(0, (reach - p["bladeOffsetMeters"]) / reach) if reach else 0
    return lerp(anchor, tip, fraction)


def trajectory(n, p):
    v = direction(n)
    if v is None:
        return None
    x = (n["x"] - 1.5) * p["cellMeters"]
    y = p["bottomRowMeters"] + n["y"] * p["cellMeters"]
    z = p["hitPlaneMeters"]
    e = p["gripTravelMeters"]
    anchor = shoulder(n, p)
    return tip_to_grip((x - v[0] * e, y - v[1] * e, z), anchor, p), tip_to_grip(
        (x + v[0] * e, y + v[1] * e, z), anchor, p
    )


def lerp(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def norm(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def distance3(p, a, b):
    v = tuple(y - x for x, y in zip(a, b))
    den = sum(x * x for x in v)
    t = max(0, min(1, sum((p[i] - a[i]) * v[i] for i in range(3)) / den)) if den else 0
    return norm(p, lerp(a, b, t))


def timed_distance(a, b, p):
    ta, tb = trajectory(a, p), trajectory(b, p)
    if ta is None or tb is None:
        return None
    duration = p["cutHalfDurationSeconds"]
    lo = max(a["t"], b["t"]) - duration
    hi = min(a["t"], b["t"]) + duration
    if lo > hi:
        return None

    def point(n, tr, t):
        return lerp(*tr, (t - n["t"] + duration) / (2 * duration))

    aa = point(a, ta, lo)
    bb = point(b, tb, lo)
    az = point(a, ta, hi)
    bz = point(b, tb, hi)
    r = tuple(x - y for x, y in zip(aa, bb))
    v = tuple((x - y) - r[i] for i, (x, y) in enumerate(zip(az, bz)))
    den = sum(x * x for x in v)
    u = max(0, min(1, -sum(x * y for x, y in zip(r, v)) / den)) if den else 0
    return norm(lerp(aa, az, u), lerp(bb, bz, u))


def sequence_findings(ns, p, bombs=None):
    output = []
    last = {}
    state = None
    for beat, g in groups(ns).items():
        t = g[0]["t"]
        active = state and t - state["time"] <= p["armRecoverySeconds"]
        hands = {n["c"] for n in g}
        if active and state["lower"] in hands and state["upper"] not in hands:
            lower = state["lower"]
            upper = state["upper"]
            old = last[lower]
            for n in g:
                if n["c"] != lower:
                    continue
                before = (old["x"], old["y"])
                after = (n["x"], n["y"])
                held = (last[upper]["x"], last[upper]["y"])
                if distance(held, before, after) <= 0.5 or (
                    n["x"] > last[upper]["x"] if lower == 0 else n["x"] < last[upper]["x"]
                ):
                    ambiguous = (
                        n["d"] == 8
                        or old["d"] == 8
                        or last[upper]["d"] == 8
                        or any(state["time"] < bomb["t"] <= t for bomb in (bombs or []))
                    )
                    prior_upper_cut = state.get("entryHands") == [upper]
                    ambiguous = ambiguous or prior_upper_cut
                    output.append(
                        {
                            "rule": "S02" if ambiguous else "S01",
                            "beat": beat,
                            "crossingBeat": state["beat"],
                            "upperHand": upper,
                            "lowerHand": lower,
                            "severity": "unresolved" if ambiguous else "review",
                            "reason": (
                                "crossed pose inferred after upper hand already cut; order not established"
                                if prior_upper_cut
                                else "dot/bomb context invalidates held-arm inference"
                            )
                            if ambiguous
                            else "lower-hand-first resolution of inferred crossed posture",
                            "notes": [old, last[upper], n],
                        }
                    )
        for n in g:
            last[n["c"]] = n
        if (
            len(last) == 2
            and t - min(n["t"] for n in last.values()) <= p["armStateSeconds"]
            and last[0]["x"] > last[1]["x"]
            and abs(last[0]["y"] - last[1]["y"]) >= 0.5
        ):
            upper = max(last, key=lambda c: last[c]["y"])
            state = {"upper": upper, "lower": 1 - upper, "time": t, "beat": beat, "entryHands": sorted(hands)}
        else:
            state = None
    return output


def physical_findings(ns, p):
    out = []
    radius = p["controllerRadiusMeters"]
    head = (0, p["eyeHeightMeters"], 0)
    for i, n in enumerate(ns):
        tr = trajectory(n, p)
        if tr is None:
            continue
        clearance = distance3(head, *tr) - radius - p["headRadiusMeters"]
        if clearance < 0:
            out.append(
                {
                    "rule": "C02",
                    "beat": n["b"],
                    "clearanceMeters": clearance,
                    "reason": "surrogate controller/head capsules overlap",
                    "notes": [n],
                }
            )
        reach = max(norm(shoulder(n, p), x) for x in tr)
        if reach > p["armReachMeters"]:
            out.append({"rule": "C03", "beat": n["b"], "requiredSurrogateReachMeters": reach, "notes": [n]})
        for other in ns[i + 1 :]:
            if other["t"] - n["t"] > 2 * p["cutHalfDurationSeconds"]:
                break
            if other["c"] == n["c"]:
                continue
            d = timed_distance(n, other, p)
            if d is not None and d < 2 * radius:
                out.append(
                    {
                        "rule": "C01",
                        "beat": n["b"],
                        "otherBeat": other["b"],
                        "clearanceMeters": d - 2 * radius,
                        "severity": "unresolved",
                        "reason": "conditional surrogate overlap, not observed controller collision",
                        "notes": [n, other],
                    }
                )
    for finding in out:
        if finding["rule"] == "C03":
            n = finding["notes"][0]
            tip = (
                (n["x"] - 1.5) * p["cellMeters"],
                p["bottomRowMeters"] + n["y"] * p["cellMeters"],
                p["hitPlaneMeters"],
            )
            # Alternative torso shifts demonstrate that the centered posture is not unique.
            possibilities = []
            for shift in [-p["torsoShiftMeters"], 0, p["torsoShiftMeters"]]:
                anchor = list(shoulder(n, p))
                anchor[0] += shift
                v = direction(n)
                targets = [
                    (
                        tip[0] + s * v[0] * p["gripTravelMeters"],
                        tip[1] + s * v[1] * p["gripTravelMeters"],
                        tip[2],
                    )
                    for s in [-1, 1]
                ]
                possibilities.append(max(norm(anchor, tip_to_grip(t, anchor, p)) for t in targets))
            finding["alternativePostureMinReachMeters"] = min(possibilities)
            finding["severity"] = (
                "posture_sensitive" if min(possibilities) <= p["armReachMeters"] else "unresolved"
            )
    return out


def ray_box(eye, target, low, high):
    near, far = 0.0, 1.0
    for i in range(3):
        delta = target[i] - eye[i]
        if abs(delta) < 1e-9:
            if not low[i] <= eye[i] <= high[i]:
                return False
        else:
            a, b = (low[i] - eye[i]) / delta, (high[i] - eye[i]) / delta
            near = max(near, min(a, b))
            far = min(far, max(a, b))
            if near > far:
                return False
    return near < 1 - 1e-6 and far > 0


def vision_findings(ns, walls, njs, rt, p, *, target_index=None):
    out = []
    times = [n["t"] for n in ns]
    width = p["noteWidthMeters"]
    half = width / 2
    eye = (0, p["eyeHeightMeters"], 0)
    indices = range(len(ns)) if target_index is None else (target_index,)
    for idx in indices:
        n = ns[idx]
        start = n["t"] - rt
        end = n["t"] - 0.10
        if end <= start:
            continue
        blockers = ns[bisect_left(times, n["t"] - rt) : idx]
        hits = {}
        step = (end - start) / p["visionSamples"]
        if not blockers and not walls:
            continue
        for j in range(p["visionSamples"]):
            t = start + (j + 0.5) * step
            target = (
                (n["x"] - 1.5) * p["cellMeters"],
                p["bottomRowMeters"] + n["y"] * p["cellMeters"],
                p["hitPlaneMeters"] + njs * (n["t"] - t),
            )
            for k, b in enumerate(blockers):
                if not b["t"] - rt <= t < b["t"] or abs(b["t"] - n["t"]) < 1e-5:
                    continue
                x = (b["x"] - 1.5) * p["cellMeters"]
                y = p["bottomRowMeters"] + b["y"] * p["cellMeters"]
                z = p["hitPlaneMeters"] + njs * (b["t"] - t)
                if ray_box(eye, target, (x - half, y - half, z - half), (x + half, y + half, z + half)):
                    hits.setdefault(("note", k), []).append(t)
            for k, w in enumerate(walls):
                if t > w["end"] or t < w["start"] - rt:
                    continue
                lo = (
                    (w["x"] - 2) * p["cellMeters"],
                    p["bottomRowMeters"] + (w["y"] - 0.5) * p["cellMeters"],
                    max(0.01, p["hitPlaneMeters"] + njs * (w["start"] - t)),
                )
                hi = (
                    (w["x"] + w["w"] - 2) * p["cellMeters"],
                    p["bottomRowMeters"] + (w["y"] + w["h"] - 0.5) * p["cellMeters"],
                    p["hitPlaneMeters"] + njs * (w["end"] - t),
                )
                if hi[2] > 0 and ray_box(eye, target, lo, hi):
                    hits.setdefault(("wall", k), []).append(t)
        for (kind, k), sample_times in hits.items():
            duration = len(sample_times) * step
            if duration >= p["minimumOcclusionSeconds"]:
                lead = max(0, n["t"] - (max(sample_times) + step / 2))
                finding = {
                    "rule": "V01" if kind == "note" else "V02",
                    "beat": n["b"],
                    "occluderBeat": blockers[k]["b"] if kind == "note" else walls[k]["b"],
                    "sampledOccludedSeconds": duration,
                    "estimatedClearLeadSeconds": lead,
                    "severity": "early_occlusion_info"
                    if lead >= p["requiredClearLeadSeconds"]
                    else "late_visibility_review",
                    "target": n,
                    "reason": "target-center ray blocked in fixed-eye surrogate",
                }
                if kind == "note" and "id" in blockers[k]:
                    finding["occluderNoteId"] = blockers[k]["id"]
                out.append(finding)
    return out


def run(folder, difficulty, profile=None):
    p = {**DEFAULT, **(profile or {})}
    info = load(folder / "Info.dat")
    meta = next(
        d
        for d in info["difficultyBeatmaps"]
        if d["characteristic"] == "Standard" and d["difficulty"] == difficulty
    )
    path = folder / meta["beatmapDataFilename"]
    data = load(path)
    audio = load(folder / info["audio"]["audioDataFilename"])
    if not data.get("version", "").startswith("4."):
        raise ValueError("Current runner supports v4 only")
    if data.get("arcs") or data.get("chains"):
        raise ValueError("Arc/chain trajectory support not implemented")
    if data.get("njsEvents"):
        raise ValueError("Visibility preview requires constant NJS")
    seconds = BeatClock(info["audio"]["bpm"], audio).seconds
    ns = notes(data)
    for n in ns:
        n["t"] = seconds(n["b"])
    duration = hjd(info["audio"]["bpm"], meta["noteJumpMovementSpeed"], meta["noteJumpStartBeatOffset"])
    rt = duration * 60 / info["audio"]["bpm"]
    walls = []
    for e in data.get("obstacles", []):
        d = data["obstaclesData"][e.get("i", 0)]
        b = e.get("b", 0)
        walls.append(
            {
                "b": b,
                "start": seconds(b),
                "end": seconds(b + d.get("d", 0)),
                **{k: d.get(k, 0) for k in ["x", "y", "w", "h"]},
            }
        )
    bombs = [{"t": seconds(e.get("b", 0))} for e in data.get("bombNotes", [])]
    findings = (
        sequence_findings(ns, p, bombs)
        + physical_findings(ns, p)
        + vision_findings(ns, walls, meta["noteJumpMovementSpeed"], rt, p)
    )
    return {
        "difficulty": difficulty,
        "characteristic": "Standard",
        "source": str(path),
        "sourceSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "model": "motion-visibility-surrogate v0.2",
        "status": "review_required_not_physical_certification",
        "profile": p,
        "approach": {
            "njs": meta["noteJumpMovementSpeed"],
            "offset": meta["noteJumpStartBeatOffset"],
            "halfJumpBeats": duration,
            "reactionSeconds": rt,
        },
        "staticCounts": audit(path)["counts"],
        "counts": dict(Counter(f["rule"] for f in findings)),
        "severityCounts": dict(Counter(f.get("severity", "review") for f in findings)),
        "findings": findings,
        "unresolvedDots": sum(n["d"] == 8 for n in ns),
        "limitations": [
            "note-derived controller surrogates are not tracked player hands",
            "arm ordering inferred from note coordinates; torso/wrist strategies unknown",
            "fixed eye and linear flight; no jump arc, player sway or crouch simulation",
            "target-center visibility only; durations are sampled totals not continuous intervals",
            "no bomb-body/saber collision or environment geometry",
            "no safety/comfort certificate",
        ],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", type=Path)
    ap.add_argument("difficulty", choices=["Expert", "ExpertPlus"])
    ap.add_argument("output", type=Path)
    ap.add_argument("--profile", type=Path)
    args = ap.parse_args()
    r = run(args.folder, args.difficulty, load(args.profile) if args.profile else None)
    write(args.output, r)
    print(r["difficulty"], r["approach"], r["counts"])


if __name__ == "__main__":
    main()
