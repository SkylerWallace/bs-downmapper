"""Send prepared editor operations to the C# engine host when configured."""

import json
import os
from pathlib import Path
import subprocess


def enabled():
    return bool(os.environ.get("BS_DOWNMAP_NATIVE_ENGINE"))


def model_context(value):
    from beat_clock import BeatClock

    if isinstance(value, BeatClock):
        return {"starts": value.starts, "times": value.times, "rates": value.rates}
    if isinstance(value, Path):
        return str(value)
    if isinstance(value, dict):
        return {key: model_context(item) for key, item in value.items()}
    if isinstance(value, (list, tuple, set, frozenset)):
        return [model_context(item) for item in value]
    return value


def invoke(operation, payload):
    executable = Path(os.environ["BS_DOWNMAP_NATIVE_ENGINE"]).resolve()
    command = ["dotnet", str(executable)] if executable.suffix.lower() == ".dll" else [str(executable)]
    process = subprocess.run(
        [*command, operation],
        input=json.dumps(payload, allow_nan=False),
        encoding="utf-8",
        capture_output=True,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if process.returncode:
        raise ValueError(f"Native {operation} failed: {process.stderr.strip() or process.returncode}")
    return json.loads(process.stdout)


def priority(reference, contexts, seeds, profiles, sections, streams, repeat_matching):
    from analyze_standard_spreads import groups
    from rhythm_inputs import easy_sparse_cues

    tiers = {}
    mandatory = set()
    for tier, (level, protected) in contexts.items():
        locked = set(protected) | level["anchorIds"]
        mandatory.update(locked)
        windows, accents = (
            easy_sparse_cues(level, list(groups(level["notes"]).items())) if tier == "Easy" else ((), ())
        )
        tiers[tier] = {
            "notes": level["notes"],
            "protected": sorted(locked),
            "required": sorted(accents),
            "windows": windows,
            "seed": [note["id"] for note in seeds[tier]],
            "accents": sorted(level.get("linkAccentBeats", ())),
        }
    easy = profiles["Easy"]
    result = invoke("nested-priority", {
        "folder": str(reference["folder"].resolve()),
        "difficulty": reference["meta"].get("difficulty", reference["meta"].get("_difficulty")).replace("Expert+", "ExpertPlus"),
        "model": str(Path(os.environ["BS_DOWNMAP_NATIVE_RANKER"]).resolve()),
        "decoder": str(Path(os.environ["BS_DOWNMAP_NATIVE_DECODER"]).resolve()),
        "tiers": tiers,
        "sections": [[section["startBeat"], section["endBeatExclusive"]] for section in sections],
        "streams": [stream["noteIds"] for stream in streams],
        "endpoints": [point[0] for point in contexts["Easy"][0]["linkedEndpoints"]],
        "easy": {
            "gap": easy["automaticTimingMinimumGapSeconds"],
            "pulse": easy["automaticTimingPulseSeconds"],
            "accents": easy.get("automaticTimingPhraseAccents", False),
            "swaps": easy.get("automaticTimingPhraseSwaps", False),
            "budget": easy.get("automaticTimingPhraseBudget", False),
        },
        "matchRepeats": repeat_matching,
    })
    return result["order"], result["budgets"], mandatory


def reference_priority(reference, repeat_matching):
    result = invoke("reference-priority", {
        "folder": str(reference["folder"].resolve()),
        "difficulty": reference["meta"].get("difficulty", reference["meta"].get("_difficulty")).replace("Expert+", "ExpertPlus"),
        "engine": str(Path(__file__).resolve().parents[1]),
        "model": str(Path(os.environ["BS_DOWNMAP_NATIVE_RANKER"]).resolve()),
        "decoder": str(Path(os.environ["BS_DOWNMAP_NATIVE_DECODER"]).resolve()),
        "matchRepeats": repeat_matching,
    })
    return result["order"], result["budgets"], set(result["mandatory"])


def place(notes, level, sections, profile, policy):
    pairs = {}
    rows = []
    by_id = {note["id"]: note for note in notes}
    for note in notes:
        intent = note.get("intent", {})
        pair = intent.get("pairId") if intent.get("kind") == "paired_hand_call_response" else None
        rows.append(note if pair is None else {**note, "pairIndex": pairs.setdefault(pair, len(pairs))})
    result = invoke("place", {
        "folder": str(level["folder"].resolve()),
        "difficulty": level["meta"].get("difficulty", level["meta"].get("_difficulty")).replace("Expert+", "ExpertPlus"),
        "notes": rows, "source": level["notes"], "walls": level["walls"], "bombs": level["bombs"],
        "endpoints": level.get("linkedEndpoints", ()), "profile": profile,
        "sections": [[section["startBeat"], section["endBeatExclusive"], section["nps"]] for section in sections],
        "njs": policy["njs"], "approach": policy["approachSeconds"],
        "repairCenters": profile.get("boundedRepairBeats", ()),
    })
    if "failure" in result:
        return None, {**result, "notes": [by_id[i] for i in result["ids"]]}
    return [{**by_id[row["id"]], **row} for row in result["notes"]], None
