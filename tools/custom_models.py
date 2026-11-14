"""Versioned local model packages for the ChroMapper bridge and engine."""

from copy import deepcopy
import json
import math
import os
from pathlib import Path
import re
import runpy
import sys
import native_engine

from analyze_standard_spreads import groups, nearest


def load_models(kind):
    root = Path(os.environ.get("BS_DOWNMAP_MODEL_DIR") or Path(__file__).resolve().parents[1] / "models/custom")
    models = {}
    for path in sorted(root.glob("*/model.json")):
        try:
            model = json.loads(path.read_text(encoding="utf-8-sig"))
            if model["kind"] != kind:
                continue
            model_id = model["id"]
            version = model["apiVersion"]
            extension = ".py" if version == 1 else ".exe" if version == 2 and native_engine.enabled() else None
            if (extension is None or not re.fullmatch(r"custom-[a-z0-9][a-z0-9-]*", model_id)
                    or not isinstance(model["label"], str) or not model["label"].strip()):
                raise ValueError("Expected a supported API version and a custom- model ID")
            if model_id in models:
                raise ValueError(f"Duplicate model ID {model_id}")
            entry = (path.parent / model["entry"]).resolve()
            if path.parent.resolve() not in entry.parents or entry.suffix.lower() != extension or not entry.is_file():
                raise ValueError(f"Entry must be a local {extension} file inside the model package")
            setting_values(model, {})
        except (AttributeError, KeyError, TypeError, ValueError, OSError) as error:
            print(f"Skipping model manifest {path}: {error}", file=sys.stderr)
            continue
        models[model_id] = (model, entry, path.parent.resolve())
    return models


def setting_values(model, overrides):
    definitions = model.get("settings", {})
    if not isinstance(overrides, dict) or set(overrides) - definitions.keys():
        raise ValueError("Unknown model settings")
    values = {}
    for key, spec in definitions.items():
        default = spec["default"]
        value = overrides.get(key, default)
        kind = spec["type"]
        if kind == "boolean":
            valid = type(value) is bool
        elif kind == "choice":
            options = spec["options"]
            valid = (isinstance(options, list) and all(isinstance(option, str) for option in options)
                     and isinstance(value, str) and value in options)
        elif kind == "number":
            minimum, maximum, step = spec["min"], spec["max"], spec.get("step", .01)
            valid = (all(type(n) in (int, float) and math.isfinite(n) for n in (value, minimum, maximum, step))
                     and minimum < maximum and minimum <= value <= maximum and step > 0)
        else:
            valid = False
        if not valid:
            raise ValueError(f"Invalid value for model setting {key}")
        values[key] = value
    return values


def model_callback(kind, model_id, overrides):
    registration = load_models(kind).get(model_id)
    if registration is None:
        raise ValueError(f"Unknown custom {kind} model {model_id}")
    model, entry, package = registration
    values = setting_values(model, overrides)
    if native_engine.enabled():
        def invoke_native(context):
            result = native_engine.invoke("custom-model", {
                "package": str(package), "manifest": model,
                "context": native_engine.model_context(context), "settings": values,
                "python": sys.executable, "adapter": str(Path(__file__).with_name("custom_model_adapter.py").resolve()),
            })
            if kind == "pattern" and isinstance(result, dict):
                return {int(key) if key.isascii() and key.isdecimal() else key: value for key, value in result.items()}
            return result
        return invoke_native
    function = runpy.run_path(str(entry)).get("select_timings" if kind == "timing" else "seed_patterns")
    if not callable(function):
        raise ValueError(f"{model_id} is missing its {kind} callback")

    def invoke(context):
        try:
            return function(deepcopy(context), values.copy())
        except Exception as error:
            raise ValueError(f"{model_id}: {error}") from error

    return invoke


def timing_selection(callback, level, difficulty, protected, required_beats, suggested_count, reference):
    candidates = {note["id"]: note for note in level["notes"]}
    ids = callback({
        "level": level, "difficulty": difficulty, "suggested_count": suggested_count,
        "protected_ids": set(protected), "required_beats": set(required_beats), "reference": reference,
    })
    if not isinstance(ids, (list, tuple)) or not ids or any(type(i) is not int or i not in candidates for i in ids):
        raise ValueError("A timing model must return a nonempty list of existing candidate note IDs")
    selected = set(ids)
    if len(selected) != len(ids):
        raise ValueError("A timing model returned duplicate note IDs")
    selected.update(protected & candidates.keys())
    beats = groups(candidates.values())
    beat_times = sorted(beats)
    for required in required_beats:
        beat = nearest(required, beat_times)
        if beat is None:
            raise ValueError(f"The model's timing source cannot retain required beat {required}")
        if not any(note["id"] in selected for note in beats[beat]):
            selected.add(beats[beat][0]["id"])
    return [note for note_id, note in candidates.items() if note_id in selected]


def pattern_seeds(callback, seeded, foundation, rhythm, difficulty, protected):
    ids = {note["id"] for note in seeded}
    proposals = callback({
        "seeds": seeded, "foundation": foundation, "rhythm": rhythm,
        "difficulty": difficulty, "protected_ids": set(protected),
    })
    if not isinstance(proposals, dict):
        raise ValueError("A pattern model must return a dictionary of note ID to seed overrides")
    bounds = {"x": (0, 3), "y": (0, 2), "d": (0, 8), "a": (-180, 180)}
    for note_id, changes in proposals.items():
        if type(note_id) is not int or note_id not in ids or not isinstance(changes, dict):
            raise ValueError(f"Invalid pattern seed for note ID {note_id}")
        for key, value in changes.items():
            if key not in bounds or type(value) is not int or not bounds[key][0] <= value <= bounds[key][1]:
                raise ValueError(f"Invalid pattern seed field {key} for note ID {note_id}")
    return [
        {**note, **proposals.get(note["id"], {})} if note["id"] not in protected else note for note in seeded
    ]
