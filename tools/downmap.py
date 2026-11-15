"""Map-independent pattern/flow prototype with an authoritative authored rhythm.

No song names, absolute beat ranges or song-specific templates are used.
Supplied note times/hands/counts are immutable; automatic pruning is deferred.
"""

import argparse, copy, itertools, json, math, os, re, subprocess, sys, zipfile
from pathlib import Path
from collections import Counter
from analyze_standard_spreads import load, write, groups, metrics, pair, notes as decode4, TOL
from beatmap_io import (
    read_level,
    export,
    diagnostic_package,
    sha,
    normalize,
    gameplay,
    standard_entries,
    DifficultyNotFound,
)
from mapping_intent import (
    rhythm_signature,
    seed_from_rhythm,
    detect_features,
    project_bomb_returns,
    mark_crossover_support,
    mark_bomb_reentries,
    bomb_return_pairs,
)
from geometry_policy import (
    context as geometry_context,
    exclusions,
    preference,
    crouch_recovery_cue,
    introduces_hand_inversion,
    crossover_depth,
    inline_readability_cost,
    placement_transition_cost,
    double_comfort_cost,
    double_clearance_cost,
)
from audit_placements import check_group, audit, segment, distance, VECTORS
from audit_motion_visibility import (
    run as motion_audit,
    sequence_findings,
    physical_findings,
    timed_distance,
    trajectory,
    shoulder,
    norm,
    vision_findings,
    DEFAULT,
)
from audit_swing_transitions import run as transition_audit
from beat_clock import BeatClock, clock_for
from rhythm_inputs import repair_conflicting_links, timing_link_context, retain_timing_links
from jump_settings import jump_settings, verified_jump_settings
from custom_models import model_callback, pattern_seeds
from repair_search import BEAM_WIDTH, BRANCH_WIDTH, MAX_REPAIR_WINDOWS, relaxation_costs, within_window_budget


def regular_streams(ns, profile):
    gs = list(groups(ns).values())
    found = []
    i = 0
    while i < len(gs) - 1:
        if len(gs[i]) != 1 or len(gs[i + 1]) != 1:
            i += 1
            continue
        delta = gs[i + 1][0]["b"] - gs[i][0]["b"]
        if delta <= 0 or delta > profile["regularStreamMaximumGapBeats"] + 0.0001:
            i += 1
            continue
        j = i + 1
        while (
            j < len(gs)
            and len(gs[j]) == 1
            and gs[j][0]["c"] != gs[j - 1][0]["c"]
            and abs(gs[j][0]["b"] - gs[j - 1][0]["b"] - delta) < 0.0001
        ):
            j += 1
        if j - i >= profile["regularStreamMinimumNotes"]:
            found.append(
                {
                    "startBeat": gs[i][0]["b"],
                    "endBeatExclusive": gs[j - 1][0]["b"] + delta,
                    "gapBeats": delta,
                    "notes": j - i,
                    "noteIds": [g[0]["id"] for g in gs[i:j]],
                    "sourceDownDownEntry": gs[i][0]["c"] == 1
                    and gs[i + 1][0]["c"] == 0
                    and gs[i][0]["d"] == 1
                    and gs[i + 1][0]["d"] == 1,
                }
            )
            i = j
        else:
            i += 1
    return found


def section_plan(ns, bpm, profile, clock=None):
    clock = clock or BeatClock(bpm)
    times = sorted(groups(ns))
    window = profile["densityWindowBeats"]
    end = math.ceil(times[-1] + 1)
    proposals = []
    for b in range(window, end - window):
        before = sum(b - window <= t < b for t in times) / window
        after = sum(b <= t < b + window for t in times) / window
        change = abs(after - before)
        if change >= 0.75 and change / max(before, after, 0.25) >= 0.5:
            proposals.append((change, b))
    boundaries = {0, end}
    accepted = []
    for score, b in sorted(proposals, reverse=True):
        if all(abs(b - other) >= window for other in accepted):
            accepted.append(b)
            boundaries.add(b)
    for a, b in zip(times, times[1:]):
        if b - a >= 2:
            boundaries.update([a + min(1, (b - a) / 2), b])
    streams = regular_streams(ns, profile)
    for stream in streams:
        boundaries.update([stream["startBeat"], stream["endBeatExclusive"]])
    bounds = sorted(boundaries)
    sections = []
    for a, b in zip(bounds, bounds[1:]):
        xs = [n for n in ns if a <= n["b"] < b]
        sg = groups(xs)
        mode = "preserve_rhythm_simplify_geometry" if len(sg) / (b - a) >= 1 else "preserve_sparse_motif"
        sections.append(
            {
                "startBeat": a,
                "endBeatExclusive": b,
                "notes": len(xs),
                "timingGroups": len(sg),
                "nps": len(xs) / clock.gap(a, b),
                "operation": mode,
                "boundaryStatus": "authored-rhythm density/rest and source motif proposal; not an audio-confirmed phrase",
            }
        )
    return sections, streams


def bomb_conflicts(n, bombs, clock, bpm):
    if n["d"] == 8:
        return set()
    a, b = segment(n, extent=0.75)
    return {
        i
        for i, v in enumerate(bombs)
        if abs(clock.gap(n["b"], v["b"])) <= 0.375 * 60 / bpm and distance((v["x"], v["y"]), a, b) < 0.8
    }


def bomb_clearances(n, bombs, clock, bpm):
    if n["d"] == 8:
        return {}
    a, b = segment(n, extent=0.75)
    return {
        i: distance((v["x"], v["y"]), a, b)
        for i, v in enumerate(bombs)
        if abs(clock.gap(n["b"], v["b"])) <= 0.375 * 60 / bpm
    }


def wall_conflicts(n, walls, clear_beats=0, *, clock=None, clear_seconds=0):
    return {
        i
        for i, w in enumerate(walls)
        if w["b"] <= n["b"]
        and (
            clock.gap(w["b"] + w["d"], n["b"]) < clear_seconds
            if clock
            else n["b"] < w["b"] + w["d"] + clear_beats
        )
        and w["x"] <= n["x"] < w["x"] + w["w"]
        and w["y"] <= n["y"] < w["y"] + w["h"]
    }


def strict_placement_exclusions(n, policy, level):
    if n["id"] in level.get("anchorIds", ()):
        return []
    if any(
        abs(n["b"] - b) <= TOL and (n["c"], n["x"], n["y"]) == (c, x, y)
        for b, c, x, y in level.get("linkedEndpoints", ())
    ):
        return ["new_linked_endpoint_attachment"]
    return exclusions(n, policy)


def placement_exclusions(n, policy, level):
    reasons = strict_placement_exclusions(n, policy, level)
    allowed = relaxation_costs(n, policy, reasons)
    return [reason for reason in reasons if reason not in allowed]


def placement_repair_costs(n, policy, level):
    return relaxation_costs(n, policy, strict_placement_exclusions(n, policy, level))


def weighted_repair_report(notes, policy, level):
    charges = []
    total, counts = Counter(), Counter()
    for note in notes:
        if note["id"] not in policy["repairWindows"]:
            continue
        rules = placement_repair_costs(note, policy, level)
        if rules:
            center = policy["repairWindows"][note["id"]]
            total[center] += sum(rules.values())
            counts[center] += 1
            charges.append(
                {
                    "window": center,
                    "beat": note["b"],
                    "hand": note["c"],
                    "x": note["x"],
                    "y": note["y"],
                    "direction": note["d"],
                    "rules": rules,
                }
            )
    if any(not within_window_budget(cost, counts[center]) for center, cost in total.items()):
        raise ValueError("Bounded placement repair exceeded its window budget.")
    return sorted(charges, key=lambda row: (row["beat"], row["hand"], row["x"], row["y"], row["direction"]))


def nearby_linked_swings(ns, clock):
    anchors = [dict(n, t=clock.seconds(n["b"])) for n in ns if n.get("linkedAnchor")]
    window = 2 * DEFAULT["cutHalfDurationSeconds"]
    return (
        {
            n["id"]: [
                a
                for a in anchors
                if abs(a["b"] - n["b"]) <= TOL
                or (a["c"] != n["c"] and abs(a["t"] - clock.seconds(n["b"])) <= window)
            ]
            for n in ns
            if not n.get("linkedAnchor")
        }
        if anchors
        else {}
    )


def conflicts_with_linked_swing(n, time, anchors):
    for anchor in anchors:
        if abs(anchor["b"] - n["b"]) <= TOL and any(f["rule"] != "G04" for f in check_group([n, anchor])):
            return True
        if anchor["c"] != n["c"] and controller_overlap(n, time, [anchor]):
            return True
    return False


def controller_overlap(n, time, other_swings):
    for other in other_swings:
        gap = timed_distance(dict(n, t=time), other, DEFAULT)
        if gap is not None and gap < 2 * DEFAULT["controllerRadiusMeters"]:
            return True
    return False


def compact_crossed_pairs(timing, clock):
    """Bring both hands home when two compact crossover calls still collide."""
    by_id = {n["id"]: n for _, group in timing for n in group}
    for (_, first), (_, second) in zip(timing, timing[1:]):
        if len(first) != 1 or len(second) != 1:
            continue
        a, b = first[0], second[0]
        if a["c"] == b["c"]:
            continue
        intents = [n.get("intent", {}) for n in (a, b)]
        if any(intent.get("kind") != "paired_hand_call_response" for intent in intents):
            continue
        targets = [
            dict(n, x=x, y=y, t=clock.seconds(n["b"]))
            for n, (x, y) in zip((a, b), (intent["preferredCell"] for intent in intents))
        ]
        if all(crossover_depth(n) for n in targets) and controller_overlap(
            targets[0], targets[0]["t"], targets[1:]
        ):
            for intent in intents:
                for note_id in intent["pairIds"]:
                    n = by_id[note_id]
                    n["intent"]["preferredCell"] = [n["c"] + 1, 0]


def max_reach(n):
    t = trajectory(n, DEFAULT)
    return max(norm(p, shoulder(n, DEFAULT)) for p in t) if t else 0


def simplify_high_crossed_links(level, profile):
    """Release elevated crossed anchors so their notes can use the ordinary placement search."""
    if not profile.get("simplifyHighCrossedLinks") or not level["anchorIds"]:
        return level
    _, features = detect_features(level["notes"], level["bombs"], level["bpm"], level["clock"])
    protected = {
        note_id for f in features if f["kind"] in ("bomb_return_reset", "reset_exit") for note_id in f["noteIds"]
    }
    parallel = parallel_double_directions(level["notes"])

    def comfortable(n):
        depth = crossover_depth(n)
        return not (
            (depth == 2 and n["y"] >= 1)
            or (depth and n["y"] == 2 and (n["d"] in (1, 6, 7) or n["id"] in parallel))
        )

    prepared = timing_link_context(level, protected, anchor_allowed=comfortable)
    return retain_timing_links(prepared, level["notes"])


def parallel_double_directions(notes):
    directions = {}
    for double in groups(notes).values():
        if len(double) != 2 or double[0]["c"] == double[1]["c"]:
            continue
        if any(n.get("intent") or n.get("linkedAnchor") or n.get("a", 0) or n["d"] == 8 for n in double):
            continue
        horizontal = next((n["d"] for n in double if n["d"] in (2, 3)), None)
        if horizontal is not None and all(VECTORS[n["d"]][0] == VECTORS[horizontal][0] for n in double):
            for n in double:
                directions[n["id"]] = horizontal
    return directions


def exceeds_reach(n, source_reach, controls):
    reach = max_reach(n)
    return reach > source_reach + controls["reachSlackMeters"] or (
        controls.get("centeredReachOnly", False) and reach > DEFAULT["armReachMeters"]
    )


