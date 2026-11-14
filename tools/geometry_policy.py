"""Explicit geometry controls and absolute placement exclusions.

Controls are preferences/eligibility settings, not calibrated human difficulty.
All time windows use seconds, derived from the current map's tempo.
"""

import math
from bisect import bisect_left, bisect_right
from analyze_standard_spreads import groups
from audit_placements import distance as point_segment_distance, segment, segment_distance
from audit_motion_visibility import DEFAULT as MOTION, hjd, ray_box
from beat_clock import clock_for
from repair_search import repair_windows, side_wall_recovery_gap

DEFAULTS = {
    "height": 0.35,
    "width": 0.75,
    "crossover": 0.55,
    "sparseSectionAllowance": 0.0,
    "sideWallRecoverySeconds": 0.0,
    "ownSideOutward": False,
    "bottomRowDownstrokes": False,
    "bottomRowOuterInward": False,
    "crouchMiddleRecoverySeconds": 0.0,
    "bombSwingClearanceMeters": 0.6,
    "oppositeInlineSeconds": 0.0,
    "sameHandInlineSeconds": 0.0,
    "topRowSpacingSeconds": 0.55,
    "topPalmSpacingSeconds": 1.0,
    "topDotSpacingSeconds": 0.9,
    "crouchRecoverySeconds": 0.85,
    "centerVisionWindowSeconds": 0.75,
    "noteBombHeadClearanceMeters": 0.45,
    "reachSlackMeters": 0.2,
    "sourcePositionCost": 1.0,
    "recoveryResetSeconds": 0.7,
    "recoveryResetCost": 6.0,
    "resetCueMinimumCrouchSeconds": 0.2,
}


def ramp_settings(profile):
    ramp = {"enabled": False, "startFraction": 0.75, "amount": 0.25, **profile.get("difficultyRamp", {})}
    if (
        not isinstance(ramp["enabled"], bool)
        or not 0 <= ramp["startFraction"] < 1
        or not 0 <= ramp["amount"] <= 1
    ):
        raise ValueError("Invalid difficulty ramp settings")
    return ramp


def crossover_depth(n):
    return max(0, n["x"] - 1) if n["c"] == 0 else max(0, 2 - n["x"])


def outward_direction(hand):
    return 2 if hand == 0 else 3


def inline_readability_cost(a, b, ctx):
    if a["x"] != b["x"]:
        return 0
    c = ctx["controls"]
    same_hand = a["c"] == b["c"]
    window = c["sameHandInlineSeconds"] if same_hand else c["oppositeInlineSeconds"]
    gap = abs(ctx["clock"].gap(a["b"], b["b"]))
    if not 0 < gap <= window:
        return 0
    if a["y"] == b["y"]:
        return 5
    return 0 if same_hand else 1.5


def introduces_hand_inversion(trial, source):
    red = [n for n in trial if n["c"] == 0]
    blue = [n for n in trial if n["c"] == 1]
    return any(r["x"] > b["x"] and source[r["id"]]["x"] <= source[b["id"]]["x"] for r in red for b in blue)


def controls(profile):
    c = {**DEFAULTS, **profile.get("geometry", {})}
    if any(
        not 0 <= c[k] <= 1
        for k in ["height", "width", "crossover", "sparseSectionAllowance"]
    ):
        raise ValueError("Normalized geometry controls must lie in [0,1]")
    if any(not isinstance(v, (int, float)) or not math.isfinite(v) or v < 0 for v in c.values()):
        raise ValueError("Geometry controls must be finite nonnegative numbers")
    return c


