"""Source-structure hypotheses, not proof of musical intent.

No song names or absolute beat ranges. User-supplied rhythms are immutable.
"""

import copy, math
from collections import Counter
from analyze_standard_spreads import groups, nearest
from audit_swing_transitions import rotation_lower_bound
from geometry_policy import crossover_depth
from beat_clock import BeatClock, clock_for


def rhythm_signature(ns):
    return sorted((float(n["b"]), n["c"]) for n in ns)


def seed_from_rhythm(source, rhythm, source_anchors=(), rhythm_anchors=()):
    """Use ONLY supplied time/hand/multiplicity; derive positions from source.

    New hands or added emphasis objects use a same-time other-hand seed when
    needed. If the target introduces a time absent from source, use the nearest
    source event and explicitly report that lower-confidence extrapolation.
    """
    sg = groups(source)
    times = sorted(sg)
    used = Counter()
    seeded = []
    log = []
    anchored_groups = {(n["b"], n["c"]) for n in rhythm if n["id"] in rhythm_anchors}
    for r in sorted(rhythm, key=lambda n: (n["b"], n["c"], n["id"] not in rhythm_anchors, n["id"])):
        b, c = r["b"], r["c"]
        t = nearest(b, times)
        exact = t is not None
        if t is None:
            t = min(times, key=lambda t: abs(t - b))
        same = [n for n in sg[t] if n["c"] == c]
        other = [n for n in sg[t] if n["c"] != c]
        if (b, c) in anchored_groups:
            same.sort(key=lambda n: n["id"] not in source_anchors)
        pool = same + other
        j = used[(b, c)]
        used[(b, c)] += 1
        seed = pool[j % len(pool)]
        n = {k: seed[k] for k in ["x", "y", "d", "a"]}
        n.update(b=b, c=c, id=r["id"])
        mirrored = seed["c"] != c
        if mirrored:
            n["x"] = 3 - n["x"]
            n["d"] = {2: 3, 3: 2, 4: 5, 5: 4, 6: 7, 7: 6}.get(n["d"], n["d"])
            n["a"] = -n["a"]
        seeded.append(n)
        log.append(
            {
                "id": r["id"],
                "beat": b,
                "hand": c,
                "sourceId": seed["id"],
                "sourceBeat": t,
                "provenance": "same-time same-hand"
                if exact and j < len(same)
                else "same-time reassigned/emphasis seed"
                if exact
                else "nearest-time extrapolation",
                "mirroredOtherHandSeed": mirrored,
                "manualGeometryUsed": False,
            }
        )
    assert rhythm_signature(seeded) == rhythm_signature(rhythm)
    return seeded, log


def bomb_return_pairs(ns, bombs, clock, cue_radius=0.51):
    for hand in (0, 1):
        swings = list(groups([n for n in ns if n["c"] == hand]).values())
        for previous, current in zip(swings, swings[1:]):
            if len(previous) != 1 or len(current) != 1:
                continue
            a, b = previous[0], current[0]
            dt = clock.gap(a["b"], b["b"])
            if (
                (a["d"], a["a"]) != (b["d"], b["a"])
                or a["d"] == 8
                or not (0.18 <= dt <= 1)
                or (a["x"], a["y"]) == (b["x"], b["y"])
            ):
                continue
            cues = [
                k
                for k, bomb in enumerate(bombs)
                if a["b"] < bomb["b"] < b["b"]
                and math.hypot(bomb["x"] - a["x"], bomb["y"] - a["y"]) <= cue_radius
            ]
            if cues:
                yield a, b, cues