def stack_placements(options):
    """Build same-hand layouts incrementally before combining both hands."""
    layouts = [()]
    for choices in options:
        layouts = [
            (*layout, choice)
            for layout in layouts
            for choice in choices
            if all(
                not any(finding["rule"] != "G04" for finding in check_group([other[1], choice[1]]))
                for other in layout
            )
        ]
    return layouts


def stack_direction_constraints(group, source, exclusions):
    """Try the authored stack axes as a group, including reversed strokes."""
    hands = [[n for n in group if n["c"] == hand] for hand in (0, 1)]
    if all(len(notes) < 2 for notes in hands):
        return []
    opposite = {0: 1, 1: 0, 2: 3, 3: 2, 4: 7, 5: 6, 6: 5, 7: 4}
    choices = []
    for notes in hands:
        if len(notes) < 2:
            choices.append([frozenset()])
            continue
        authored = {source[n["id"]]["d"] for n in notes}
        if len(authored) != 1 or (arrow := next(iter(authored))) not in opposite:
            return []
        choices.append(
            [
                frozenset(
                    (n["id"], d)
                    for n in notes
                    for d in source[n["id"]]["geometryDirections"]
                    if d != direction
                )
                for direction in (arrow, opposite[arrow])
                if all(
                    direction in source[n["id"]]["geometryDirections"]
                    and (n["id"], direction) not in exclusions
                    for n in notes
                )
            ]
        )
    return [exclusions | left | right for left, right in itertools.product(*choices)]


def parallel_double_constraints(group, source, exclusions, directions=(2, 3)):
    """Retry a split authored double with its parallel cuts intact."""
    if len(group) != 2 or group[0]["c"] == group[1]["c"]:
        return None
    if any(n.get("linkedAnchor") or n.get("streamEntry") for n in group):
        return None
    authored = [source[n["id"]] for n in group]
    direction = authored[0]["d"]
    if (
        direction not in directions
        or authored[1]["d"] != direction
        or all(n["d"] == direction for n in group)
    ):
        return None
    if any(direction not in n["geometryDirections"] or (n["id"], direction) in exclusions for n in authored):
        return None
    return exclusions | {(n["id"], d) for n in authored for d in n["geometryDirections"] if d != direction}


def open_crossed_reset_cells(notes, features, bombs, clock):
    """Allow a hand to return home when the same authored bomb row supports it."""
    outgoing = {}
    reset_notes = set()
    for feature in features:
        if feature["kind"] == "bomb_return_reset":
            outgoing[feature["noteIds"][0]] = feature
            reset_notes.update(feature["noteIds"])
    moved = set()
    for beat, group in groups(notes).items():
        time = clock.seconds(beat)
        fixed = [n for n in group if n["id"] in reset_notes and n.get("intent", {}).get("preservePosition")]
        for left, right in itertools.combinations(fixed, 2):
            if left["c"] == right["c"]:
                continue
            red, blue = (left, right) if left["c"] == 0 else (right, left)
            if red["x"] <= blue["x"] or not controller_overlap(red, time, [dict(blue, t=time)]):
                continue
            for n in (red, blue):
                feature = outgoing.get(n["id"])
                cue_beats = {bombs[i]["b"] for i in feature["bombIndices"]} if feature else set()
                home = (n["c"] + 1, n["y"])
                cells = (
                    {home}
                    if not feature or any(b["b"] in cue_beats and (b["x"], b["y"]) == home for b in bombs)
                    else set()
                )
                if cells and home != (n["x"], n["y"]):
                    n["intent"]["allowedCells"] = [list(cell) for cell in sorted(cells | {(n["x"], n["y"])})]
                    n["intent"]["preservePosition"] = False
                    moved.add(n["id"])
    return [f for f in features if f["kind"] == "bomb_return_reset" and moved.intersection(f["noteIds"])]


RELOCATED_BOMB_CUE_RADIUS = 1.00001


def open_reset_repair_cells(notes, resets, bombs, relocated):
    """Open cells for blocked reset notes without moving or substituting their cues."""
    by_id = {n["id"]: n for n in notes}
    outgoing = {}
    fixed_neighbors = {}
    adjusted = []
    for feature in resets:
        start, end = feature["noteIds"]
        for note_id in (start, end):
            intent = by_id[note_id].get("intent", {})
            if intent.get("kind") == "bomb_return_reset":
                intent["relocatableBombReset"] = True
                intent.setdefault("resetNeighbors", []).append(end if note_id == start else start)
        if not relocated.intersection((start, end)):
            continue
        adjusted.append(feature)
        by_id[end]["intent"]["previousResetNoteId"] = start
        outgoing.setdefault(start, set()).update(feature["bombIndices"])
        for current, other in ((start, end), (end, start)):
            neighbor = by_id[other]
            if other not in relocated and neighbor["intent"].get("preservePosition"):
                fixed_neighbors.setdefault(current, set()).add((neighbor["x"], neighbor["y"]))

    for note_id in sorted(relocated):
        intent = by_id[note_id]["intent"]
        intent["preservePosition"] = False
        intent["allowedCells"] = [
            [x, y]
            for x, y in itertools.product(range(4), range(3))
            if (x, y) not in fixed_neighbors.get(note_id, ())
            and all(
                math.hypot(bombs[i]["x"] - x, bombs[i]["y"] - y) <= RELOCATED_BOMB_CUE_RADIUS
                for i in outgoing.get(note_id, ())
            )
        ]
    return adjusted


def preserves_reset_cues(notes, bombs, clock, resets):
    """Verify both displaced same-hand strokes and the original intervening bombs."""
    actual = {
        (a["c"], a["b"], b["b"]): set(cues)
        for a, b, cues in bomb_return_pairs(notes, bombs, clock, cue_radius=RELOCATED_BOMB_CUE_RADIUS)
    }
    return all(
        set(reset["bombIndices"]) <= actual.get((reset["hand"], reset["startBeat"], reset["endBeat"]), set())
        for reset in resets
    )


class PlacementDeadEnd(ValueError):
    def __init__(self, beat, notes, message, visibility=None):
        super().__init__(f"{message} at {beat}; manual source annotation needed")
        self.beat = beat
        self.reason = message
        self.notes = notes
        self.visibility = visibility


class DirectionDeadEnd(ValueError):
    def __init__(self, message, beat, swings):
        super().__init__(message)
        self.beat = beat
        self.swings = swings


def geometry_direction_domains(ns, level, profile, joint_clearance=()):
    """Reject direction choices with no feasible source-relative placement.

    This is a forward constraint, not a complete joint direction/body solver.
    Recent-note occlusion is still checked during sequential placement.
    """
    originals = {n["id"]: n for n in level["notes"]}
    result = copy.deepcopy(ns)
    policy = geometry_context(ns, level, profile)
    njs = policy["njs"]
    rt = policy["approachSeconds"]
    clock = policy["clock"]
    clear = dict(clock=clock, clear_seconds=DEFAULT["requiredClearLeadSeconds"])
    anchor_neighbors = nearby_linked_swings(ns, clock)
    walls = [
        {**w, "start": clock.seconds(w["b"]), "end": clock.seconds(w["b"] + w["d"])} for w in level["walls"]
    ]
    own = lambda n: n["x"] < 2 if n["c"] == 0 else n["x"] >= 2
    stacks = {
        n["id"]
        for hand in (0, 1)
        for group in groups([n for n in ns if n["c"] == hand]).values()
        if len(group) > 1
        for n in group
    }
    opening_dots = set()
    blocked_resets = []
    for hand in (0, 1):
        first = next(iter(groups([n for n in ns if n["c"] == hand]).values()), [])
        if all(n["d"] == 8 for n in first):
            opening_dots.update(n["id"] for n in first)
    for n in result:
        original = originals[n["id"]]
        intent = n.get("intent", {})
        t = clock.seconds(n["b"])
        allowed = []
        if n.get("linkedAnchor"):
            n["geometryDirections"] = [n["d"]]
            continue
        bomb_context = (policy["nearbyBombs"][n["id"]], clock, level["bpm"])
        nearby = [w for w in walls if w["start"] <= t + rt and w["end"] >= t - rt]
        directions = intent.get("allowedDirections", [8] if n["d"] == 8 else range(8))
        limit = (
            max(max_reach(originals[i]) for i in intent["pairIds"])
            if intent.get("pairIds")
            else max_reach(original)
        )
        baseline = bomb_clearances(original, *bomb_context)
        baseline_bombs = bomb_conflicts(original, *bomb_context)
        baseline_walls = wall_conflicts(original, level["walls"], **clear)
        repairs = [None]
        if profile.get("repairEmptyGeometryDomains"):
            repairs.append(
                "reset_cell"
                if intent.get("kind") == "bomb_return_reset" and intent.get("preservePosition")
                else "clearance"
            )
            if n["id"] in joint_clearance and not intent.get("preservePosition"):
                repairs = ["stack_clearance" if n["id"] in stacks else "clearance"]
                if n["id"] in opening_dots:
                    n["retainOpeningDot"] = True
        for repair in repairs:
            for d in directions:
                for x, y in itertools.product(range(4), range(3)):
                    v = {
                        **n,
                        "x": x,
                        "y": y,
                        "d": d,
                        "a": n["a"] if d == n["d"] else 0,
                        "domainRepair": repair,
                    }
                    if placement_exclusions(v, policy, level):
                        continue
                    if conflicts_with_linked_swing(v, t, anchor_neighbors.get(n["id"], ())):
                        continue
                    if intent.get("preservePosition") and (x, y) != (original["x"], original["y"]):
                        continue
                    if "allowedCells" in intent and [x, y] not in intent["allowedCells"]:
                        continue
                    if (
                        not own(original)
                        and original["d"] in [2, 3]
                        and d in [2, 3]
                        and d != original["d"]
                        and not own(v)
                    ):
                        continue
                    if exceeds_reach(v, limit, policy["controls"]):
                        continue
                    if bomb_conflicts(v, *bomb_context) - baseline_bombs:
                        continue
                    if any(
                        value < min(0.8, baseline[k]) - 0.05
                        for k, value in bomb_clearances(v, *bomb_context).items()
                    ):
                        continue
                    if wall_conflicts(v, level["walls"], **clear) - baseline_walls:
                        continue
                    if any(
                        f["severity"] == "late_visibility_review"
                        for f in vision_findings([dict(v, t=t)], nearby, njs, rt, DEFAULT)
                    ):
                        continue
                    allowed.append(d)
                    break
            if allowed:
                if repair:
                    n["domainRepair"] = repair
                break
        if not allowed:
            if intent.get("relocatableBombReset"):
                blocked_resets.append(n)
                continue
            if blocked_resets:
                break
            raise PlacementDeadEnd(n["b"], [n], "No geometric direction domain")
        n["geometryDirections"] = allowed
        if n["d"] not in allowed and any(
            abs(a["b"] - n["b"]) <= TOL for a in anchor_neighbors.get(n["id"], ())
        ):
            n["linkedDirectionRepair"] = True
    if blocked_resets:
        raise PlacementDeadEnd(blocked_resets[0]["b"], blocked_resets, "No geometric direction domain")
    return result


