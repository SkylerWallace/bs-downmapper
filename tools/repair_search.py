"""Policy and limits for emergency placement search."""

import math

from analyze_standard_spreads import groups


MAX_WINDOW_COST = 24.0
MAX_WINDOW_NOTES = 4
MAX_REPAIR_WINDOWS = 9
BEAM_WIDTH = 6
BRANCH_WIDTH = 6


def side_wall_recovery_gap(note, policy):
    """Time since the last wall in this lane. A nonpositive gap means it is still active."""
    clock = policy["clock"]
    return min(
        (
            clock.gap(wall["b"] + wall["d"], note["b"])
            for wall in policy["walls"]
            if wall["y"] == 0
            and wall["h"] >= 3
            and wall["w"] < 4
            and wall["x"] <= note["x"] < wall["x"] + wall["w"]
            and wall["b"] <= note["b"]
        ),
        default=math.inf,
    )


def repair_windows(notes, clock, centers):
    """Map note IDs to disjoint windows with a bounded lead-in and exit."""
    if not centers:
        return {}
    attacks = list(groups(notes).items())
    windows = {}
    for beat in centers:
        middle = min(range(len(attacks)), key=lambda i: abs(clock.gap(beat, attacks[i][0])))
        for attack, group in attacks[max(0, middle - 7) : middle + 8]:
            if -0.9 <= clock.gap(beat, attack) <= 0.75:
                for note in group:
                    windows.setdefault(note["id"], beat)
    return windows


def relaxation_costs(note, policy, reasons):
    """Price the relaxable subset. Every other rejection remains binding."""
    if note["id"] not in policy.get("repairWindows", {}) or note.get("linkedAnchor"):
        return {}
    weights = {
        "central_readability_window": 3.0,
        "middle_row_downstroke": 5.0,
        "middle_row_outer_inward": 6.0,
    }
    controls = policy["controls"]
    if "side_wall_recovery_lane" in reasons:
        preferred = controls["sideWallRecoverySeconds"]
        actual = side_wall_recovery_gap(note, policy)
        if max(0.30, preferred - 0.20) <= actual < preferred:
            weights["side_wall_recovery_lane"] = 6 + 10 * (preferred - actual)
    if "top_row_recovery" in reasons:
        direction = note["d"]
        spacing = (
            controls["topDotSpacingSeconds"]
            if direction == 8
            else (
                controls["topPalmSpacingSeconds"]
                if direction in (1, 6, 7)
                else controls["topRowSpacingSeconds"]
            )
        )
        required = spacing / (0.5 + controls["height"])
        actual = policy["gaps"][note["id"]]
        # The target height preference changes the desired recovery, but it
        # should not change the amount of real time a repair may borrow.
        if actual >= max(0.5, required - 0.35):
            weights["top_row_recovery"] = 6 + 8 * max(0.0, 1 - actual / required) if required else 6
    scale = policy["repairCostScale"]
    return {reason: weights[reason] * scale for reason in reasons if reason in weights}


def within_window_budget(cost, notes):
    return cost <= MAX_WINDOW_COST + 1e-9 and notes <= MAX_WINDOW_NOTES and math.isfinite(cost)