def project_bomb_returns(ns, features, source, provenance, vertical):
    """Keep reset accents supported by the retained same-hand rhythm."""
    chains = []
    for a, b, cues in bomb_return_pairs(source["notes"], source["bombs"], clock_for(source)):
        if chains and chains[-1]["notes"][-1]["id"] == a["id"]:
            chains[-1]["notes"].append(b)
            chains[-1]["cues"].extend(cues)
        else:
            chains.append({"notes": [a, b], "cues": cues[:]})
    source_ids = {r["id"]: r["sourceId"] for r in provenance if r["provenance"] == "same-time same-hand"}
    for chain in chains:
        anchors = {n["id"] for n in chain["notes"]}
        hand = chain["notes"][0]["c"]
        swings = list(groups([n for n in ns if n["c"] == hand]).values())
        for previous, current in zip(swings, swings[1:]):
            if len(previous) != 1 or len(current) != 1:
                continue
            a, b = previous[0], current[0]
            if any(source_ids.get(n["id"]) not in anchors for n in (a, b)):
                continue
            kind = "projected_bomb_return"
            if any(n.get("intent") and n["intent"]["kind"] != kind for n in (a, b)):
                continue
            d = chain["notes"][0]["d"]
            if vertical and d in (4, 5, 6, 7):
                d = 0 if d in (4, 5) else 1
            features.append(
                {
                    "kind": kind,
                    "hand": hand,
                    "startBeat": a["b"],
                    "endBeat": b["b"],
                    "noteIds": [a["id"], b["id"]],
                    "direction": d,
                    "sourceCueBeats": [source["bombs"][i]["b"] for i in chain["cues"]],
                }
            )
            for n in (a, b):
                x = n["x"]
                if vertical:
                    x = min(x, 1) if hand == 0 else max(x, 2)
                n.setdefault(
                    "intent",
                    {
                        "kind": kind,
                        "allowedDirections": [d],
                        "targetDirection": d,
                        "preferredCell": [x, n["y"]],
                    },
                )
            b["intent"]["resetFromBeat"] = a["b"]


def mark_crossover_support(ns, features, bpm, clock=None):
    clock = clock or BeatClock(bpm)
    timing = list(groups(ns).values())
    group_index = {n["id"]: i for i, g in enumerate(timing) for n in g}
    for f in list(features):
        if f["kind"] != "crossover_return":
            continue
        i, j = (group_index[n] for n in f["noteIds"])
        if i == 0 or j != i + 2 or any(len(g) != 1 for g in timing[i - 1 : j + 1]):
            continue
        a, cross, b, release = (g[0] for g in timing[i - 1 : j + 1])
        gaps = [y["b"] - x["b"] for x, y in zip((a, cross, b), (cross, b, release))]
        if a["c"] != b["c"] or a["c"] == cross["c"] or a.get("intent") or b.get("intent"):
            continue
        if max(gaps) - min(gaps) > 0.0001 or clock.gap(a["b"], b["b"]) > 0.45:
            continue
        turn = rotation_lower_bound(a, b)
        if turn is None or turn > 45 or a["d"] not in (1, 6, 7) or b["d"] not in (0, 4, 5):
            continue
        kind = "interleaved_crossover_support"
        features.append({"kind": kind, "startBeat": a["b"], "endBeat": b["b"], "noteIds": [a["id"], b["id"]]})
        a["intent"] = {"kind": kind, "targetDirection": a["d"], "phaseAnchor": True, "entry": True}
        b["intent"] = {
            "kind": kind,
            "targetDirection": b["d"],
            "phaseAnchor": True,
            "preferredCell": [b["x"], min(a["y"], 1)],
            "positionWeight": 2,
        }


def mark_bomb_reentries(ns, bombs, clock):
    """Recognize a rested entry into a short, alternating arc lead-in."""
    cues = []
    for hand in (0, 1):
        swings = list(groups([n for n in ns if n["c"] == hand]).values())
        for i in range(1, len(swings) - 2):
            previous, start = swings[i - 1][0], swings[i][0]
            if clock.gap(previous["b"], start["b"]) < 1.3:
                continue
            local = [b for b in bombs if previous["b"] < b["b"] <= start["b"]]
            reset_bombs = [b for b in local if b["x"] == hand + 1 and b["y"] in (0, 2)]
            if not reset_bombs or clock.gap(max(b["b"] for b in local), start["b"]) < 0.5:
                continue
            phrase = []
            for swing in swings[i : i + 5]:
                if len(swing) != 1:
                    break
                n = swing[0]
                if n["a"] or n["d"] not in (0, 1, 4, 5, 6, 7):
                    break
                if phrase and (
                    clock.gap(phrase[-1]["b"], n["b"]) > 0.4
                    or (n["d"] in (0, 4, 5)) == (phrase[-1]["d"] in (0, 4, 5))
                ):
                    break
                phrase.append(n)
                if n.get("linkedAnchor"):
                    if len(phrase) >= 3 and not any(start["b"] < b["b"] <= n["b"] for b in bombs):
                        cues.append(
                            {
                                "kind": "rested_bomb_reentry",
                                "hand": hand,
                                "startBeat": start["b"],
                                "endBeat": n["b"],
                                "noteIds": [v["id"] for v in phrase],
                                "bombBeats": [b["b"] for b in reset_bombs],
                            }
                        )
                        start["parityReentry"] = True
                    break
                if n.get("intent"):
                    break
    return cues