def geometry(ns, level, sections, profile):
    if profile.get("geometry", {}).get("compactPairedCells"):
        ns = copy.deepcopy(ns)
    source = {n["id"]: n for n in level["notes"]}
    out = []
    last = {}
    edits = []
    queue = []
    pair_cells = {}
    policy = geometry_context(ns, level, profile, sections)
    own = lambda n: n["x"] < 2 if n["c"] == 0 else n["x"] >= 2
    njs = policy["njs"]
    rt = policy["approachSeconds"]
    clock = policy["clock"]
    clear = dict(clock=clock, clear_seconds=DEFAULT["requiredClearLeadSeconds"])
    anchor_neighbors = nearby_linked_swings(ns, clock)
    walls = [
        {**w, "start": clock.seconds(w["b"]), "end": clock.seconds(w["b"] + w["d"])} for w in level["walls"]
    ]
    timing = list(groups(ns).items())
    comfortable_doubles = profile.get("comfortableDoubles")
    windup_weight = profile.get("doubleWindupClearanceWeight", 0)
    if policy["controls"].get("compactPairedCells"):
        compact_crossed_pairs(timing, clock)
    import native_engine
    if native_engine.enabled() and os.environ.get("BS_DOWNMAP_NATIVE_PLACEMENT") == "1":
        rows, failure = native_engine.place(ns, level, sections, profile, policy)
        if failure is not None:
            raise PlacementDeadEnd(failure["beat"], failure["notes"], failure["failure"], failure["visibility"])
        edits = [
            {"id": n["id"], "beat": n["b"], "before": source[n["id"]], "after": n,
             "reason": "source-relative reach/flow simplification; bomb and wall context retained"}
            for n in rows
            if (n["x"], n["y"]) != (source[n["id"]]["x"], source[n["id"]]["y"])
        ]
        return rows, edits, []
    base_options = {}
    cell = lambda n: (n["x"], n["y"])
    inline_window = max(
        policy["controls"]["sameHandInlineSeconds"], policy["controls"]["oppositeInlineSeconds"]
    )
    for beat, g in timing:
        t = clock.seconds(beat)
        nearby_walls = [w for w in walls if w["start"] <= t + rt and w["end"] >= t - rt]
        for n in g:
            original = source[n["id"]]
            if n.get("linkedAnchor"):
                base_options[n["id"]] = [(0, n)]
                continue
            bomb_context = (policy["nearbyBombs"][n["id"]], clock, level["bpm"])
            intent = n.get("intent", {})
            paired = intent.get("kind") == "paired_hand_call_response"
            limit = max(max_reach(source[i]) for i in intent["pairIds"]) if paired else max_reach(original)
            baseline_clearance = bomb_clearances(original, *bomb_context)
            baseline_bombs = bomb_conflicts(original, *bomb_context)
            baseline_walls = wall_conflicts(original, level["walls"], **clear)
            choices = []
            for x, y in itertools.product(range(4), range(3)):
                v = {**n, "x": x, "y": y}
                if placement_exclusions(v, policy, level):
                    continue
                if conflicts_with_linked_swing(v, t, anchor_neighbors.get(n["id"], ())):
                    continue
                if intent.get("preservePosition") and (x, y) != (original["x"], original["y"]):
                    continue
                if "allowedCells" in intent and [x, y] not in intent["allowedCells"]:
                    continue
                # The user's two crossover failures: reversing a horizontal
                # cut while keeping the old crossover is not a simplification.
                reversed_cross = (
                    not own(original)
                    and original["d"] in [2, 3]
                    and n["d"] in [2, 3]
                    and original["d"] != n["d"]
                )
                if reversed_cross and not own(v):
                    continue
                if exceeds_reach(v, limit, policy["controls"]):
                    continue
                if bomb_conflicts(v, *bomb_context) - baseline_bombs:
                    continue
                if any(
                    value < min(0.8, baseline_clearance[k]) - 0.05
                    for k, value in bomb_clearances(v, *bomb_context).items()
                ):
                    continue
                if wall_conflicts(v, level["walls"], **clear) - baseline_walls:
                    continue
                if any(
                    f["severity"] == "late_visibility_review"
                    for f in vision_findings([dict(v, t=t)], nearby_walls, njs, rt, DEFAULT)
                ):
                    continue
                costs = preference(v, original, None, policy)
                if v["id"] in policy["repairWindows"]:
                    v["weightedRepairCost"] = sum(placement_repair_costs(v, policy, level).values())
                costs["sourceReachIncrease"] = max(0, max_reach(v) - limit) * 5
                if v.get("retainOpeningDot") and (x, y) != (original["x"], original["y"]):
                    costs["openingDotCell"] = 25
                if v.get("domainRepair") in ("clearance", "stack_clearance"):
                    costs["clearanceRepair"] = 2 * len(
                        placement_exclusions({**v, "domainRepair": None}, policy, level)
                    )
                choices.append((sum(costs.values()), v))
            if not choices:
                raise PlacementDeadEnd(beat, g, "No source-relative geometry")
            base_options[n["id"]] = choices

    pair_domains = {}
    for n in ns:
        intent = n.get("intent", {})
        if intent.get("kind") != "paired_hand_call_response" or intent["pairId"] in pair_domains:
            continue
        pair_domains[intent["pairId"]] = set.intersection(
            *({cell(v) for _, v in base_options[i]} for i in intent["pairIds"])
        )
    for n in ns:
        domain = pair_domains.get(n.get("intent", {}).get("pairId"))
        if domain:
            base_options[n["id"]] = [(cost, v) for cost, v in base_options[n["id"]] if cell(v) in domain]

    def placement_choices(index):
        beat, g = timing[index]
        t = clock.seconds(beat)
        prior = []
        for n in reversed(out):
            if clock.gap(n["b"], beat) > rt:
                break
            prior.append(dict(n, t=clock.seconds(n["b"])))
        prior.reverse()
        recent = [n for n in prior if clock.gap(n["b"], beat) <= inline_window]
        following = (
            timing[index + 1][1]
            if index + 1 < len(timing) and clock.gap(beat, timing[index + 1][0]) <= inline_window
            else []
        )
        compact = policy["controls"].get("compactPairedCells")
        paired_release = (
            compact
            and len(g) == 2
            and g[0]["c"] != g[1]["c"]
            and g[0]["d"] == g[1]["d"]
            and all(source[n["id"]]["x"] in (0, 3) and source[n["id"]]["y"] > 0 for n in g)
            and any(
                n.get("intent", {}).get("kind") == "paired_hand_call_response"
                and clock.gap(n["b"], beat) <= 0.65
                for n in last.values()
            )
        )
        opts = []
        for n in g:
            prev = last.get(n["c"])
            intent = n.get("intent", {})
            paired = intent.get("kind") == "paired_hand_call_response"
            choices = []
            occluders = set()
            for base, v in base_options[n["id"]]:
                if (
                    prev
                    and prev["id"] == intent.get("previousResetNoteId")
                    and (prev["x"], prev["y"]) == (v["x"], v["y"])
                ):
                    continue
                vis = vision_findings(prior + [dict(v, t=t)], [], njs, rt, DEFAULT, target_index=len(prior))
                late = [f for f in vis if f["severity"] == "late_visibility_review"]
                if late:
                    occluders.update(f["occluderNoteId"] for f in late if "occluderNoteId" in f)
                    continue
                travel = 0.25 * math.hypot(v["x"] - prev["x"], v["y"] - prev["y"]) if prev else 0
                travel += placement_transition_cost(prev, v, policy)
                if paired and intent["pairId"] in pair_cells and cell(v) != pair_cells[intent["pairId"]]:
                    travel += policy["controls"].get("pairSpacingCost", 3)
                if (
                    compact
                    and not paired
                    and prev
                    and prev.get("intent", {}).get("kind") == "paired_hand_call_response"
                    and clock.gap(prev["b"], v["b"]) <= 0.65
                ):
                    travel += 2.1 * abs(v["x"] - prev["x"])
                if paired_release and not v.get("linkedAnchor"):
                    travel += 2.5 * (abs(v["x"] - (v["c"] + 1)) + v["y"])
                readability = sum(inline_readability_cost(v, other, policy) for other in recent)
                # Reserve the next note's preferred lane when shifting it
                # would be harder than moving the current note out of its way.
                for other in following:
                    future = base_options[other["id"]]
                    adjustments = []
                    for cost, option in future:
                        overlap = (
                            compact
                            and v["c"] != option["c"]
                            and controller_overlap(v, t, [dict(option, t=clock.seconds(option["b"]))])
                        )
                        adjustments.append(
                            cost + inline_readability_cost(v, option, policy) + 9 * bool(overlap)
                        )
                    readability += min(adjustments) - min(cost for cost, _ in future)
                choices.append((base + travel + readability, v))
            if not choices:
                fixed = sorted(p["id"] for p in prior if p.get("linkedAnchor") and p["id"] in occluders)
                visibility = (
                    {"target": n["id"], "blockers": fixed} if n.get("linkedAnchor") and fixed else None
                )
                raise PlacementDeadEnd(beat, g, "No source-relative geometry", visibility)
            limit = (
                12
                if n["id"] in policy["repairWindows"]
                or (len(g) > 2 and profile.get("repairEmptyGeometryDomains"))
                else 8
            )
            opts.append(sorted(choices, key=lambda choice: choice[0])[:limit])
        viable = []
        stacks = [
            stack_placements([options for n, options in zip(g, opts) if n["c"] == hand]) for hand in (0, 1)
        ]
        for left, right in itertools.product(*stacks):
            combo = (*left, *right)
            trial = [n for _, n in combo]
            if introduces_hand_inversion(trial, source):
                continue
            static = check_group(trial)
            if any(f["rule"] != "G04" for f in static):
                continue
            context = [
                n
                for n in prior
                if t - n["t"]
                <= max(
                    DEFAULT["armStateSeconds"] + DEFAULT["armRecoverySeconds"],
                    2 * DEFAULT["cutHalfDurationSeconds"],
                )
            ] + [dict(n, t=t) for n in trial]
            sq = sequence_findings(context, DEFAULT)
            if any(
                f["rule"] == "S01"
                and f["beat"] >= beat - 0.0001
                and not all(n.get("linkedAnchor") for n in f["notes"])
                for f in sq
            ):
                continue
            if any(
                f["rule"] == "C01"
                and max(f["beat"], f.get("otherBeat", -1)) >= beat - 0.0001
                and not all(n.get("linkedAnchor") for n in f["notes"])
                for f in physical_findings(context, DEFAULT)
            ):
                continue
            comfort = double_comfort_cost(trial, windup_weight=windup_weight) if comfortable_doubles else 0
            viable.append((sum(cost for cost, _ in combo) + comfort, trial))
        if not viable:
            raise PlacementDeadEnd(beat, g, "No joint geometry")
        return sorted(viable, key=lambda item: item[0])

    def advance_state(chosen, hand_state, pair_state):
        for note in chosen:
            hand_state[note["c"]] = note
            intent = note.get("intent", {})
            if intent.get("kind") == "paired_hand_call_response":
                pair_state[intent["pairId"]] = cell(note)

    def place(chosen, beat):
        for note in chosen:
            original = source[note["id"]]
            if cell(note) != cell(original):
                edits.append(
                    {
                        "id": note["id"],
                        "beat": beat,
                        "before": original,
                        "after": note,
                        "reason": "source-relative reach/flow simplification; bomb and wall context retained",
                    }
                )
        advance_state(chosen, last, pair_cells)
        out.extend(chosen)

    def search_window(start, stop):
        """Beam-search competing layouts through the lead-in, hit, and exit."""
        nonlocal last, pair_cells
        prefix = out[:]
        saved_last, saved_pairs = last, pair_cells
        # Keep quality and rule cost separate so a cheaper compromise ranks first.
        beam = [(0.0, 0, 0.0, (), last, pair_cells)]
        try:
            for index in range(start, stop):
                candidates = []
                failure = None
                for penalty, count, cost, path, hand_state, pair_state in beam:
                    out[:] = prefix + [n for attack in path for n in attack]
                    last, pair_cells = hand_state, pair_state
                    try:
                        layouts = placement_choices(index)
                    except PlacementDeadEnd as error:
                        failure = error
                        continue
                    layouts.sort(
                        key=lambda item: (sum(n.get("weightedRepairCost", 0) for n in item[1]), item[0])
                    )
                    for local_cost, chosen in layouts[:BRANCH_WIDTH]:
                        charges = [n.get("weightedRepairCost", 0) for n in chosen]
                        next_penalty, next_count = penalty + sum(charges), count + sum(c > 0 for c in charges)
                        if not within_window_budget(next_penalty, next_count):
                            continue
                        next_last, next_pairs = hand_state.copy(), pair_state.copy()
                        advance_state(chosen, next_last, next_pairs)
                        candidates.append(
                            (
                                next_penalty,
                                next_count,
                                cost + local_cost,
                                (*path, chosen),
                                next_last,
                                next_pairs,
                            )
                        )
                if not candidates:
                    beat, group = timing[index]
                    print(
                        f"Bounded cell search exhausted at beat {beat} ({BEAM_WIDTH} paths per attack).",
                        flush=True,
                    )
                    raise failure or PlacementDeadEnd(beat, group, "Bounded placement budget exhausted")
                beam = sorted(candidates, key=lambda entry: entry[:3])[:BEAM_WIDTH]
            return beam[0][3]
        finally:
            out[:] = prefix
            last, pair_cells = saved_last, saved_pairs

    windows = {}
    window_ids = [policy["repairWindows"].get(group[0]["id"]) for _, group in timing]
    for i, center in enumerate(window_ids):
        if center is not None and (i == 0 or center != window_ids[i - 1]):
            stop = i + 1
            while stop < len(timing) and window_ids[stop] == center:
                stop += 1
            # Screen the first unrelaxed exit as part of the candidate path.
            if stop < len(timing) and window_ids[stop] is None:
                stop += 1
            windows[i] = stop

    index = 0
    retries = 0
    checkpoints = []
    while index < len(timing):
        if index in windows:
            stop = windows[index]
            for offset, chosen in enumerate(search_window(index, stop)):
                place(chosen, timing[index + offset][0])
            checkpoints.clear()
            index = stop
            continue
        try:
            choices = iter(placement_choices(index))
            _, chosen = next(choices)
        except PlacementDeadEnd:
            # Reconsider nearby cells before changing directions or reporting a dead end.
            for slot in reversed(range(len(checkpoints))):
                previous, alternatives, out_size, edit_size, old_last, old_pairs = checkpoints[slot]
                alternative = next(alternatives, None)
                if alternative is not None and retries < 24:
                    del out[out_size:]
                    del edits[edit_size:]
                    last, pair_cells = old_last.copy(), old_pairs.copy()
                    index, choices, chosen = previous, alternatives, alternative[1]
                    del checkpoints[slot:]
                    retries += 1
                    break
            else:
                raise
        checkpoints.append((index, choices, len(out), len(edits), last.copy(), pair_cells.copy()))
        if len(checkpoints) > 4:
            checkpoints.pop(0)
        place(chosen, timing[index][0])
        index += 1
    return out, edits, queue


