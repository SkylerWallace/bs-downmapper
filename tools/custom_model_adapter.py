"""Run a legacy Python timing or pattern callback from a native engine request."""

from contextlib import redirect_stdout
import json
from pathlib import Path
import runpy
import sys

from beat_clock import BeatClock


def restore_level(level):
    if level is None:
        return
    if "folder" in level:
        level["folder"] = Path(level["folder"])
    if "anchorIds" in level:
        level["anchorIds"] = set(level["anchorIds"])
    if "linkedEndpoints" in level:
        level["linkedEndpoints"] = [tuple(point) for point in level["linkedEndpoints"]]
    for name in ("clock", "legacyGridClock"):
        if level.get(name) is not None:
            values = level[name]
            clock = BeatClock.__new__(BeatClock)
            clock.starts, clock.times, clock.rates = (values[key] for key in ("starts", "times", "rates"))
            level[name] = clock


def main(entry, kind):
    request = json.load(sys.stdin)
    if kind != request["kind"] or kind not in ("timing", "pattern"):
        raise ValueError("Unexpected custom model kind.")
    context = request["context"]
    for name in ("level", "reference", "foundation", "rhythm"):
        restore_level(context.get(name))
    for name in ("protected_ids", "required_beats"):
        if name in context:
            context[name] = set(context[name])
    name = "select_timings" if kind == "timing" else "seed_patterns"
    with redirect_stdout(sys.stderr):
        callback = runpy.run_path(entry).get(name)
        if not callable(callback):
            raise ValueError(f"Expected callback {name}.")
        result = callback(context, request["settings"])
    if kind == "pattern" and (
        not isinstance(result, dict) or any(type(note_id) is not int for note_id in result)
    ):
        raise ValueError("A pattern model must return integer note IDs as keys.")
    json.dump(result, sys.stdout, allow_nan=False)


if __name__ == "__main__":
    main(*sys.argv[1:])