def detect_features(ns, bombs, bpm, clock=None):
    clock = clock or BeatClock(bpm)
    gs = list(groups(ns).values())
    features = []
    marked = copy.deepcopy(ns)
    by_id = {n["id"]: n for n in marked}
    i = 0
    # Repeated AA-BB hand roles with consistent spacing and opposite strokes.
    # Preserve hand roles and rhythm; simplify each complete pair together.
    while i < len(gs) - 3:
        pairs = []
        j = i
        delta = None
        last_color = None
        last_end = None
        while j + 1 < len(gs):
            a, b = gs[j], gs[j + 1]
            if len(a) != 1 or len(b) != 1:
                break
            a, b = a[0], b[0]
            gap = b["b"] - a["b"]
            rotation = rotation_lower_bound(a, b)
            if a.get("linkedAnchor") or b.get("linkedAnchor"):
                break
            if (
                a["c"] != b["c"]
                or a["d"] not in [0, 1, 4, 5, 6, 7]
                or b["d"] not in [0, 1, 4, 5, 6, 7]
                or rotation is None
                or rotation > 45
            ):
                break
            if not (0 < gap <= 0.75 and clock.gap(a["b"], b["b"]) <= 0.5):
                break
            if delta is None:
                delta = gap
            if (
                abs(gap - delta) > 0.0001
                or last_color == a["c"]
                or last_end is not None
                and abs(a["b"] - last_end - delta) > 0.0001
            ):
                break
            pairs.append((a, b))
            last_color = a["c"]
            last_end = b["b"]
            j += 2
        if len(pairs) < 2:
            i += 1
            continue
        feature = {
            "kind": "paired_hand_call_response",
            "startBeat": pairs[0][0]["b"],
            "endBeat": pairs[-1][1]["b"],
            "noteIds": [n["id"] for pair in pairs for n in pair],
            "evidence": "at least two complete opposite-stroke pairs, alternating hand roles, regular spacing",
            "confidence": "structural hypothesis; musical role not inferred",
        }
        fi = len(features)
        features.append(feature)
        for pi, (a, b) in enumerate(pairs):
            first = 1 if a["d"] in [1, 6, 7] else 0
            for n, d in [(a, first), (b, 1 - first)]:
                by_id[n["id"]]["intent"] = {
                    "kind": feature["kind"],
                    "featureId": fi,
                    "pairId": f"{fi}:{pi}",
                    "pairIds": [a["id"], b["id"]],
                    "preferredCell": [a["x"], min(a["y"], 1)],
                    "targetDirection": d,
                    "allowedDirections": [d],
                }
        i = j
    # A repeated cut with a displaced next target and a bomb at/near the old
    # target offers explicit return/reset choreography. Merely having any bomb
    # in the interval is insufficient. Protect the linked source gesture.
    for a, b, cues in bomb_return_pairs(ns, bombs, clock):
        if a.get("linkedAnchor") or b.get("linkedAnchor"):
            continue
        fi = len(features)
        feature = {
            "kind": "bomb_return_reset",
            "hand": a["c"],
            "startBeat": a["b"],
            "endBeat": b["b"],
            "noteIds": [a["id"], b["id"]],
            "bombIndices": cues,
            "evidence": "repeated direction, displaced target, intervening bomb near the previous target",
            "confidence": "structural reset cue; physical/musical interpretation requires review",
        }
        features.append(feature)
        for n in [a, b]:
            by_id[n["id"]]["intent"] = {
                "kind": feature["kind"],
                "featureId": fi,
                "targetDirection": n["d"],
                "allowedDirections": [n["d"]],
                "preservePosition": True,
            }
    # If the authored choreography removes a later reset cue, stop repeating
    # the forced cut: release on its opposite axis, then settle the next rapid
    # same-hand stroke vertically. This follows the detected reset context,
    # rather than a song-specific exit template.
    opposites = {0: 1, 1: 0, 2: 3, 3: 2, 4: 7, 7: 4, 5: 6, 6: 5}
    for f in list(features):
        if f["kind"] != "bomb_return_reset":
            continue
        hand = [g for g in groups([n for n in ns if n["c"] == f["hand"]]).values()]
        i = next(i for i, g in enumerate(hand) if any(n["id"] == f["noteIds"][-1] for n in g))
        if i + 1 >= len(hand) or len(hand[i + 1]) != 1:
            continue
        end = hand[i][0]
        release = hand[i + 1][0]
        if release.get("linkedAnchor") or by_id[release["id"]].get("intent") or release["d"] != end["d"]:
            continue
        if any(end["b"] < bomb["b"] < release["b"] for bomb in bombs):
            continue
        target = opposites[end["d"]]
        fi = len(features)
        ids = [release["id"]]
        entry = by_id[f["noteIds"][0]]
        dx = end["x"] - entry["x"]
        dy = end["y"] - entry["y"]
        return_cell = [
            max(0, min(3, entry["x"] - dx)),
            max(0, min(1, entry["y"] - dy if dy else 1 if target in [0, 4, 5] else 0)),
        ]
        by_id[release["id"]]["intent"] = {
            "kind": "reset_exit",
            "featureId": fi,
            "targetDirection": target,
            "allowedDirections": [target],
            "preferredCell": return_cell,
            "positionWeight": 2,
        }
        if i + 2 < len(hand) and len(hand[i + 2]) == 1:
            tail = hand[i + 2][0]
            up = lambda d: d in [0, 4, 5]
            if (
                not tail.get("linkedAnchor")
                and not by_id[tail["id"]].get("intent")
                and clock.gap(release["b"], tail["b"]) <= 0.5
                and up(tail["d"]) == up(target)
            ):
                d = 1 if up(target) else 0
                by_id[tail["id"]]["intent"] = {
                    "kind": "reset_exit",
                    "featureId": fi,
                    "targetDirection": d,
                    "allowedDirections": [d],
                }
                ids.append(tail["id"])
        features.append(
            {
                "kind": "reset_exit",
                "startBeat": release["b"],
                "endBeat": hand[min(i + 2, len(hand) - 1)][0]["b"],
                "noteIds": ids,
                "evidence": "uncued repeated cut after the last detected bomb return; opposite-axis release then rapid settling stroke",
                "confidence": "source-structure hypothesis; review exit choreography",
            }
        )
    # Keep the footprint of a low crossover return when the source supplies
    # both opposite strokes. Clearance constraints still apply.
    for hand in (0, 1):
        swings = list(groups([n for n in ns if n["c"] == hand]).values())
        for first, second in zip(swings, swings[1:]):
            if len(first) != 1 or len(second) != 1:
                continue
            a, b = first[0], second[0]
            if any(n.get("linkedAnchor") or by_id[n["id"]].get("intent") for n in (a, b)):
                continue
            gap = clock.gap(a["b"], b["b"])
            if (
                a["y"] == b["y"] == 0
                and a["d"] in (6, 7)
                and opposites[a["d"]] == b["d"]
                and crossover_depth(a) == 1
                and crossover_depth(b) == 2
                and 0.18 <= gap <= 0.5
            ):
                feature_id = len(features)
                features.append(
                    {
                        "kind": "crossover_return",
                        "startBeat": a["b"],
                        "endBeat": b["b"],
                        "noteIds": [a["id"], b["id"]],
                        "evidence": "low opposite-axis return on a source crossover",
                    }
                )
                for n in (a, b):
                    by_id[n["id"]]["intent"] = {
                        "kind": "crossover_return",
                        "featureId": feature_id,
                        "preferredCell": [n["x"], n["y"]],
                    }
    return sorted(marked, key=lambda n: (n["b"], n["c"], n["id"])), features