def context(ns, level, profile, sections=()):
    c = controls(profile)
    bpm = level["bpm"]
    gaps = {}
    global_groups = list(groups(ns).values())
    next_gap = {}
    clock = clock_for(level)
    ramp = ramp_settings(profile)
    first, last = global_groups[0][0]["b"], global_groups[-1][0]["b"]
    relaxation = {}
    for n in ns:
        progress = clock.gap(first, n["b"]) / clock.gap(first, last) if last > first else 0
        fraction = max(0, (progress - ramp["startFraction"]) / (1 - ramp["startFraction"]))
        relaxation[n["id"]] = 1 - ramp["amount"] * fraction if ramp["enabled"] else 1
    local_spacing = {}
    for i, g in enumerate(global_groups):
        before = clock.gap(global_groups[i - 1][0]["b"], g[0]["b"]) if i else math.inf
        after = clock.gap(g[0]["b"], global_groups[i + 1][0]["b"]) if i + 1 < len(global_groups) else math.inf
        for n in g:
            next_gap[n["id"]] = after
            local_spacing[n["id"]] = min(before, after)
    for hand in [0, 1]:
        gs = list(groups([n for n in ns if n["c"] == hand]).values())
        for i, g in enumerate(gs):
            before = clock.gap(gs[i - 1][0]["b"], g[0]["b"]) if i else math.inf
            after = clock.gap(g[0]["b"], gs[i + 1][0]["b"]) if i + 1 < len(gs) else math.inf
            for n in g:
                gaps[n["id"]] = min(before, after)
    njs = level["njs"] * profile.get("njsMultiplier", 1)
    approach = hjd(bpm, njs, level.get("offset", 0) + profile.get("jumpOffsetAddition", 0)) * 60 / bpm
    bomb_times = sorted((clock.seconds(b["b"]), i) for i, b in enumerate(level["bombs"]))
    window = (
        max(
            approach,
            c["bombSwingClearanceMeters"] / njs,
            c["noteBombHeadClearanceMeters"] / njs,
            0.375 * 60 / bpm,
            1e-9,
        )
        + 1e-9
    )
    nearby_bombs = {}
    for n in ns:
        beat = n["b"]
        t = clock.seconds(beat)
        # Keep the exact-beat head overlap check even if its configured margin is zero.
        first = bisect_left(bomb_times, (min(t - window, clock.seconds(beat - 0.0001)) - 1e-9, -1))
        last = bisect_right(
            bomb_times, (max(t + window, clock.seconds(beat + 0.0001)) + 1e-9, len(bomb_times))
        )
        nearby_bombs[n["id"]] = [level["bombs"][i] for i in sorted(i for _, i in bomb_times[first:last])]
    wall_free = {}
    sparse = {}
    for n in ns:
        beat = n["b"]
        clear = not any(
            clock.gap(beat, w["b"]) <= 0.5 and clock.gap(w["b"] + w["d"], beat) <= 0.5 for w in level["walls"]
        )
        wall_free[n["id"]] = clear
        if c["sparseSectionAllowance"] and clear and local_spacing[n["id"]] >= 0.35 and gaps[n["id"]] >= 0.8:
            section = next((s for s in sections if s["startBeat"] <= beat < s["endBeatExclusive"]), None)
            if section and section["nps"] <= 1.7:
                sparse[n["id"]] = c["sparseSectionAllowance"]
    return {
        "controls": c,
        "gaps": gaps,
        "nextGapSeconds": next_gap,
        "bpm": bpm,
        "clock": clock,
        "walls": level["walls"],
        "nearbyBombs": nearby_bombs,
        "njs": njs,
        "approachSeconds": approach,
        "relaxation": relaxation,
        "ramp": ramp,
        "sparseAllowance": sparse,
        "wallFree": wall_free,
        "repairWindows": repair_windows(ns, clock, profile.get("boundedRepairBeats", ())),
        "repairCostScale": 1 + max(0, 0.8 - profile.get("minimumSourceNoteRatio", 0.8)),
    }


def bomb_visibility_cost(n, ctx):
    """Sample note obstruction of bomb centers during the approach."""
    cell = MOTION["cellMeters"]
    half = MOTION["noteWidthMeters"] / 2
    eye = (0, MOTION["eyeHeightMeters"], 0)
    approach = ctx["approachSeconds"]
    hits = 0
    for bomb in ctx["nearbyBombs"][n["id"]]:
        gap = ctx["clock"].gap(n["b"], bomb["b"])
        if abs(gap) >= approach:
            continue
        for step in range(1, 9):
            lead = approach * step / 9
            note_lead = lead - gap
            if not 0 < note_lead < approach:
                continue
            target = (
                (bomb["x"] - 1.5) * cell,
                MOTION["bottomRowMeters"] + bomb["y"] * cell,
                MOTION["hitPlaneMeters"] + ctx["njs"] * lead,
            )
            x = (n["x"] - 1.5) * cell
            y = MOTION["bottomRowMeters"] + n["y"] * cell
            z = MOTION["hitPlaneMeters"] + ctx["njs"] * note_lead
            if ray_box(eye, target, (x - half, y - half, z - half), (x + half, y + half, z + half)):
                hits += 1
    return 0.5 * hits


def crouch_recovery_cue(n, ctx):
    c = ctx["controls"]
    clock = ctx["clock"]
    return any(
        w["y"] >= 2
        and w["w"] >= 3
        and clock.gap(w["b"], w["b"] + w["d"]) >= c["resetCueMinimumCrouchSeconds"]
        and w["b"] <= n["b"]
        and clock.gap(w["b"] + w["d"], n["b"]) <= c["crouchRecoverySeconds"]
        for w in ctx["walls"]
    )