TOOLS = Path(__file__).resolve().parent
QUICKJS = TOOLS.parent / ".tools" / "quickjs-runtime"
QUICKJS_EXE = QUICKJS / ("qjs.exe" if sys.platform == "win32" else "qjs")


def run_quickjs(task, data, output):
    # Keep Windows paths in Python. The QuickJS CLI uses the system code page for argv.
    process = subprocess.run(
        [str(QUICKJS_EXE), "dispatch.mjs", task],
        cwd=QUICKJS, input=data, capture_output=True, encoding="utf-8", errors="replace",
    )
    if process.returncode == 0:
        output.write_text(process.stdout, encoding="utf-8")
    return process


def run_direction_search(run):
    source, output = run / "direction-input.json", run / "direction-search.json"
    if QUICKJS_EXE.is_file():
        return run_quickjs("plan_directions", source.read_text(encoding="utf-8"), output)
    return subprocess.run(
        ["node", str(TOOLS / "plan_directions.cjs"), str(source), str(output)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def prefer_roomier_doubles(run, placed, level, sections, profile, validate):
    """Give close doubles one soft direction retry and retain the best verified chart."""
    weight = profile.get("doubleWindupClearanceWeight", 0)
    if not weight or profile.get("boundedRepairBeats"):
        return placed
    baseline = placed[0]
    scores = {}
    for pair in groups(baseline).values():
        if len(pair) != 2 or pair[0]["c"] == pair[1]["c"] or any(n["d"] == 8 for n in pair):
            continue
        cost = double_clearance_cost(*pair, weight)
        if cost >= 6:
            for n in pair:
                if not (n.get("linkedAnchor") or n.get("intent") or n.get("streamEntry")):
                    scores[n["id"]] = (n["d"], min(cost, 9))
    if not scores:
        return placed
    input_path = run / "direction-input.json"
    output_path = run / "direction-search.json"
    parity_path = run / "candidate-parity.json"
    original_input, original_search = load(input_path), load(output_path)
    original_parity = load(parity_path)
    probe = [
        {**n, "doubleClearanceDirection": scores[n["id"]][0], "doubleClearanceCost": scores[n["id"]][1]}
        if n["id"] in scores else n for n in original_input["notes"]
    ]
    write(input_path, {**original_input, "notes": probe})
    accepted = False
    screened_alternative = False
    try:
        if run_direction_search(run).returncode:
            return placed
        search = load(output_path)
        candidate = geometry(search["notes"], level, sections, profile)
        before = sum(double_comfort_cost(g, windup_weight=weight) for g in groups(baseline).values())
        after = sum(double_comfort_cost(g, windup_weight=weight) for g in groups(candidate[0]).values())
        previous = {n["id"]: n for n in baseline}
        changed = sum(
            0.75 * (n["d"] != previous[n["id"]]["d"])
            + 0.25 * (abs(n["x"] - previous[n["id"]]["x"]) + abs(n["y"] - previous[n["id"]]["y"]))
            for n in candidate[0]
        )
        if after + changed >= before:
            return placed
        screened_alternative = True
        validate(candidate[0], search)
        parity = load(parity_path)
        if any(parity[key] > original_parity[key] for key in ("errors", "warnings")):
            return placed
        accepted = True
        return candidate
    except PlacementDeadEnd:
        return placed
    finally:
        if not accepted:
            write(input_path, original_input)
            write(output_path, original_search)
            if screened_alternative:
                validate(baseline, original_search)


def plan_geometry(run, direction_input, level, sections, profile, validate=None):
    """Retry a blocked placement or parity check without changing the rhythm."""
    pending = [(mode, frozenset()) for mode in ("source", "warnings", "horizontal", "standard")]
    seen = set()
    trace = []
    last_error = None
    joint_frontier = None
    direction_failure = None
    audit_failure = None
    attempts = Counter()
    budgets = {"standard": 16, "horizontal": 8, "warnings": 6, "source": 10}
    ns = {n["id"]: n for n in direction_input["notes"]}
    repair_policy = (
        geometry_context(list(ns.values()), level, profile, sections)
        if profile.get("boundedRepairBeats")
        else None
    )
    repair_ids = repair_policy["repairWindows"] if repair_policy else {}
    joint_retries = {}
    best_repair = None
    extra_attempts = 0
    # Directions are shared by a same-hand swing. Canonical exclusions avoid
    # spending the retry budget on one equivalent branch per cube in a stack.
    representative = {}
    first_at_attack = {}
    for n in ns.values():
        if not n.get("linkedAnchor"):
            representative[n["id"]] = first_at_attack.setdefault((n["b"], n["c"]), n["id"])
    while pending:
        if repair_policy:
            mode_order = {"standard": 0, "horizontal": 1, "warnings": 2, "source": 3}
            first = min(
                range(len(pending)),
                key=lambda i: (
                    mode_order[pending[i][0]],
                    pending[i] not in joint_retries,
                    -joint_retries.get(pending[i], 0),
                    len(pending[i][1]),
                ),
            )
            mode, exclusions = pending.pop(first)
        else:
            mode, exclusions = pending.pop()
        exclusions = frozenset((representative.get(note_id, note_id), d) for note_id, d in exclusions)
        key = mode, exclusions
        if mode == "warnings" and (
            direction_failure is None or not profile.get("repairEmptyGeometryDomains")
        ):
            continue
        if mode == "source" and (audit_failure is None or not profile.get("repairEmptyGeometryDomains")):
            continue
        if key in seen or attempts[mode] >= budgets[mode]:
            continue
        seen.add(key)
        attempts[mode] += 1
        if best_repair is not None:
            if extra_attempts >= 3:
                break
            extra_attempts += 1
        by_note = {}
        for note_id, direction in sorted(exclusions):
            by_note.setdefault(note_id, []).append(direction)
        entry = {
            "excludedDirections": by_note,
            "allowHorizontalRepair": mode != "standard",
            "allowProvisionalParityWarning": mode in ("warnings", "source"),
            "preferSourceArrows": mode == "source",
        }
        trace.append(entry)
        write(run / "direction-input.json", {**direction_input, **entry})
        result = run_direction_search(run)
        if result.returncode:
            reason = next(
                (
                    line.removeprefix("Error: ")
                    for line in result.stderr.splitlines()
                    if line.startswith("Error: ")
                ),
                "Direction search failed. See the run log.",
            )
            entry.update(status="direction search exhausted", reason=reason)
            if not reason.startswith("No supported direction path"):
                print(result.stderr, end="", file=sys.stderr)
                raise ValueError(reason)
            if not exclusions:
                match = re.match(r"No supported direction path at beat (\S+) \((left|right) hand\)", reason)
                if match:
                    beat = float(match[1])
                    hand = int(match[2] == "right")
                    swings = list(
                        groups(
                            [
                                n
                                for n in ns.values()
                                if n["c"] == hand
                                and n["b"] <= beat + TOL
                                and level["clock"].gap(n["b"], beat) <= 1
                            ]
                        ).values()
                    )
                    direction_failure = DirectionDeadEnd(reason, beat, swings[-2:])
                    if last_error is None:
                        last_error = direction_failure
            continue
        try:
            direction_search = load(run / "direction-search.json")
            placed = geometry(direction_search["notes"], level, sections, profile)
            if validate:
                validate(placed[0], direction_search)
        except PlacementDeadEnd as error:
            last_error = error
            if (
                key in joint_retries
                and error.reason == "No joint geometry"
                and error.beat > joint_retries[key] + TOL
                and (joint_frontier is None or error.beat > joint_frontier.beat)
            ):
                joint_frontier = error
            if error.reason.startswith("Unexpected parity") and (
                audit_failure is None or error.beat > audit_failure.beat
            ):
                audit_failure = error
            entry.update(status="placement blocked", reason=str(error))
            if error.visibility:
                entry["fixedOcclusion"] = error.visibility
            alternatives = list(error.notes)
            if repair_policy:
                clock = repair_policy["clock"]
                failure_beat = error.beat
                alternatives.extend(
                    sorted(
                        (
                            n
                            for n in direction_search["notes"]
                            if n["id"] in repair_ids and abs(clock.gap(failure_beat, n["b"])) <= 0.75
                        ),
                        key=lambda n: abs(clock.gap(failure_beat, n["b"])),
                    )[:6]
                )
            for n in reversed(alternatives):
                if n.get("linkedAnchor") or n.get("streamEntry"):
                    continue
                representative_id = representative[n["id"]]
                if any(
                    d != n["d"] and (representative_id, d) not in exclusions
                    for d in ns[n["id"]]["geometryDirections"]
                ):
                    pending.append((mode, exclusions | {(representative_id, n["d"])}))
            if profile.get("repairEmptyGeometryDomains") and error.reason == "No joint geometry":
                for constraints in reversed(stack_direction_constraints(error.notes, ns, exclusions)):
                    pending.append((mode, constraints))
                if repair_policy:
                    # Keep earlier joint choices while repairing the next double.
                    # Trying both authored cuts does not relax any placement rule.
                    directions = (2, 3, 4, 5, 6, 7) if profile.get("repairParallelDiagonals") else (2, 3)
                    constraints = parallel_double_constraints(error.notes, ns, exclusions, directions)
                    if constraints is not None and (mode, constraints) not in joint_retries:
                        joint_retries[mode, constraints] = error.beat
                        pending.append((mode, constraints))
        else:
            entry["status"] = "placed"
            if repair_policy:
                affected = [n for n in placed[0] if n["id"] in repair_ids]
                penalty = sum(sum(placement_repair_costs(n, repair_policy, level).values()) for n in affected)
                motion = sum(
                    (n["d"] != ns[n["id"]]["d"])
                    + 0.25 * math.hypot(n["x"] - ns[n["id"]]["x"], n["y"] - ns[n["id"]]["y"])
                    for n in affected
                )
                rank = (penalty, motion)
                entry["weightedRepairCost"], entry["localShapeChange"] = rank
                if best_repair is None or rank < best_repair[0]:
                    best_repair = (
                        rank,
                        placed,
                        direction_search,
                        {
                            key: entry[key]
                            for key in (
                                "excludedDirections",
                                "allowHorizontalRepair",
                                "allowProvisionalParityWarning",
                                "preferSourceArrows",
                            )
                        },
                    )
                if penalty:
                    # Explore a few alternatives to the first legal direction
                    # sequence, using its most expensive relaxed cells as cues.
                    charged = sorted(
                        (n for n in affected if not n.get("linkedAnchor") and not n.get("streamEntry")),
                        key=lambda n: -sum(placement_repair_costs(n, repair_policy, level).values()),
                    )
                    for n in charged[:3]:
                        rep = representative[n["id"]]
                        if any(
                            d != n["d"] and (rep, d) not in exclusions
                            for d in ns[n["id"]]["geometryDirections"]
                        ):
                            pending.append((mode, exclusions | {(rep, n["d"])}))
                    continue
                break
            if len(trace) > 1:
                write(run / "placement-search.json", trace)
            return placed
    write(run / "placement-search.json", trace)
    if best_repair is not None:
        _, placed, direction_search, settings = best_repair
        write(run / "direction-input.json", {**direction_input, **settings})
        write(run / "direction-search.json", direction_search)
        if validate:
            validate(placed[0], direction_search)
        return placed
    last_error = audit_failure or joint_frontier or last_error
    if isinstance(last_error, PlacementDeadEnd):
        last_error.direction_failure = direction_failure
    elif direction_failure is not None:
        last_error = direction_failure
    raise last_error or direction_failure or ValueError("No supported direction path")


def check_parity(run, name, path, level, reentries=()):
    output = run / (name + "-parity.json")
    clock = level["clock"]
    lookup = {}
    if len(clock.starts) > 1 or clock.rates[0] != 60 / level["bpm"]:
        raw = load(path)
        for key in ("colorNotes", "bombNotes"):
            for event in raw[key]:
                beat = event.get("b", 0)
                proxy = clock.seconds(beat) * level["bpm"] / 60
                lookup[proxy] = beat
                event["b"] = proxy
        path = run / (name + "-parity-proxy.dat")
        write(path, raw)
    projected = {beat: proxy for proxy, beat in lookup.items()}
    cues = [
        {"hand": cue["hand"], "beat": projected.get(cue["startBeat"], cue["startBeat"])}
        for cue in reentries
    ]
    if cues:
        config = run / (name + "-parity-reentries.json")
        write(config, cues)
    if QUICKJS_EXE.is_file():
        process = run_quickjs("check_parity", json.dumps({
            "chart": load(path), "bpm": level["bpm"], "reentries": cues,
        }), output)
        if process.returncode:
            raise ValueError(f"QuickJS parity check failed: {process.stderr.strip()}")
    else:
        command = ["node", str(TOOLS / "check_parity.cjs"), str(path), str(output), "check", str(level["bpm"])]
        if cues:
            command.append(str(config))
        subprocess.run(command, check=True)
    if lookup:
        report = load(output)
        for item in report["issues"] + report["trace"]:
            item["beat"] = lookup[item["beat"]]
        report["timeBasis"] = "Elapsed seconds, reported in native beats"
        write(output, report)


def build(args):
    if getattr(args, "pattern_model", None):
        settings = load(args.pattern_model_settings) if args.pattern_model_settings else {}
        args.custom_pattern = model_callback("pattern", args.pattern_model, settings)
    link_choices = {}
    clearance_ids = set()
    released_anchors = set()
    simplified_hands = set()
    relaxed_exits = set()
    relaxed_pairs = set()
    relocated_returns = set()
    visibility_choices = {}
    repair_beats = []
    repair_parallel_diagonals = False
    for attempt in range(12):
        try:
            return build_chart(
                args,
                link_choices,
                clearance_ids,
                released_anchors,
                relaxed_exits,
                visibility_choices,
                relocated_returns,
                repair_beats,
                repair_parallel_diagonals,
                relaxed_pairs,
            )
        except (PlacementDeadEnd, DirectionDeadEnd) as error:
            if attempt == 11:
                raise
            if not getattr(args, "repair_joint_links", False):
                if not getattr(args, "retry_placement", False):
                    raise
                args = copy.copy(args)
                args.repair_joint_links = True
                print(f"Retrying placement with local repairs at beat {error.beat}", flush=True)
                continue
            path = Path(args.output) / "joint-link-repairs.json"
            repairs = load(path) if path.is_file() else []
            alternate_visibility = next(
                (
                    r
                    for r in reversed(repairs)
                    if "visibilityChoice" in r
                    and any(abs(error.beat - b) <= TOL for b in [r["beat"], *r["blockingBeats"]])
                    and r["visibilityChoice"] + 1 < r["alternatives"]
                ),
                None,
            )
            if alternate_visibility:
                repair = alternate_visibility
                visibility_choices[repair["releasedForVisibility"]] = (
                    repair["blockingNoteIds"],
                    repair["visibilityChoice"] + 1,
                )
                print(f"Retrying the other linked note for visibility at beat {repair['beat']}", flush=True)
                continue
            occlusion = getattr(error, "visibility", None)
            if occlusion and occlusion["target"] not in visibility_choices:
                visibility_choices[occlusion["target"]] = (occlusion["blockers"], 0)
                print(f"Retrying linked visibility at beat {error.beat}", flush=True)
                continue
            direction = (
                error if isinstance(error, DirectionDeadEnd) else getattr(error, "direction_failure", None)
            )
            if direction is not None:
                nearby = [n for swing in reversed(direction.swings) for n in swing]
                exits = {
                    n["id"] for n in nearby if n.get("intent", {}).get("kind") == "reset_exit"
                } - relaxed_exits
                if exits:
                    relaxed_exits.update(exits)
                    print(f"Retrying the reset exit at beat {direction.beat}", flush=True)
                    continue
                editable = {
                    n["id"]
                    for n in nearby
                    if not n.get("linkedAnchor") and not n.get("intent", {}).get("preservePosition")
                } - clearance_ids
                if editable:
                    clearance_ids.update(editable)
                    print(f"Retrying clearance for the direction path at beat {direction.beat}", flush=True)
                    continue
                pair = next((
                    n["intent"]["pairIds"] for n in nearby
                    if not n.get("linkedAnchor") and not n.get("streamEntry")
                    and n.get("intent", {}).get("kind") == "paired_hand_call_response"
                    and n["id"] not in relaxed_pairs
                ), None)
                if pair:
                    relaxed_pairs.update(pair)
                    print(f"Retrying the paired gesture with either vertical phase at beat {direction.beat}", flush=True)
                    continue
                anchor = next(
                    (n for n in nearby if n.get("linkedAnchor") and n["id"] not in released_anchors), None
                )
                if anchor and len(released_anchors) < 5:
                    released_anchors.add(anchor["id"])
                    print(
                        f"Retrying direction path after releasing a linked anchor at beat {anchor['b']}",
                        flush=True,
                    )
                    continue
                if anchor and anchor["c"] not in simplified_hands:
                    # Repeated failures after local releases indicate that this
                    # hand's attached gestures cannot carry the retained Easy rhythm.
                    rhythm = read_level(args.rhythm_source, args.rhythm_difficulty or args.difficulty)
                    hand = anchor["c"]
                    released_anchors.update(
                        n["id"] for n in rhythm["notes"] if n["c"] == hand and n["id"] in rhythm["anchorIds"]
                    )
                    simplified_hands.add(hand)
                    print(
                        f"Retrying without remaining linked anchors for {'left' if hand == 0 else 'right'} hand",
                        flush=True,
                    )
                    continue
            if isinstance(error, PlacementDeadEnd) and error.reason.startswith("Unexpected parity"):
                opening = set(getattr(error, "opening_dots", [])) - clearance_ids
                if opening:
                    clearance_ids.update(opening)
                    print(
                        f"Retrying the authored opening dot position for parity at beat {error.beat}",
                        flush=True,
                    )
                    continue
                anchor = next(
                    (
                        n
                        for n in error.notes
                        if n.get("linkedAnchor")
                        and n["id"] not in released_anchors
                        and abs(n["b"] - error.beat) <= TOL
                    ),
                    None,
                )
                if anchor and len(released_anchors) < 5:
                    released_anchors.add(anchor["id"])
                    print(
                        f"Retrying audited parity after releasing a linked anchor at beat {anchor['b']}",
                        flush=True,
                    )
                    continue
            if not isinstance(error, PlacementDeadEnd) or error.reason not in (
                "No geometric direction domain",
                "No source-relative geometry",
                "No joint geometry",
                "Bounded placement budget exhausted",
            ):
                raise
            resets = [n for n in error.notes if n.get("intent", {}).get("relocatableBombReset")]
            returns = {n["id"] for n in resets}
            neighbors = {
                other
                for n in resets
                if n["id"] in relocated_returns
                for other in n["intent"]["resetNeighbors"]
            }
            if error.reason == "No geometric direction domain" and (returns | neighbors) - relocated_returns:
                additions = (returns | neighbors) - relocated_returns
                relocated_returns.update(additions)
                print(f"Retrying {len(additions)} reset cells near beat {error.beat}", flush=True)
                continue
            if len(repair_beats) < MAX_REPAIR_WINDOWS and not any(
                abs(beat - error.beat) <= TOL for beat in repair_beats
            ):
                repair_beats.append(error.beat)
                print(f"Opening bounded placement search near beat {error.beat}", flush=True)
                continue
            alternate = next(
                (
                    r
                    for r in repairs
                    if "choice" in r
                    and any(abs(error.beat - b) <= TOL for b in [r["beat"], *r.get("affectedBeats", [])])
                    and r["choice"] + 1 < r["alternatives"]
                ),
                None,
            )
            if alternate:
                link_choices[alternate["beat"]] = alternate["choice"] + 1
                print(f"Retrying linked attachments at beat {error.beat}", flush=True)
                continue
            editable = {
                n["id"]
                for n in error.notes
                if not n.get("linkedAnchor") and not n.get("intent", {}).get("preservePosition")
            }
            if error.reason == "No source-relative geometry":
                # A relocated reset can have a legal direction but no distinct
                # cell left after its predecessor is placed.
                editable &= relocated_returns
            elif error.reason != "No joint geometry":
                raise
            if not (editable - clearance_ids):
                if error.reason == "No joint geometry" and not repair_parallel_diagonals:
                    direction_input = load(Path(args.output) / "direction-input.json")
                    source_notes = {n["id"]: n for n in direction_input["notes"]}
                    if parallel_double_constraints(error.notes, source_notes, frozenset(), (4, 5, 6, 7)):
                        repair_parallel_diagonals = True
                        print(f"Retrying authored parallel diagonals near beat {error.beat}", flush=True)
                        continue
                raise
            clearance_ids.update(editable)
            print(f"Retrying compatible cells at beat {error.beat}", flush=True)


def build_chart(
    args,
    link_choices,
    clearance_ids,
    released_anchors,
    relaxed_exits,
    visibility_choices,
    relocated_returns,
    repair_beats=(),
    repair_parallel_diagonals=False,
    relaxed_pairs=(),
):
    run = Path(args.output)
    if (run / "frozen.json").exists():
        raise FileExistsError("Refusing to overwrite a frozen run")
    difficulty = args.difficulty
    rhythm_difficulty = args.rhythm_difficulty or difficulty
    profile_path = args.profile or Path("profiles") / f"{difficulty.lower()}.json"
    profile = load(profile_path)
    if repair_beats:
        profile["boundedRepairBeats"] = repair_beats[:]
    if repair_parallel_diagonals:
        profile["repairParallelDiagonals"] = True
    if getattr(args, "repair_joint_links", False):
        profile["repairEmptyGeometryDomains"] = True
    original_level = read_level(args.source, args.source_difficulty)
    rhythm_level = read_level(args.rhythm_source, rhythm_difficulty)
    rhythm_level = simplify_high_crossed_links(rhythm_level, profile)
    expert_plus = standard_entries(original_level["info"]).get("ExpertPlus", {})
    expert_plus_njs = expert_plus.get("noteJumpMovementSpeed", expert_plus.get("_noteJumpMovementSpeed"))
    jump_input = (difficulty, rhythm_level["bpm"], expert_plus_njs)
    native_jump = getattr(args, "jump_settings", None)
    candidate_njs, candidate_offset, half_jump, jump_distance = (
        verified_jump_settings(*jump_input, load(native_jump)) if native_jump else jump_settings(*jump_input)
    )
    profile.update(njsMultiplier=1, jumpOffsetAddition=0)
    joint_link_repairs = []
    if getattr(args, "repair_joint_links", False):
        supplied_rhythm = rhythm_level
        rhythm_level, joint_link_repairs = repair_conflicting_links(
            supplied_rhythm, link_choices, released_anchors, visibility_choices
        )
        fixed = [
            dict(n, t=rhythm_level["clock"].seconds(n["b"]))
            for n in rhythm_level["notes"]
            if n["id"] in rhythm_level["anchorIds"]
        ]
        if len(fixed) > 1:
            approach = geometry_context(
                rhythm_level["notes"],
                {**rhythm_level, "njs": candidate_njs, "offset": candidate_offset},
                profile,
            )
            occlusions = {}
            for finding in vision_findings(fixed, [], approach["njs"], approach["approachSeconds"], DEFAULT):
                if finding["severity"] == "late_visibility_review":
                    occlusions.setdefault(finding["target"]["id"], set()).add(finding["occluderNoteId"])
            new_choices = {
                target: (sorted(blockers), 0)
                for target, blockers in occlusions.items()
                if target not in visibility_choices
            }
            if new_choices:
                visibility_choices.update(new_choices)
                rhythm_level, joint_link_repairs = repair_conflicting_links(
                    supplied_rhythm, link_choices, released_anchors, visibility_choices
                )
        write(run / "joint-link-repairs.json", joint_link_repairs)
    if abs(original_level["bpm"] - rhythm_level["bpm"]) > 0.0001:
        raise ValueError("Rhythm input BPM must match source")

    def songfile(l):
        return l["folder"] / l["info"].get("audio", {}).get("songFilename", l["info"].get("_songFilename"))

    if sha(songfile(original_level)) != sha(songfile(rhythm_level)):
        raise ValueError("Rhythm input must reference the same audio")
    clock = rhythm_level["clock"]
    source_clock = original_level["clock"]
    terminal = max(original_level["notes"][-1]["b"], rhythm_level["notes"][-1]["b"])
    if any(
        abs(clock.seconds(b) - source_clock.seconds(b)) > 0.001
        for b in set(clock.starts + source_clock.starts + [terminal])
    ):
        raise ValueError("Rhythm input tempo map must match source")
    level = {**copy.deepcopy(rhythm_level), "njs": candidate_njs, "offset": candidate_offset}
    seeded, seeding = seed_from_rhythm(
        original_level["notes"], rhythm_level["notes"], original_level["anchorIds"], rhythm_level["anchorIds"]
    )
    anchors = {
        n["id"]: {
            **n,
            "linkedAnchor": True,
            "intent": {"kind": "linked_anchor", "allowedDirections": [n["d"]]},
        }
        for n in rhythm_level["notes"]
        if n["id"] in rhythm_level["anchorIds"]
    }
    level["notes"] = [anchors.get(n["id"], n) for n in seeded]
    if getattr(args, "custom_pattern", None):
        _, cues = detect_features(level["notes"], level["bombs"], level["bpm"], clock)
        protected = rhythm_level["anchorIds"] | {
            note_id for cue in cues if cue["kind"] in ("bomb_return_reset", "reset_exit")
            for note_id in cue["noteIds"]
        }
        level["notes"] = pattern_seeds(
            args.custom_pattern, level["notes"], original_level, rhythm_level, difficulty, protected
        )
    write(run / "rhythm-seeding.json", seeding)
    if not (
        0 < profile["minimumSourceNoteRatio"] <= profile["maximumSourceNoteRatio"] <= 1
        and 0 <= profile["maximumSectionPruneFraction"] <= 0.2
    ):
        raise ValueError("Invalid Expert count envelope / pruning profile")
    if (run / "candidate").resolve() == level["folder"].resolve() or run.resolve() == level[
        "folder"
    ].resolve():
        raise ValueError("Output must not overwrite the source package")
    sourcepath = original_level["folder"] / original_level["filename"]
    sections, streams = section_plan(level["notes"], level["bpm"], profile, clock)
    ns, features = detect_features(level["notes"], level["bombs"], level["bpm"], clock)
    for n in ns:
        if n["id"] in relaxed_exits and n.get("intent", {}).get("kind") == "reset_exit":
            n["intent"].pop("allowedDirections", None)
    policy = geometry_context(ns, level, profile, sections)
    if (
        relaxed_pairs
        or profile.get("flexiblePairedPhase")
        or policy["controls"].get("compactPairedCells")
        or policy["controls"].get("compactDeepPairedCells")
    ):
        reassignments = {s["id"] for s in seeding if s["mirroredOtherHandSeed"]}
        for n in ns:
            intent = n.get("intent", {})
            if intent.get("kind") != "paired_hand_call_response":
                continue
            if profile.get("flexiblePairedPhase") or n["id"] in relaxed_pairs:
                intent["allowedDirections"] = [0, 1]
                intent["flexiblePhase"] = True
            if policy["controls"].get("compactPairedCells"):
                x, _ = intent["preferredCell"]
                if any(i in reassignments for i in intent["pairIds"]):
                    x = 1 if n["c"] == 0 else 2
                else:
                    x = min(x, 2) if n["c"] == 0 else max(x, 1)
                intent["preferredCell"] = [x, 0]
            elif policy["controls"].get("compactDeepPairedCells"):
                x, _ = intent["preferredCell"]
                if x == 3 * (1 - n["c"]):
                    intent["preferredCell"] = [2 if n["c"] == 0 else 1, 0]
    if profile.get("preserveSourceResets", False):
        project_bomb_returns(
            ns, features, original_level, seeding, policy["controls"]["bottomRowDownstrokes"]
        )
    adjusted_resets = (
        open_crossed_reset_cells(ns, features, level["bombs"], clock)
        if getattr(args, "repair_joint_links", False)
        else []
    )
    if getattr(args, "repair_joint_links", False):
        resets = [f for f in features if f["kind"] == "bomb_return_reset"]
        repaired = open_reset_repair_cells(ns, resets, level["bombs"], relocated_returns)
        adjusted_resets.extend(f for f in repaired if f not in adjusted_resets)
    if profile.get("supportInterleavedCrossovers", False):
        mark_crossover_support(ns, features, level["bpm"], clock)
    reentries = mark_bomb_reentries(ns, level["bombs"], clock) if profile.get("restedBombReentry") else []
    features.extend(reentries)
    for n in ns:
        n["recoveryCue"] = crouch_recovery_cue(n, policy)
    if profile.get("comfortableDoubles"):
        parallels = parallel_double_directions(ns)
        for n in ns:
            if n["id"] in parallels:
                n["parallelDoubleDirection"] = parallels[n["id"]]
    pruning = [
        {"operation": "disabled", "reason": "Supplied rhythm is authoritative; automatic pruning deferred"}
    ]
    write(run / "intent-features.json", features)
    entries = {i for s in streams if s["sourceDownDownEntry"] for i in s["noteIds"][:2]}
    for n in ns:
        n["streamEntry"] = n["id"] in entries
    diagonal_preference = profile.get("diagonalPreference", 0)
    if not math.isfinite(diagonal_preference) or diagonal_preference < 0:
        raise ValueError("diagonalPreference must be finite and nonnegative")
    drumming = profile.get("drumming", {})
    drum_gap = drumming.get("minimumGapSeconds", math.inf)
    drum_cost = drumming.get("downstrokePreference", 0)
    adherence = drumming.get("parityAdherence", 0.5)
    sparse_drum_gap = drumming.get("sparseMinimumGapSeconds", drum_gap)
    beginner = profile.get("beginnerDirections", {})
    outward_cost = beginner.get("outwardPreference", 0)
    upward_repeat = beginner.get("repeatedUpPenalty", 0)
    synchronized_double = beginner.get("synchronizedDoublePreference", 0)
    horizontal_retention = profile.get("horizontalRetention", 0)
    if drumming and (
        not 0.75 < sparse_drum_gap <= drum_gap < math.inf
        or not 0 <= drum_cost < math.inf
        or not 0 <= adherence <= 1
    ):
        raise ValueError("Invalid drumming gap, preference or parity adherence")
    if any(
        not math.isfinite(value) or value < 0
        for value in (outward_cost, upward_repeat, synchronized_double, horizontal_retention)
    ):
        raise ValueError("Direction preferences must be finite and nonnegative")
    group_hands = {n["id"]: len({v["c"] for v in group}) for group in groups(ns).values() for n in group}
    for n in ns:
        if n.get("intent") or n["streamEntry"]:
            continue
        gap = policy["gaps"][n["id"]]
        minimum_gap = sparse_drum_gap if n["id"] in policy["sparseAllowance"] else drum_gap
        if gap >= minimum_gap and drum_cost:
            n["downstrokePreference"] = drum_cost * policy["relaxation"][n["id"]]
            n["minimumDrumGapSeconds"] = minimum_gap
        outward_context = beginner.get("outwardOnDoubles", True) or group_hands[n["id"]] == 1
        if not beginner.get("outwardBesideBombs", True) and any(
            abs(b["b"] - n["b"]) < 0.0001 for b in level["bombs"]
        ):
            outward_context = False
        if (
            outward_cost
            and outward_context
            and policy["controls"]["ownSideOutward"]
            and gap >= 0.5
            and policy["wallFree"][n["id"]]
        ):
            n["outwardPreference"] = outward_cost * (1 + policy["sparseAllowance"].get(n["id"], 0))
        if upward_repeat:
            n["repeatedUpPenalty"] = upward_repeat
        if horizontal_retention and gap >= 0.30 and n["d"] in (2, 3):
            n["horizontalRetention"] = horizontal_retention
        outward_diagonal = n["d"] in ((4, 6) if n["c"] == 0 else (5, 7))
        if (
            profile.get("flattenOutwardDiagonals", False)
            and outward_diagonal
            and not crossover_depth(n)
            and gap >= 0.29
            and policy["wallFree"][n["id"]]
            and group_hands[n["id"]] == 1
        ):
            n["flattenOutward"] = True
    for hand in (0, 1):
        swings = list(groups([n for n in ns if n["c"] == hand]).values())
        for index, swing in enumerate(swings):
            neighbors = swings[max(0, index - 1) : index] + swings[index + 1 : index + 2]
            for n in swing:
                if n.get("intent") or n["streamEntry"] or not diagonal_preference:
                    continue
                straight_context = len(neighbors) == 2 and all(
                    other["d"] in (0, 1) and abs(clock.gap(n["b"], other["b"])) <= 0.75
                    for group in neighbors
                    for other in group
                )
                beside_bomb = any(abs(bomb["b"] - n["b"]) <= 0.0001 for bomb in level["bombs"])
                busy_deep_palm = (
                    crossover_depth(n) == 2 and n["d"] in (6, 7) and policy["gaps"][n["id"]] <= 0.35
                )
                extra = 0.65 * (straight_context or busy_deep_palm) + 1.0 * beside_bomb
                n["diagonalCost"] = (diagonal_preference + extra) * policy["relaxation"][n["id"]]
    write(
        run / "section-plan.json",
        {"sections": sections, "streams": streams, "pruning": pruning, "profile": profile},
    )
    ns = geometry_direction_domains(ns, level, profile, clearance_ids)
    if len(clock.starts) > 1 or clock.rates[0] != 60 / level["bpm"]:
        for n in ns:
            n["seconds"] = clock.seconds(n["b"])
    direction_input = {
        "notes": ns,
        "bombs": level["bombs"],
        "bpm": level["bpm"],
        "controls": policy["controls"],
        "drumming": drumming,
        "beginnerDirections": beginner,
        "transitionPreferences": profile.get("transitionPreferences", {}),
    }
    source_diag = diagnostic_package(level, level["notes"], run / "diagnostic-source")
    check_parity(run, "source", source_diag / "Expert.beatmap.dat", level, reentries)
    source_report = load(run / "source-parity.json")

    def validate_parity(placed, direction_search):
        if adjusted_resets and not preserves_reset_cues(placed, level["bombs"], clock, adjusted_resets):
            raise ValueError("Relocated reset lost its displaced repeat and original bomb cue")
        diagnostic = diagnostic_package(
            level, placed, run / "diagnostic-candidate", candidate_njs, candidate_offset
        )
        check_parity(run, "candidate", diagnostic / "Expert.beatmap.dat", level, reentries)
        placed_policy = geometry_context(placed, level, profile, sections)
        issue = unexpected_parity(
            placed,
            level,
            profile,
            placed_policy,
            features,
            direction_search,
            source_report,
            load(run / "candidate-parity.json"),
        )
        if issue is not None:
            beat, hand = issue["beat"], issue["hand"]
            swings = groups([n for n in placed if n["c"] == hand and n["b"] <= beat + TOL])
            repair_notes = [n for group in reversed(list(swings.values())[-3:]) for n in group]
            side = "left" if hand == 0 else "right"
            error = PlacementDeadEnd(
                beat, repair_notes, f"Unexpected parity {issue['status']} for {side} hand"
            )
            first = next(iter(swings.values()), [])
            if len(swings) <= 4 and all(n["d"] == 8 for n in first):
                original = {n["id"]: n for n in level["notes"]}
                error.opening_dots = [
                    n["id"]
                    for n in first
                    if (n["x"], n["y"]) != (original[n["id"]]["x"], original[n["id"]]["y"])
                ]
            raise error

    placed = plan_geometry(run, direction_input, level, sections, profile, validate_parity)
    if not adjusted_resets:
        placed = prefer_roomier_doubles(run, placed, level, sections, profile, validate_parity)
    candidate, edits, queue = placed
    policy = geometry_context(candidate, level, profile, sections)
    weighted_repairs = weighted_repair_report(candidate, policy, level)
    if repair_beats:
        write(run / "bounded-repairs.json", {"windowCenters": repair_beats, "relaxations": weighted_repairs})
    violations = [
        {"id": n["id"], "beat": n["b"], "hand": n["c"], "reasons": placement_exclusions(n, policy, level)}
        for n in candidate
        if placement_exclusions(n, policy, level)
    ]
    domain_repairs = [
        {
            "id": n["id"],
            "beat": n["b"],
            "hand": n["c"],
            "kind": n["domainRepair"],
            "strictExclusions": strict_placement_exclusions({**n, "domainRepair": None}, policy, level),
        }
        for n in candidate
        if n.get("domainRepair")
    ]
    seed_policy = geometry_context(level["notes"], level, profile)
    write(
        run / "placement-policy.json",
        {
            "controls": policy["controls"],
            "difficultyRamp": policy["ramp"],
            "sparseAllowanceNotes": len(policy["sparseAllowance"]),
            "candidateFindings": violations,
            "emptyDomainRepairs": domain_repairs,
            "weightedPlacementRepairs": weighted_repairs,
            "preservedAnchorFindings": [
                {"id": n["id"], "beat": n["b"], "hand": n["c"], "reasons": exclusions(n, policy)}
                for n in candidate
                if n.get("linkedAnchor") and exclusions(n, policy)
            ],
            "seedFindings": [
                {"id": n["id"], "beat": n["b"], "hand": n["c"], "reasons": exclusions(n, seed_policy)}
                for n in level["notes"]
                if exclusions(n, seed_policy)
            ],
            "model": "absolute head occupancy, top-row eligibility, crouch recovery and central readability guards; thresholds provisional",
        },
    )
    assert not violations, "Absolute placement-policy violation"
    write(run / "geometry-edits.json", edits)
    write(run / "normalized-candidate.json", candidate)
    path = export(
        level, candidate, run / "candidate", profile, difficulty, args.title_suffix, constant_njs=True,
        foundation=original_level,
    )
    diag = run / "diagnostic-candidate"
    diagnostic_package(original_level, original_level["notes"], run / "diagnostic-original")
    write(run / "source-expanded-screening.json", motion_audit(run / "diagnostic-source", "Expert"))
    write(run / "geometry.json", audit(diag / "Expert.beatmap.dat"))
    write(run / "expanded-screening.json", motion_audit(diag, "Expert"))
    write(run / "transitions.json", transition_audit(diag, "Expert"))
    write(run / "seeded-baseline.dat", gameplay(level["raw"], level["notes"]))
    if reentries:
        for name, package in [("source", source_diag), ("candidate", diag)]:
            check_parity(run, name + "-raw", package / "Expert.beatmap.dat", level)
    ratio = len(candidate) / len(original_level["notes"])
    assert rhythm_signature(candidate) == rhythm_signature(rhythm_level["notes"])
    write(
        run / "draft.json",
        {
            "sourceFolder": str(original_level["folder"]),
            "sourceDifficulty": args.source_difficulty,
            "sourceFilename": original_level["filename"],
            "sourceSha256": sha(sourcepath),
            "profileFile": str(profile_path),
            "sourceNotes": len(original_level["notes"]),
            "targetDifficulty": difficulty,
            "rhythmSourceFolder": str(rhythm_level["folder"]),
            "rhythmDifficulty": rhythm_difficulty,
            "rhythmFilename": rhythm_level["filename"],
            "rhythmSha256": sha(rhythm_level["folder"] / rhythm_level["filename"]),
            "rhythmNotes": len(rhythm_level["notes"]),
            "candidateNotes": len(candidate),
            "retainedRatio": ratio,
            "bpm": level["bpm"],
            "rhythmInputTimesHandsMultiplicitiesPreserved": True,
            "rhythmBombsPreserved": len(level["bombs"]),
            "rhythmWallsPreserved": len(level["walls"]),
            "parityBaseline": "Seeded rhythm and supplied choreography, not unpruned Expert+",
            "authoredLinkedAnchorsPreserved": len(anchors),
            "tempoRegions": len(clock.starts),
            "jumpSettings": {
                "njs": candidate_njs,
                "startBeatOffset": candidate_offset,
                "halfJumpDuration": half_jump,
                "jumpDistance": jump_distance,
            },
            "jointLinkRepairs": joint_link_repairs,
            "comfortLinksReleased": rhythm_level.get("simplifiedLinkCount", 0),
            "relaxedResetExitNoteIds": sorted(relaxed_exits),
            "adjustedResetBeats": [(f["hand"], f["startBeat"], f["endBeat"]) for f in adjusted_resets],
            "adjustedResetCues": adjusted_resets,
            "emptyDomainRepairs": domain_repairs,
            "sourceNjsEvents": len(original_level["raw"].get("njsEvents", [])),
            "rhythmNjsEventsRemoved": len(rhythm_level["raw"].get("njsEvents", [])),
            "manualNoteGeometryUsedForGeneration": bool(anchors),
            "automaticPruning": False,
            "mode": "Supervised rhythm-supplied pattern experiment; not a blind full-downmapping evaluation",
            "unresolved": queue,
            "limitations": [
                "Authored arc and chain paths and attached anchors are preserved; motion screening covers colored notes, not linked trajectories",
                "Intent features are structural hypotheses, not musical understanding",
                "Geometry and direction searches are separate",
                "No listening or physical playtest",
            ],
        },
    )
    print("Draft:", len(candidate), "notes; supplied rhythm preserved exactly; BPM", level["bpm"])


def unexpected_parity(
    normalized, level, profile, policy, intent_features, direction_search, source_report, candidate_report
):
    """Identify a final parity finding not justified by the same approval rules as freeze."""
    source_issues = {(round(i["beat"], 4), i["hand"], i["status"]) for i in source_report["issues"]}
    drumming = profile.get("drumming", {})
    drum_gap = drumming.get("minimumGapSeconds", math.inf)
    sparse_drum_gap = drumming.get("sparseMinimumGapSeconds", drum_gap)
    planned = {(r["hand"], r["fromBeat"], r["beat"]) for r in direction_search.get("drummingRepeats", [])}
    projected = {
        (f["hand"], f["startBeat"], f["endBeat"], f["direction"])
        for f in intent_features
        if f["kind"] == "projected_bomb_return"
    }
    verified_repeats = set()
    verified_resets = {}
    for hand in (0, 1):
        swings = list(groups([n for n in normalized if n["c"] == hand]).values())
        for before, after in zip(swings, swings[1:]):
            start, end = before[0]["b"], after[0]["b"]
            if (
                len(before) == len(after) == 1
                and before[0]["d"] == after[0]["d"]
                and (hand, start, end, after[0]["d"]) in projected
            ):
                verified_resets[(round(end, 4), hand)] = after[0]["d"]
            minimum_gap = (
                sparse_drum_gap if all(n["id"] in policy["sparseAllowance"] for n in after) else drum_gap
            )
            if (
                (hand, start, end) in planned
                and all(n["d"] == 1 for n in before + after)
                and min(policy["gaps"][n["id"]] for n in after) >= minimum_gap
                and not any(start < b["b"] <= end for b in level["bombs"])
            ):
                verified_repeats.add((round(end, 4), hand))
    for issue in candidate_report["issues"]:
        beat, hand = round(issue["beat"], 4), issue["hand"]
        expected_drumming = (beat, hand) in verified_repeats and all(d == 1 for d in issue["directions"])
        expected_reset = issue["directions"] == [verified_resets.get((beat, hand))]
        inherited = (beat, hand, issue["status"]) in source_issues or (
            issue["status"] == "warning" and (beat, hand, "error") in source_issues
        )
        if not (inherited or expected_drumming or expected_reset):
            return issue
    if len(verified_resets) != len(projected):
        raise ValueError("Projected source reset changed before freezing")
    return None


def freeze(args):
    run = Path(args.output)
    if (run / "frozen.json").exists():
        raise FileExistsError("Already frozen")
    draft = load(run / "draft.json")
    level = read_level(run / "candidate", draft.get("targetDifficulty", "Expert"))
    normalized = level["notes"]
    original = read_level(draft["sourceFolder"], draft.get("sourceDifficulty", "ExpertPlus"))
    assert sha(original["folder"] / original["filename"]) == draft["sourceSha256"]
    rhythm = read_level(draft["rhythmSourceFolder"], draft["rhythmDifficulty"])
    assert sha(rhythm["folder"] / rhythm["filename"]) == draft["rhythmSha256"]
    plan = load(run / "section-plan.json")
    profile = plan["profile"]
    rhythm = simplify_high_crossed_links(rhythm, profile)
    assert rhythm.get("simplifiedLinkCount", 0) == draft.get("comfortLinksReleased", 0)
    if draft.get("jointLinkRepairs"):
        choices = {
            repair["beat"]: repair["choice"] for repair in draft["jointLinkRepairs"] if "choice" in repair
        }
        released = {
            repair["releasedForDirection"]
            for repair in draft["jointLinkRepairs"]
            if "releasedForDirection" in repair
        }
        visibility = {
            r["releasedForVisibility"]: (r["blockingNoteIds"], r["visibilityChoice"])
            for r in draft["jointLinkRepairs"]
            if "releasedForVisibility" in r
        }
        rhythm, repairs = repair_conflicting_links(rhythm, choices, released, visibility)
        assert repairs == draft["jointLinkRepairs"], "Linked repair differs from the screened plan"
    assert rhythm_signature(normalized) == rhythm_signature(rhythm["notes"])
    policy = geometry_context(normalized, level, {**profile, "njsMultiplier": 1}, plan["sections"])
    note_shape = lambda n: tuple(n[k] for k in ["b", "c", "x", "y", "d", "a"])
    signature = lambda ns: sorted(map(note_shape, ns))
    assert signature(n for n in normalized if n["id"] in level["anchorIds"]) == signature(
        n for n in rhythm["notes"] if n["id"] in rhythm["anchorIds"]
    ), "Linked note attachments changed"
    assert level["linkedEndpoints"] == rhythm["linkedEndpoints"] and level["audioData"] == rhythm["audioData"]
    geometry_keys = (
        "arcs",
        "arcsData",
        "chains",
        "chainsData",
        "_sliders",
        "sliders",
        "burstSliders",
        "bpmEvents",
    )
    assert all(level["raw"].get(k) == rhythm["raw"].get(k) for k in geometry_keys), (
        "Linked object or tempo data changed"
    )
    assert not level["raw"].get("njsEvents"), "Candidate must use the constant NJS checked during placement"
    assert level["bombs"] == rhythm["bombs"] and level["walls"] == rhythm["walls"], (
        "Bomb or wall layout changed"
    )
    if rhythm["audioData"] is not None:
        for filename in (
            rhythm["info"]["audio"]["audioDataFilename"],
            rhythm["meta"]["lightshowDataFilename"],
        ):
            assert sha(level["folder"] / filename) == sha(rhythm["folder"] / filename), (
                "Timing or lighting asset changed"
            )
    assert signature(normalized) == signature(
        decode4(load(run / "diagnostic-candidate/Expert.beatmap.dat"))
    ), "Candidate changed since screening"
    repairs = {}
    if draft.get("emptyDomainRepairs"):
        assert profile.get("repairEmptyGeometryDomains")
        domains = {n["id"]: n for n in load(run / "direction-input.json")["notes"]}
        for n in load(run / "normalized-candidate.json"):
            kind = n.get("domainRepair")
            if not kind:
                continue
            planned = domains[n["id"]]
            assert kind == planned.get("domainRepair") and (n["b"], n["c"]) == (planned["b"], planned["c"])
            assert n["d"] in planned["geometryDirections"]
            if kind == "reset_cell":
                assert planned["intent"]["kind"] == "bomb_return_reset" and note_shape(n) == note_shape(
                    planned
                )
            if kind == "stack_clearance":
                assert sum(m["c"] == n["c"] and abs(m["b"] - n["b"]) <= TOL for m in normalized) > 1
            repairs[note_shape(n)] = kind
    screened = [{**n, "domainRepair": repairs.get(note_shape(n))} for n in normalized]
    weighted_repairs = weighted_repair_report(screened, policy, level)
    recorded_repairs = load(run / "placement-policy.json").get("weightedPlacementRepairs", [])
    assert weighted_repairs == recorded_repairs, "Placement rule charges changed before freeze"
    center_repairs = {row["beat"] for row in weighted_repairs if "central_readability_window" in row["rules"]}
    for n in normalized:
        kind = repairs.get(note_shape(n))
        if kind in (
            "clearance",
            "stack_clearance",
        ) and "central_readability_window" in strict_placement_exclusions(n, policy, level):
            center_repairs.add(n["b"])
        assert not placement_exclusions({**n, "domainRepair": kind}, policy, level), (
            "Absolute placement exclusion in final native map"
        )
    geo = load(run / "geometry.json")
    assert not any(f["rule"] != "G04" for f in geo["findings"])
    intent_features = load(run / "intent-features.json")
    if draft.get("adjustedResetCues"):
        assert preserves_reset_cues(normalized, level["bombs"], level["clock"], draft["adjustedResetCues"]), (
            "Relocated reset lost its original bomb cue"
        )
    elif draft.get("adjustedResetBeats"):
        actual = {
            (a["c"], a["b"], b["b"])
            for a, b, _ in bomb_return_pairs(normalized, level["bombs"], level["clock"])
        }
        assert {tuple(f) for f in draft["adjustedResetBeats"]} <= actual, "Relocated reset lost its bomb cue"
    reentry_key = lambda f: (f["hand"], f["startBeat"], f["endBeat"])
    planned_reentries = {reentry_key(f) for f in intent_features if f["kind"] == "rested_bomb_reentry"}
    if planned_reentries:
        authored_shape = [dict(n, linkedAnchor=n["id"] in level["anchorIds"]) for n in normalized]
        actual_reentries = mark_bomb_reentries(authored_shape, level["bombs"], level["clock"])
        assert planned_reentries <= {reentry_key(f) for f in actual_reentries}, (
            "Rested lead-in lost its alternating stroke shape"
        )
        contextual = load(run / "candidate-parity.json")
        assert contextual["appliedRestedReentries"] == len(planned_reentries)
        issue_keys = lambda report: {(i["hand"], i["beat"], i["status"]) for i in report["issues"]}
        difference = issue_keys(load(run / "candidate-raw-parity.json")) ^ issue_keys(contextual)
        assert all(
            any(hand == c and start <= beat <= end for c, start, end in planned_reentries)
            for hand, beat, _ in difference
        ), "Rested reentry changed parity outside its recorded phrase"
    issue = unexpected_parity(
        normalized,
        level,
        profile,
        policy,
        intent_features,
        load(run / "direction-search.json"),
        load(run / "source-parity.json"),
        load(run / "candidate-parity.json"),
    )
    assert issue is None, f"Unexpected parity finding needs review before freezing: {issue}"
    source_visibility = {
        (round(f["beat"], 4), f["rule"])
        for f in load(run / "source-expanded-screening.json")["findings"]
        if f.get("severity") == "late_visibility_review"
    }
    repaired_visibility = {
        (round(r["beat"], 4), r["hand"])
        for r in draft.get("jointLinkRepairs", [])
        if "releasedForVisibility" in r
    }
    for finding in load(run / "expanded-screening.json")["findings"]:
        if finding.get("severity") != "late_visibility_review":
            continue
        assert (round(finding["beat"], 4), finding["target"]["c"]) not in repaired_visibility, (
            "Linked visibility repair requires a clear approach"
        )
        assert (round(finding["beat"], 4), finding["rule"]) in source_visibility, (
            "New late visibility finding needs review before freezing"
        )
        assert finding["beat"] not in center_repairs and not (
            finding["rule"] == "V01" and finding.get("occluderBeat") in center_repairs
        ), "Center row repair requires a clear approach"
    write(
        run / "frozen.json",
        {
            **draft,
            "candidateSha256": sha(level["folder"] / level["filename"]),
            "candidateFilename": level["filename"],
            "status": "unverified rhythm-supplied candidate; final positional comparison follows freezing; prior supervised studies are known",
        },
    )
    print("Frozen", sha(level["folder"] / level["filename"]))


def compare(args):
    run = Path(args.output)
    frozen = load(run / "frozen.json")
    difficulty = frozen.get("targetDifficulty", "Expert")
    candidate = read_level(run / "candidate", difficulty)
    assert sha(candidate["folder"] / candidate["filename"]) == frozen["candidateSha256"]
    source = read_level(frozen["sourceFolder"], frozen.get("sourceDifficulty", "ExpertPlus"))
    try:
        reference = read_level(frozen["sourceFolder"], difficulty)
    except DifficultyNotFound:
        reference = read_level(frozen["rhythmSourceFolder"], frozen["rhythmDifficulty"])
    diagnostic_package(reference, reference["notes"], run / "diagnostic-reference")
    foundation = frozen.get("sourceDifficulty", "ExpertPlus")
    maps = {foundation: source, "Reference": reference, "Candidate": candidate}
    summary = {}
    audio = load(run / "diagnostic-source/AudioData.dat")
    for label, level in maps.items():
        d = load(
            run
            / (
                "diagnostic-"
                + {foundation: "original", "Reference": "reference", "Candidate": "candidate"}[label]
            )
            / "Expert.beatmap.dat"
        )
        summary[label] = metrics(
            level["notes"],
            d,
            audio,
            {"noteJumpMovementSpeed": level["njs"], "noteJumpStartBeatOffset": level["offset"]},
        )
        summary[label].update(
            arcs=len(level["raw"].get("arcs", level["raw"].get("sliders", level["raw"].get("_sliders", [])))),
            chains=len(level["raw"].get("chains", level["raw"].get("burstSliders", []))),
            angleOffsetNotes=sum(n["a"] != 0 for n in level["notes"]),
        )
    comparisons = {
        a + "->" + b: pair(maps[a]["notes"], maps[b]["notes"])[0]
        for a, b in [(foundation, "Candidate"), (foundation, "Reference"), ("Reference", "Candidate")]
    }
    write(
        run / "comparison.json",
        {
            "frozenCandidateVerified": True,
            "mode": "Rhythm supplied separately; compare geometry/flow, not independent pruning performance",
            "referenceSha256": sha(reference["folder"] / reference["filename"]),
            "metrics": summary,
            "comparisons": comparisons,
        },
    )
    sections = load(run / "section-plan.json")["sections"]
    write(
        run / "section-comparison.json",
        [
            {
                **s,
                "counts": {
                    k: sum(s["startBeat"] <= n["b"] < s["endBeatExclusive"] for n in v["notes"])
                    for k, v in maps.items()
                },
            }
            for s in sections
        ],
    )
    print(
        {
            k: {x: v[x] for x in ["notes", "timingGroups", "diagonalPct", "topRowPct"]}
            for k, v in summary.items()
        }
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("build")
    p.add_argument("--source", required=True, type=Path)
    p.add_argument(
        "--source-difficulty",
        choices=("Easy", "Normal", "Hard", "Expert", "ExpertPlus"),
        default="ExpertPlus",
    )
    p.add_argument("--rhythm-source", required=True, type=Path)
    p.add_argument("--difficulty", choices=("Easy", "Normal", "Hard", "Expert"), default="Expert")
    p.add_argument(
        "--rhythm-difficulty", help="Authored rhythm difficulty. Defaults to the output difficulty."
    )
    p.add_argument("--profile", type=Path, help="Override the profile selected by --difficulty.")
    p.add_argument("--jump-settings", type=Path, help=argparse.SUPPRESS)
    p.add_argument("--pattern-model")
    p.add_argument("--pattern-model-settings", type=Path)
    p.add_argument(
        "--repair-joint-links",
        action="store_true",
        help="Release conflicting fixed links while preserving all note timings.",
    )
    p.add_argument(
        "--retry-placement",
        action="store_true",
        help="Try bounded placement repairs if the initial search fails.",
    )
    p.add_argument(
        "--title-suffix", default="", help='Append a bracketed label to the song title, such as "r05 Expert".'
    )
    p.add_argument("--output", required=True, type=Path)
    for name in ["freeze", "compare"]:
        sub.add_parser(name).add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    {"build": build, "freeze": freeze, "compare": compare}[args.command](args)


if __name__ == "__main__":
    main()
