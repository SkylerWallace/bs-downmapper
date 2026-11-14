"""Resolve the constant note jump settings used by placement and export."""

import math


DEFAULT_NJS = {"Easy": 10, "Normal": 10, "Hard": 12, "Expert": 14, "ExpertPlus": 16}


def hjd(bpm, njs, offset):
    """Return half jump duration in beats using the game's base jump rule."""
    if not all(math.isfinite(value) and value > 0 for value in (bpm, njs)):
        raise ValueError("Positive finite BPM and NJS required.")
    beats = 4
    while beats > 1 and njs * (60 / bpm) * beats > 17.999:
        beats /= 2
    return max(0.25, beats + offset)


def jump_settings(difficulty, bpm, expert_plus_njs):
    """Return NJS, quarter-beat offset, half jump duration, and distance."""
    njs = DEFAULT_NJS[difficulty]
    valid_njs = (
        type(expert_plus_njs) in (int, float) and math.isfinite(expert_plus_njs) and expert_plus_njs >= 10
    )
    if valid_njs:
        if difficulty == "ExpertPlus":
            njs = expert_plus_njs
        elif difficulty in ("Hard", "Expert"):
            shares = {"Hard": 1, "Expert": 2}
            speed = 10 + (expert_plus_njs - 10) / 3 * shares[difficulty]
            njs = math.floor(2 * speed) / 2
    base = hjd(bpm, njs, 0)
    distance_per_quarter = 0.5 * njs * (60 / bpm)
    if not math.isfinite(distance_per_quarter) or distance_per_quarter <= 0:
        raise ValueError("Cannot calculate jump distance for this BPM and NJS.")
    minimum = max(1, math.ceil(20 / distance_per_quarter - 1e-9))
    maximum = math.floor(32 / distance_per_quarter + 1e-9)
    if minimum > maximum:
        raise ValueError("No quarter-beat offset gives a jump distance of 20–32 m for this BPM and NJS.")
    midpoint = 25 if difficulty in ("Expert", "ExpertPlus") else 27
    quarters = min(maximum, max(minimum, round(midpoint / distance_per_quarter)))
    duration = quarters / 4
    return njs, duration - base, duration, quarters * distance_per_quarter


def verified_jump_settings(difficulty, bpm, expert_plus_njs, native):
    """Check a ChroMapper calculation against the CLI policy before using it."""
    reference = jump_settings(difficulty, bpm, expert_plus_njs)
    if not isinstance(native, dict):
        raise ValueError("Invalid ChroMapper jump settings.")
    fields = ("njs", "offset", "halfJumpDuration", "jumpDistance")
    values = tuple(native.get(field) for field in fields)
    if (
        any(type(value) not in (int, float) for value in values)
        or values[:3] != reference[:3]
        or not math.isclose(values[3], reference[3], rel_tol=0, abs_tol=1e-9)
    ):
        raise ValueError("ChroMapper jump settings differ from the generation policy.")
    return values