def exclusions(n, ctx):
    c = ctx["controls"]
    reasons = []
    spacing = ctx["gaps"][n["id"]]
    clock = ctx["clock"]
    repair = n.get("domainRepair")
    clearance_repair = repair in ("clearance", "stack_clearance")
    if (
        c["bottomRowDownstrokes"]
        and n["y"] == 1
        and n["d"] in (1, 6, 7)
        and repair not in ("reset_cell", "stack_clearance")
    ):
        reasons.append("middle_row_downstroke")
    if c["bottomRowOuterInward"] and n["y"] == 1 and n["x"] in (0, 3) and n["d"] == (3 if n["x"] == 0 else 2):
        reasons.append("middle_row_outer_inward")
    if (
        c["crouchMiddleRecoverySeconds"]
        and n["y"] == 1
        and any(
            w["y"] >= 2
            and w["w"] >= 3
            and w["b"] <= n["b"]
            and clock.gap(w["b"] + w["d"], n["b"]) < c["crouchMiddleRecoverySeconds"]
            and not (
                clearance_repair
                and w["b"] + w["d"] < n["b"]
                and clock.gap(w["b"], w["b"] + w["d"]) < c["resetCueMinimumCrouchSeconds"]
            )
            for w in ctx["walls"]
        )
    ):
        reasons.append("middle_row_during_crouch_recovery")
    if (
        (c["ownSideOutward"] or n.get("flattenOutward"))
        and n["d"] == outward_direction(n["c"])
        and crossover_depth(n)
    ):
        reasons.append("outward_cut_on_crossed_side")
    if c["sideWallRecoverySeconds"] and side_wall_recovery_gap(n, ctx) < c["sideWallRecoverySeconds"]:
        reasons.append("side_wall_recovery_lane")
    if n["y"] == 2:
        retained_reset_up = repair == "reset_cell" and n["d"] in (0, 4, 5)
        if c["height"] == 0 and not retained_reset_up:
            reasons.append("height_disabled")
        minimum = (
            c["topDotSpacingSeconds"]
            if n["d"] == 8
            else c["topPalmSpacingSeconds"]
            if n["d"] in [1, 6, 7]
            else c["topRowSpacingSeconds"]
        )
        if spacing < minimum / (0.5 + c["height"]) and not retained_reset_up:
            reasons.append("top_row_recovery")
        if any(
            w["y"] >= 2
            and w["w"] >= 3
            and w["b"] <= n["b"]
            and clock.gap(w["b"] + w["d"], n["b"]) <= c["crouchRecoverySeconds"]
            for w in ctx["walls"]
        ):
            reasons.append("top_row_during_crouch_recovery")
    if (
        n["x"] in [1, 2]
        and n["y"] == 1
        and ctx["nextGapSeconds"][n["id"]] <= c["centerVisionWindowSeconds"]
        and not clearance_repair
    ):
        reasons.append("central_readability_window")
    # Colored and bomb heads cannot occupy intersecting spatial/depth volumes.
    # Unlike old relative checks, bad source seeds are never grandfathered.
    sweep = segment(n, extent=1.5) if n["d"] != 8 else None
    cell = MOTION["cellMeters"]
    for bomb in ctx["nearbyBombs"][n["id"]]:
        dt = abs(clock.gap(n["b"], bomb["b"]))
        depth = ctx["njs"] * dt
        separation = math.hypot(n["x"] - bomb["x"], n["y"] - bomb["y"]) * cell
        distance = math.hypot(separation, depth)
        same_head = abs(n["b"] - bomb["b"]) <= 0.0001 and (n["x"], n["y"]) == (bomb["x"], bomb["y"])
        if same_head or distance < c["noteBombHeadClearanceMeters"]:
            reasons.append("note_bomb_head_overlap")
            break
        clearance = c["bombSwingClearanceMeters"]
        if sweep and depth < clearance:
            lateral = point_segment_distance((bomb["x"], bomb["y"]), *sweep) * cell
            # A one-cell separation can be usable when the cut stays parallel to
            # the bomb. The head clearance and any cut moving toward it still apply.
            grid_parallel = n["d"] in (0, 1, 2, 3) and not n.get("a", 0)
            parallel = (
                clearance_repair
                and lateral >= cell - 1e-9
                and (grid_parallel or lateral >= separation - 1e-9)
            )
            if (
                not parallel
                and math.hypot(lateral, depth) < clearance - 1e-9
                and "bomb_swing_clearance" not in reasons
            ):
                reasons.append("bomb_swing_clearance")
    return reasons


def preference(n, original, previous, ctx):
    c = ctx["controls"]
    intent = n.get("intent", {})
    target = intent.get("preferredCell", [original["x"], original["y"]])
    if intent.get("kind") in ("crossover_return", "projected_bomb_return"):
        depth_limit = c.get("crossoverReturnDepth", 2)
        x, y = target
        target = (min(x, 1 + depth_limit) if n["c"] == 0 else max(x, 2 - depth_limit), y)
    protected = intent.get("kind") in (
        "paired_hand_call_response",
        "bomb_return_reset",
        "crossover_return",
        "projected_bomb_return",
    )
    feature_weight = intent.get("positionWeight", 3 if protected else 1)
    position = (
        (abs(n["x"] - target[0]) + 0.8 * abs(n["y"] - target[1])) * c["sourcePositionCost"] * feature_weight
    )
    height = (1 - c["height"]) * 0.8 * (n["y"] == 2)
    if n["y"] == 1 and n["d"] in (1, 6, 7):
        height += c.get("middleRowDownstrokeCost", 0)
        if n["x"] in (0, 3):
            height += c.get("outerMiddleDownstrokeCost", 0)
    if n["y"] == 2 and n["d"] in (1, 6, 7):
        height += c.get("topDownstrokeCost", 0)
        if crossover_depth(n):
            height += c.get("topCrossedDownstrokeCost", 0)
    depth = crossover_depth(n)
    own_outer_lane = n["x"] == n["c"] * 3
    width = (1 - c["width"]) * (5 / 3) if own_outer_lane else 0
    crossover = (1 - c["crossover"]) * 3 * (bool(depth) if protected else depth)
    if not protected and depth == 2 and n["d"] in (1, 6, 7) and ctx["gaps"][n["id"]] <= 0.35:
        crossover += 1.8 if n["d"] in (6, 7) else 0.8
    if not protected and depth == 2:
        crossover += c.get("deepCrossoverCost", 0)
    if not protected and depth == 1 and crossover_depth(original) == 2:
        crossover -= c.get("shallowCrossoverPreference", 0)
    travel = 0 if previous is None else 0.25 * math.hypot(n["x"] - previous["x"], n["y"] - previous["y"])
    scale = ctx["relaxation"][n["id"]]
    sparse_scale = 1 - ctx["sparseAllowance"].get(n["id"], 0)
    return {
        "sourcePosition": position,
        "height": height * scale * sparse_scale,
        "width": width * scale * sparse_scale,
        "crossover": crossover
        * scale
        * (sparse_scale if depth == 1 and crossover_depth(original) == 1 else 1),
        "travel": travel,
        "bombVisibility": bomb_visibility_cost(n, ctx),
    }


def double_comfort_cost(notes, *, windup_weight=0):
    """Prefer roomy doubles and put the leading hand above the trailing hand on lateral sweeps."""
    if len(notes) != 2 or notes[0]["c"] == notes[1]["c"] or any(n["d"] == 8 for n in notes):
        return 0
    left, right = sorted(notes, key=lambda n: n["c"])
    if all(n.get("linkedAnchor") for n in notes):
        return 0
    height = 3 * sum(n["y"] == 2 and not n.get("linkedAnchor") for n in notes)
    if left["d"] == right["d"] and left["d"] in (2, 3) and not any(n.get("a", 0) for n in notes):
        lead, trail = (left, right) if left["d"] == 2 else (right, left)
        return (
            height + 3 * (lead["y"] <= trail["y"]) + 0.65 * abs(lead["x"] - trail["x"])
            + 1.4 * abs(lead["y"] - 1) + 1.4 * trail["y"]
        )

    return height + double_clearance_cost(left, right, windup_weight)


def double_clearance_cost(left, right, windup_weight):
    # The collision screen rejects intersecting envelopes. A wider margin is
    # helpful when either cut travels across the other hand's path.
    first, second = segment(left), segment(right)
    left_motion = first[1][0] - first[0][0]
    right_motion = second[1][0] - second[0][0]
    closing_motion = (right["x"] - left["x"]) * (left_motion - right_motion)
    # Outward cuts may be close during preparation even when they separate
    # after the hit. Include the vertical axis for staggered doubles.
    if windup_weight:
        relative_rise = (first[1][1] - first[0][1]) - (second[1][1] - second[0][1])
        closing_motion += (right["y"] - left["y"]) * relative_rise
    closes_gap = (abs(closing_motion) if windup_weight else closing_motion) > 0.1
    if closes_gap:
        weight = windup_weight or 5
        margin = 1.5 if windup_weight else 1.15
        return weight * max(0, margin - segment_distance(*first, *second))
    return 0


def placement_transition_cost(previous, current, ctx):
    if previous is None or current.get("linkedAnchor"):
        return 0
    gap = ctx["clock"].gap(previous["b"], current["b"])
    if gap <= 0.25 and previous["d"] in (2, 3) and current["y"] > previous["y"]:
        return ctx["controls"].get("quickHorizontalRiseCost", 0)
    return 0
