"""Run the shared downmapper and publish a frozen candidate."""

import argparse
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from uuid import uuid4

from analyze_standard_spreads import groups, load, nearest, write
from audio_onsets import read_onset_scores
from bookmark_files import copy_bookmarks
from beatmap_io import (
    metadata,
    standard_entries,
    DifficultyNotFound,
    read_level,
    export,
    sha,
    linked_anchors,
    linked_endpoints,
    linked_objects,
)
from downmap import section_plan
from custom_models import load_models, model_callback, timing_selection
from mapping_intent import detect_features
import native_engine
from nested_priority import (
    MODEL_ID as NESTED_MODEL, fraction_budget, match_repeat_priorities, priority_order, select_prefix,
)
from timing_link_scopes import editor_timing_links
from timing_sections import chart_timing_sections
from rhythm_inputs import (
    timeline,
    edited_notes,
    best_guess_notes,
    fit_easy_note_budget,
    merge_nearby_singles,
    apply_merges,
    refine_easy_pulse,
    simplify_easy_links,
    timing_link_context,
    retain_timing_links,
)

TARGETS = ("Expert", "Hard", "Normal", "Easy")
DIFFICULTIES = ("ExpertPlus", *TARGETS)
ENGINE = Path(__file__).resolve().parent.parent
LOCAL_PULSE_MODE = "rank-motifs-pulse-budget"


def profile_for(difficulty, override_path):
    profile = load(ENGINE / "profiles" / f"{difficulty.lower()}.json")
    if override_path is None:
        return profile
    edits = load(override_path)
    bounds = {
        "height": (0, 1),
        "width": (0, 1),
        "crossover": (0, 1),
        "diagonalPreference": (0, 10),
        "parityAdherence": (0, 1),
        "rampAmount": (0, 1),
    }
    for key, value in edits.items():
        if key not in bounds or isinstance(value, bool) or not isinstance(value, (int, float)):
            raise ValueError(f"Unsupported generator setting {key}")
        low, high = bounds[key]
        if not math.isfinite(value) or not low <= value <= high:
            raise ValueError(f"{key} must be between {low} and {high}")
        if key in ("height", "width", "crossover"):
            profile["geometry"][key] = value
        elif key == "parityAdherence":
            if "drumming" not in profile:
                raise ValueError("Drumming parity is available on Normal and Easy")
            profile["drumming"][key] = value
        elif key == "rampAmount":
            profile["difficultyRamp"].update(enabled=value > 0, amount=value)
        else:
            profile[key] = value
    return profile


def require_chart(folder, difficulty, role):
    info = load(folder / "Info.dat")
    try:
        metadata(info, difficulty)
    except DifficultyNotFound as error:
        hint = (
            " Select an available authored rhythm difficulty, or choose a folder with the authored chart."
            if role == "Rhythm"
            else ""
        )
        raise ValueError(f"{role} folder: {error}{hint}") from error
    return info


def run_step(command, cwd, log_path):
    result = subprocess.run(command, cwd=cwd, capture_output=True, encoding="utf-8", errors="replace")
    log_path.write_text(result.stdout + result.stderr, encoding="utf-8")
    print(result.stdout, end="", flush=True)
    print(result.stderr, end="", file=sys.stderr, flush=True)
    if result.returncode:
        reasons = re.findall(
            r"^(?:Error|ValueError|AssertionError|FileNotFoundError|KeyError|PlacementDeadEnd|DirectionDeadEnd): (.+)$",
            result.stderr,
            re.MULTILINE,
        )
        raise ValueError(reasons[-1] if reasons else f"Downmapper {command[2]} failed. See the run log.")


def checked_song(folder, difficulty, expected_info, expected_target):
    info_path = folder / "Info.dat"
    if sha(info_path) != expected_info:
        raise ValueError("Song Info changed. Open BS Downmapper again before generating.")
    info = load(info_path)
    entry = standard_entries(info).get(difficulty)
    filename_key = "beatmapDataFilename" if "difficultyBeatmaps" in info else "_beatmapFilename"
    filename = entry[filename_key] if entry is not None else None
    if filename and (Path(filename).name != filename or filename.casefold() == "info.dat"):
        raise ValueError("The existing difficulty has an invalid filename.")
    target = folder / filename if filename else None
    actual = sha(target) if target is not None and target.is_file() else "missing"
    if actual != expected_target:
        raise ValueError("The target difficulty changed. Open BS Downmapper again before generating.")
    return info, entry, filename_key


def chart_beats(raw):
    if "colorNotes" in raw:
        return list(groups({"b": n.get("b", 0)} for n in raw["colorNotes"]))
    return list(groups({"b": n["_time"]} for n in raw.get("_notes", ()) if n["_type"] in (0, 1)))


def saved_standard_timings(folder, *, exclude=None):
    info = load(folder / "Info.dat")
    timings = {}
    for difficulty, entry in standard_entries(info).items():
        if difficulty == exclude or difficulty not in DIFFICULTIES:
            continue
        name = entry.get("beatmapDataFilename", entry.get("_beatmapFilename"))
        if not name or Path(name).name != name:
            raise ValueError(f"Standard {difficulty} has an invalid filename.")
        chart = folder / name
        if chart.is_file():
            beats = chart_beats(load(chart))
            if beats:
                timings[difficulty] = beats
    return timings


def higher_timings(saved, difficulty):
    return {rank: saved[rank] for rank in DIFFICULTIES[: DIFFICULTIES.index(difficulty)] if rank in saved}


def timing_conflicts(saved, difficulty, proposed):
    """Compare distinct colored hit timings in both directions through the spread."""
    above = higher_timings(saved, difficulty)
    below = (rank for rank in DIFFICULTIES[DIFFICULTIES.index(difficulty) + 1 :] if rank in saved)
    result = []
    for rank, beats in above.items():
        missing = [beat for beat in proposed if nearest(beat, beats) is None]
        if missing:
            result.append(dict(difficulty=rank, higher=True, beats=missing))
    for rank in below:
        missing = [beat for beat in saved[rank] if nearest(beat, proposed) is None]
        if missing:
            result.append(dict(difficulty=rank, higher=False, beats=missing))
    return result


def require_nested_timings(conflicts):
    if conflicts:
        details = []
        for conflict in conflicts:
            rank = conflict["difficulty"].replace("ExpertPlus", "Expert+")
            beats = conflict["beats"]
            examples = ", ".join(str(beat) for beat in beats[:3])
            direction = f"missing from {rank}" if conflict["higher"] else f"required by {rank}"
            details.append(f"{len(beats)} {direction} (beats {examples})")
        print("TIMING_CONFLICTS_JSON: " + json.dumps(conflicts), file=sys.stderr)
        raise ValueError("Timing conflicts. " + ". ".join(details) + ". Review the rhythm before saving.")


def restrict_timing_source(level, higher):
    """Keep eligible note IDs and release links attached to excluded notes."""
    if not higher:
        return level
    notes = [
        n for n in level["notes"] if all(nearest(n["b"], beats) is not None for beats in higher.values())
    ]
    if not notes:
        raise ValueError("The higher Standard difficulties have no common hit timings.")
    if len(notes) == len(level["notes"]):
        return level
    removed = set(n["id"] for n in level["notes"]) - {n["id"] for n in notes}
    discarded = {
        id(obj)
        for _, obj, points in linked_objects(level["raw"])
        if removed.intersection(linked_anchors(level["notes"], points))
    }
    raw = {**level["raw"]}
    for key in {key for key, _, _ in linked_objects(raw)}:
        raw[key] = [obj for obj in raw[key] if id(obj) not in discarded]
    endpoints = list(linked_endpoints(raw))
    return {
        **level,
        "raw": raw,
        "notes": notes,
        "linkedEndpoints": endpoints,
        "anchorIds": linked_anchors(notes, endpoints),
    }


def install_difficulty(folder, reports, difficulty, expected_info, expected_target, *, pending=()):
    """Merge the frozen Standard difficulty and retain a recovery copy of replaced files."""
    lock = folder / ".bs-downmap.lock"
    try:
        lock.mkdir()
    except FileExistsError as error:
        raise ValueError("This song is locked by another BS Downmapper update.") from error
    try:
        info, old, filename_key = checked_song(folder, difficulty, expected_info, expected_target)
        candidate = reports / "candidate"
        generated = metadata(load(candidate / "Info.dat"), difficulty)
        chart = candidate / generated[filename_key]
        frozen = load(reports / "frozen.json")
        if sha(chart) != frozen["candidateSha256"]:
            raise ValueError("The generated difficulty changed after validation.")

        if "difficultyBeatmaps" in info:
            entries = all_entries = info["difficultyBeatmaps"]
            reserved = [item.get("lightshowDataFilename") for item in all_entries]
            reserved += [
                info.get("coverImageFilename"),
                info.get("songPreviewFilename"),
                info["audio"].get("songFilename"),
                info["audio"].get("audioDataFilename"),
            ]
        else:
            sets = info["_difficultyBeatmapSets"]
            entries = next(
                item["_difficultyBeatmaps"]
                for item in sets
                if item["_beatmapCharacteristicName"] == "Standard"
            )
            all_entries = [entry for item in sets for entry in item["_difficultyBeatmaps"]]
            reserved = [info.get("_songFilename"), info.get("_coverImageFilename")]
        reserved += [entry.get(filename_key) for entry in all_entries if entry is not old]
        reserved = {name.casefold() for name in reserved if name}

        filename = old[filename_key] if old is not None else None
        if not filename or filename.casefold() in reserved:
            filename = chart.name
            if filename.casefold() in reserved or (folder / filename).exists():
                filename = f"{chart.stem}.downmap-{uuid4().hex[:10]}.dat"
        target = folder / filename
        generated[filename_key] = filename
        if old is None:
            entries.append(generated)
        else:
            entries[entries.index(old)] = generated

        info_path = folder / "Info.dat"
        backup = reports / "previous"
        backup.mkdir()
        shutil.copy2(info_path, backup / "Info.dat")
        replacing = target.is_file()
        if replacing:
            shutil.copy2(target, backup / filename)
        with tempfile.TemporaryDirectory(prefix=".bs-downmap-", dir=folder) as temporary:
            stage = Path(temporary)
            new_chart, new_info = stage / "chart.dat", stage / "Info.dat"
            shutil.copyfile(chart, new_chart)
            write(new_info, info)
            checked_song(folder, difficulty, expected_info, expected_target)
            saved = saved_standard_timings(folder, exclude=difficulty)
            for rank in pending:
                saved.pop(rank, None)
            require_nested_timings(timing_conflicts(saved, difficulty, chart_beats(load(new_chart))))
            os.replace(new_chart, target)
            try:
                os.replace(new_info, info_path)
            except OSError:
                if replacing:
                    shutil.copy2(backup / filename, new_chart)
                    os.replace(new_chart, target)
                else:
                    target.unlink()
                raise
        return target
    finally:
        lock.rmdir()


def protected_reset_notes(level):
    _, features = detect_features(level["notes"], level["bombs"], level["bpm"], level["clock"])
    return {
        i for f in features if f["kind"] in ("bomb_return_reset", "reset_exit") for i in f["noteIds"]
    }


def automatic_timing_context(level, profile, *, protected=None):
    if protected is None:
        protected = protected_reset_notes(level)
    if "automaticTimingMinimumGapSeconds" in profile:
        level = simplify_easy_links(level, profile["automaticTimingMinimumGapSeconds"], protected)
    return timing_link_context(level, protected, maximum_reach=profile.get("maximumLinkedReach")), protected


def prepare_automatic_source(level, higher, profile, *, stable_reference=False):
    restricted = restrict_timing_source(level, higher)
    protected = None
    if stable_reference:
        eligible = {note["id"] for note in restricted["notes"]}
        protected = protected_reset_notes(level) & eligible
    return automatic_timing_context(restricted, profile, protected=protected)


def automatic_timings(
    level,
    profile,
    protected=None,
    *,
    onset_cache=None,
    onset_mode="mix",
    timing_model=None,
    budget_reference=None,
    easy_note_count=None,
    difficulty=None,
    rhythm_links=None,
    required_beats=(),
    target_note_fraction=None,
    priority_cache=None,
    custom_timing=None,
):
    if protected is None:
        level, protected = automatic_timing_context(level, profile)
    if custom_timing is not None:
        baseline, _ = automatic_timings(level, profile, protected, budget_reference=budget_reference)
        notes = timing_selection(
            custom_timing, level, difficulty, protected, required_beats,
            easy_note_count if easy_note_count is not None else len(baseline), budget_reference,
        )
        if easy_note_count is not None and len(notes) != easy_note_count:
            raise ValueError(f"Easy budget of {easy_note_count} notes cannot fit; selected {len(notes)}.")
        return notes, []
    if timing_model == NESTED_MODEL:
        if budget_reference is None or difficulty not in TARGETS:
            raise ValueError("The nested timing model needs a saved higher Standard chart.")
        if priority_cache is None:
            priority_cache = {}
        repeat_matching = rhythm_links is not False
        key = budget_reference["folder"], budget_reference["filename"], repeat_matching
        if key not in priority_cache:
            priority_cache[key] = nested_reference_plan(budget_reference, repeat_matching=repeat_matching)
        order, budgets, reference_protected = priority_cache[key]
        budget = budgets[difficulty]
        if target_note_fraction is not None:
            budget = fraction_budget(budget_reference, target_note_fraction)
        if easy_note_count is not None:
            budget = easy_note_count
        source_is_reference = level["filename"] == budget_reference["filename"]
        mandatory = set(protected) | level["anchorIds"]
        if source_is_reference:
            mandatory.update(reference_protected.intersection(note["id"] for note in level["notes"]))
        notes = select_prefix(
            budget_reference, order, level["notes"], budget, mandatory, required_beats,
            source_is_reference=source_is_reference,
        )
        if easy_note_count is not None and len(notes) != easy_note_count:
            raise ValueError(f"Easy budget of {easy_note_count} notes cannot fit; selected {len(notes)}.")
        return notes, []
    sections, streams = section_plan(level["notes"], level["bpm"], profile, level["clock"])
    if timing_model is not None:
        raise ValueError(f"Unsupported timing model: {timing_model}")
    if onset_cache is not None:
        recurring_figures = onset_mode in ("rank-motifs-figures-budget", LOCAL_PULSE_MODE)
        phrase_search = recurring_figures or onset_mode == "rank-motifs-structured-budget"
        motifs = phrase_search or onset_mode in ("rank-motifs", "rank-motifs-budget")
        boosted = motifs or onset_mode in ("rank-boosted", "rank-boosted-budget")
        ranked = onset_mode == "rank" or boosted
        mix_mode = "mix" if ranked else onset_mode
        level = {**level, "onsetScores": read_onset_scores(level, onset_cache, mix_mode)}
        if ranked:
            score_key = "rank-motifs" if motifs else onset_mode.removesuffix("-budget")
            level["rankScores"] = read_onset_scores(level, onset_cache, score_key)
            level["boostedRank"] = boosted
            level["motifRank"] = motifs
            level["rankedPhraseBudget"] = onset_mode.endswith("-budget")
            level["rankedPhraseSearch"] = phrase_search
            level["rankedRecurringFigures"] = recurring_figures
            if motifs and level["rankedPhraseBudget"] and "rank-budget" in load(onset_cache)["scores"]:
                level["budgetRankScores"] = read_onset_scores(level, onset_cache, "rank-budget")
    retained = best_guess_notes(
        level, sections, streams, profile, protected, budget_reference=budget_reference
    )
    if budget_reference is not None and "automaticTimingPulseSeconds" in profile:
        reference, reference_protected = automatic_timing_context(budget_reference, profile)
        reference_sections, reference_streams = section_plan(
            reference["notes"], reference["bpm"], profile, reference["clock"]
        )
        budget = len(
            best_guess_notes(reference, reference_sections, reference_streams, profile, reference_protected)
        )
        retained = fit_easy_note_budget(
            level,
            retained,
            budget,
            protected,
            profile["automaticTimingMinimumGapSeconds"],
            profile["automaticTimingPulseSeconds"],
        )
    merges = merge_nearby_singles(level, retained, sections, streams, protected)
    if (
        onset_mode == LOCAL_PULSE_MODE
        and "automaticTimingPulseSeconds" in profile
        and (timing_model is not None or onset_cache is not None)
    ):
        retained = refine_easy_pulse(
            level,
            retained,
            merges,
            protected,
            profile["automaticTimingMinimumGapSeconds"],
            profile["automaticTimingPulseSeconds"],
        )
    if easy_note_count is not None and len(retained) != easy_note_count:
        raise ValueError(f"Easy budget of {easy_note_count} notes cannot fit; selected {len(retained)}.")
    return retained, merges


def nested_reference_plan(reference, *, score_reference=None, repeat_matching=True):
    """Score each tier on the same source and freeze one budget-independent priority order."""
    if score_reference is None and native_engine.enabled() and os.environ.get("BS_DOWNMAP_NATIVE_PREPARATION") == "1":
        return native_engine.reference_priority(reference, repeat_matching)
    try:
        from shared_timing import easy_draft, select_shared_timings
        from shared_timing_model import MODEL_ID, predict, reference_features
    except ImportError as error:
        raise ValueError("Install the nested ranker dependencies from requirements.txt.") from error

    profiles = {tier: profile_for(tier, None) for tier in reversed(TARGETS)}
    contexts = {tier: automatic_timing_context(reference, profile) for tier, profile in profiles.items()}
    seeds = {
        tier: automatic_timings(level, profiles[tier], locked, budget_reference=reference)[0]
        for tier, (level, locked) in contexts.items()
    }
    easy, protected = contexts["Easy"]
    easy_profile = profiles["Easy"]
    sections, streams = section_plan(easy["notes"], easy["bpm"], easy_profile, easy["clock"])
    if score_reference is None and native_engine.enabled():
        return native_engine.priority(reference, contexts, seeds, profiles, sections, streams, repeat_matching)
    beats, mix, features, _ = reference_features(easy, protected, sections, streams, easy_profile)
    fractions = {tier: len(seed) / len(reference["notes"]) for tier, seed in seeds.items()}
    scores = (
        predict(ENGINE / "models" / "shared" / f"{MODEL_ID}.joblib", beats, features, fractions)
        if score_reference is None else score_reference(beats, features, fractions)
    )
    draft = easy_draft(easy, easy_profile, protected, sections, streams, scores["Easy"], mix)
    plans = {
        tier: select_shared_timings(level, seeds[tier], locked, tier, scores, mix, draft, beats)
        for tier, (level, locked) in contexts.items()
    }
    mandatory = {i for level, locked in contexts.values() for i in set(locked) | level["anchorIds"]}
    order, budgets = priority_order(reference, plans, scores, mandatory)
    if repeat_matching:
        timings = groups(reference["notes"])
        repeats = editor_timing_links(reference, timings, timeline(reference)["endBeat"], include_local=False)
        order = match_repeat_priorities(
            reference, order, budgets, scores, contexts, mandatory,
            (list(timings), repeats["sections"], repeats["markerLinks"]),
        )
    return order, budgets, mandatory


def generate_spread(source, plan):
    """Build checked targets on a copy and publish only after the full spread validates."""
    entries = plan["targets"]
    ranks = [entry["difficulty"] for entry in entries]
    if not ranks or ranks != [rank for rank in TARGETS if rank in ranks]:
        raise ValueError("Choose distinct Standard targets in highest-to-lowest order.")
    repeat_modes = {
        entry.get("rhythmLinks", True)
        for entry in entries
        if entry["mode"] == "best-guess" and "edits" not in entry
        and entry.get("model", NESTED_MODEL) == NESTED_MODEL
    }
    if len(repeat_modes) > 1:
        raise ValueError("Use one repeat-matching setting for all Nested timing ranker targets in a batch.")
    expected_info = plan["infoSha256"]
    expected = {entry["difficulty"]: entry["targetSha256"] for entry in entries}
    foundations = [entry.get("foundation", plan.get("foundation")) for entry in entries]
    if any(foundation not in DIFFICULTIES for foundation in foundations):
        raise ValueError("Choose a Standard pattern source for each target.")
    for rank in ranks:
        checked_song(source, rank, expected_info, expected[rank])
    root = Path(os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()) / "bs-downmap" / "runs"
    run = root / f"spread-{uuid4().hex[:10]}"
    run.mkdir(parents=True)
    with tempfile.TemporaryDirectory(prefix="work-", dir=run) as directory:
        work = Path(directory)
        staged = work / "song"
        staged.mkdir()
        for asset in source.iterdir():
            if asset.is_file():
                shutil.copy2(asset, staged / asset.name)
        copy_bookmarks(source, staged)
        priority_cache = {}
        for index, entry in enumerate(entries):
            rank = ranks[index]
            print(f"Preparing Standard {rank} ({index + 1} of {len(ranks)})", flush=True)
            args = [
                "--source", str(staged), "--source-difficulty", foundations[index], "--difficulty", rank,
                "--rhythm-mode", entry["mode"], "--in-place", "--expected-info-sha256", sha(staged / "Info.dat"),
                "--expected-target-sha256", expected[rank],
            ]
            for key, flag in (
                ("rhythm", "--rhythm-difficulty"), ("model", "--timing-model"), ("patternModel", "--pattern-model"),
            ):
                if entry.get(key):
                    args.extend((flag, entry[key]))
            if entry.get("targetNoteFraction") is not None:
                args.extend(("--target-note-fraction", str(entry["targetNoteFraction"])))
            if "rhythmLinks" in entry:
                args.append("--rhythm-links" if entry["rhythmLinks"] else "--no-rhythm-links")
            for key, flag in (
                ("edits", "--timing-edits"), ("settings", "--settings"),
                ("timingModelSettings", "--timing-model-settings"), ("patternModelSettings", "--pattern-model-settings"),
                ("jumpSettings", "--jump-settings"),
            ):
                if entry.get(key):
                    file = work / f"{rank}-{key}.json"
                    write(file, entry[key])
                    args.extend((flag, str(file)))
            main(args, batch=(source, ranks[:index], ranks[index + 1 :]), priority_cache=priority_cache)
        publish_spread(source, staged, run, expected_info, expected)
    print(f"Updated Standard {', '.join(ranks)}", flush=True)


def publish_spread(source, staged, run, expected_info, expected):
    if native_engine.enabled():
        native_engine.invoke("publish", {
            "source": str(source.resolve()), "staged": str(staged.resolve()), "reports": str(run.resolve()),
            "info": expected_info, "targets": expected,
        })
        return
    lock = source / ".bs-downmap.lock"
    try:
        lock.mkdir()
    except FileExistsError as error:
        raise ValueError("This song is locked by another BS Downmapper update.") from error
    try:
        final_info = load(staged / "Info.dat")
        key = "beatmapDataFilename" if "difficultyBeatmaps" in final_info else "_beatmapFilename"
        original_entries = standard_entries(load(source / "Info.dat"))
        final_entries = standard_entries(final_info)
        saved = saved_standard_timings(source)
        names = []
        for rank, fingerprint in expected.items():
            checked_song(source, rank, expected_info, fingerprint)
            name = final_entries[rank][key]
            if name != original_entries.get(rank, {}).get(key) and (source / name).exists():
                raise ValueError(f"A new file now uses the generated Standard {rank} filename.")
            names.append(name)
            saved[rank] = chart_beats(load(staged / name))
        for rank in expected:
            require_nested_timings(timing_conflicts(saved, rank, saved[rank]))
        backup = run / "previous"
        backup.mkdir()
        names.append("Info.dat")
        with tempfile.TemporaryDirectory(prefix=".bs-downmap-", dir=source) as directory:
            ready = Path(directory)
            for name in names:
                if (source / name).is_file():
                    shutil.copy2(source / name, backup / name)
                shutil.copy2(staged / name, ready / name)
            for rank, fingerprint in expected.items():
                checked_song(source, rank, expected_info, fingerprint)
            replaced = []
            try:
                for name in names:
                    os.replace(ready / name, source / name)
                    replaced.append(name)
            except OSError:
                for name in reversed(replaced):
                    if (backup / name).is_file():
                        shutil.copy2(backup / name, ready / name)
                        os.replace(ready / name, source / name)
                    else:
                        (source / name).unlink()
                raise
    finally:
        lock.rmdir()


def main(argv=None, *, batch=None, priority_cache=None):
    parser = argparse.ArgumentParser(description=__doc__)
    custom_timings = load_models("timing")
    custom_patterns = load_models("pattern")
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--batch-plan", type=Path, help="Build and save checked Standard difficulties together.")
    parser.add_argument(
        "--rhythm-links", action=argparse.BooleanOptionalAction, default=None,
        help="Prefer repeated rhythms. Enabled by default for the Nested timing ranker.",
    )
    parser.add_argument("--source-difficulty", choices=DIFFICULTIES, default="ExpertPlus")
    parser.add_argument("--rhythm-source", type=Path)
    parser.add_argument("--difficulty", choices=TARGETS)
    parser.add_argument("--rhythm-difficulty", choices=DIFFICULTIES)
    parser.add_argument("--rhythm-mode", choices=("authored", "best-guess", "manual"), default="authored")
    parser.add_argument("--timing-edits", type=Path)
    parser.add_argument("--settings", type=Path)
    parser.add_argument("--jump-settings", type=Path, help=argparse.SUPPRESS)
    parser.add_argument(
        "--easy-note-count", type=int, help="Lock the Easy note budget for a controlled automatic comparison."
    )
    parser.add_argument("--target-note-fraction", type=float, help="Approximate reference note fraction for the nested timing model.")
    parser.add_argument("--onset-cache", type=Path, help="Precomputed timing weights for automatic Easy")
    parser.add_argument(
        "--timing-model",
        choices=[NESTED_MODEL] + list(custom_timings),
        help=f"Automatic timing model (default: {NESTED_MODEL}).",
    )
    parser.add_argument("--timing-model-settings", type=Path)
    parser.add_argument("--pattern-model", choices=list(custom_patterns))
    parser.add_argument("--pattern-model-settings", type=Path)
    parser.add_argument(
        "--onset-mode",
        choices=(
            "chart",
            "mix",
            "stems",
            "rank",
            "rank-boosted",
            "rank-boosted-budget",
            "rank-motifs",
            "rank-motifs-budget",
            "rank-motifs-structured-budget",
            "rank-motifs-figures-budget",
            LOCAL_PULSE_MODE,
        ),
    )
    output = parser.add_mutually_exclusive_group()
    output.add_argument("--output-root", type=Path)
    output.add_argument("--in-place", action="store_true")
    parser.add_argument("--expected-info-sha256")
    parser.add_argument("--expected-target-sha256")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--preview", action="store_true")
    action.add_argument(
        "--check-timings", action="store_true", help="Report Standard timing conflicts without writing files."
    )
    parser.add_argument(
        "--independent-timings",
        action="store_true",
        help="Reproduce single-difficulty benchmarks outside the song folder.",
    )
    args = parser.parse_args(argv)
    if args.batch_plan:
        generate_spread(args.source.resolve(strict=True), load(args.batch_plan))
        return
    if args.independent_timings and (args.in_place or args.check_timings):
        parser.error("Independent timings are available for standalone benchmark output only.")
    if args.check_timings and (args.in_place or args.output_root):
        parser.error("--check-timings does not write output.")
    automatic = args.difficulty is not None and args.rhythm_mode == "best-guess" and args.timing_edits is None
    if args.easy_note_count is not None and (
        args.easy_note_count < 1
        or not automatic
        or args.difficulty != "Easy"
        or args.independent_timings
        or args.onset_cache
        or args.onset_mode
    ):
        parser.error("A fixed Easy note count requires nested automatic Easy with a timing model.")
    nested_context = args.rhythm_mode == "best-guess" and args.timing_model == NESTED_MODEL
    if args.timing_model and not (automatic or nested_context):
        parser.error("Timing models apply to automatic timing selection.")
    if (args.onset_cache or args.onset_mode) and not (automatic and args.difficulty == "Easy"):
        parser.error("Experimental onset modes apply to automatic Easy timing selection.")
    if args.timing_model and (args.onset_cache or args.onset_mode):
        parser.error("Use either a timing model or an experimental onset mode.")
    if args.onset_mode and (args.onset_mode == "chart") == (args.onset_cache is not None):
        parser.error("The chart mode uses no onset cache. Other onset modes require one.")
    if automatic and args.onset_cache is None and args.onset_mode is None and not args.independent_timings:
        args.timing_model = args.timing_model or NESTED_MODEL
    is_custom_timing = args.timing_model in custom_timings
    if args.timing_model_settings and not is_custom_timing:
        parser.error("Timing model settings require a custom timing model.")
    if args.pattern_model_settings and not args.pattern_model:
        parser.error("Pattern model settings require a custom pattern model.")
    custom_timing = None
    if is_custom_timing:
        settings = load(args.timing_model_settings) if args.timing_model_settings else {}
        custom_timing = model_callback("timing", args.timing_model, settings)
    if args.timing_model == NESTED_MODEL and args.independent_timings:
        parser.error("The nested timing model requires Standard difficulty nesting.")
    if args.target_note_fraction is not None and (
        not automatic or args.timing_model != NESTED_MODEL or not 0 < args.target_note_fraction <= 1
        or args.easy_note_count is not None
    ):
        parser.error("--target-note-fraction requires the nested timing model and a fraction above 0 up to 1.")
    if args.rhythm_links is not None and not (
        automatic and args.timing_model == NESTED_MODEL
    ):
        parser.error("Repeat matching requires automatic timing selection with the Nested or Shared ranker.")
    if args.rhythm_links is None:
        args.rhythm_links = automatic and args.timing_model == NESTED_MODEL
    timing_options = dict(
        onset_cache=args.onset_cache,
        onset_mode=args.onset_mode or "mix",
        timing_model=args.timing_model,
        easy_note_count=args.easy_note_count,
        difficulty=args.difficulty,
        rhythm_links=args.rhythm_links,
        target_note_fraction=args.target_note_fraction,
        priority_cache=priority_cache,
        custom_timing=custom_timing,
    )
    source = args.source.resolve(strict=True)
    pattern_folder, completed, pending = batch if batch is not None else (source, (), ())
    info = require_chart(pattern_folder, args.source_difficulty, "Pattern")
    profile = profile_for(args.difficulty, args.settings) if args.difficulty else None
    saved = (
        saved_standard_timings(source, exclude=args.difficulty)
        if args.difficulty and not args.independent_timings
        else {}
    )
    for rank in pending:
        saved.pop(rank, None)
    higher = higher_timings(saved, args.difficulty) if args.difficulty else {}
    if args.rhythm_links or args.timing_model == NESTED_MODEL or (is_custom_timing and not args.independent_timings):
        below = DIFFICULTIES[DIFFICULTIES.index(args.difficulty) + 1 :]
        timing_options["required_beats"] = {beat for rank in below for beat in saved.get(rank, ())}
    parents = [higher[rank] for rank in completed]

    def survives_parents(beat):
        return all(nearest(beat, parent) is not None for parent in parents)

    reviewed_source = pattern_folder if args.timing_edits is not None else source
    if args.rhythm_mode == "authored" and args.rhythm_source:
        reviewed_source = args.rhythm_source
    review_higher = higher
    if reviewed_source != source and args.rhythm_mode != "authored":
        review_higher = higher_timings(saved_standard_timings(reviewed_source), args.difficulty)
    rhythm_difficulty = args.rhythm_difficulty or args.difficulty
    timing_level = None
    if args.rhythm_mode != "authored":
        if args.difficulty is None:
            parser.error("Choose a target difficulty before preparing timings.")
        if args.independent_timings:
            rhythm_difficulty = args.rhythm_difficulty or args.source_difficulty
        else:
            if not review_higher:
                raise ValueError(
                    f"Standard {args.difficulty} needs a saved higher difficulty with hit timings."
                )
            default_source = next(iter(review_higher)) if args.timing_model == NESTED_MODEL else next(reversed(review_higher))
            rhythm_difficulty = args.rhythm_difficulty or default_source
            if rhythm_difficulty not in review_higher:
                raise ValueError(f"Choose a saved Standard timing source above {args.difficulty}.")
        rhythm_level = read_level(reviewed_source, rhythm_difficulty)
        if args.rhythm_mode == "best-guess":
            stable_reference = args.timing_model == NESTED_MODEL and rhythm_difficulty == next(iter(review_higher))
            timing_level, protected = prepare_automatic_source(
                rhythm_level, review_higher, profile, stable_reference=stable_reference
            )
            if higher and args.timing_edits is None:
                # Expert+ is first in rank order. Maps without it use their
                # highest available Standard chart as the budget reference.
                timing_options["budget_reference"] = read_level(source, next(iter(higher)))
        else:
            timing_level = restrict_timing_source(rhythm_level, review_higher)
            timing_level = timing_link_context(
                timing_level, protected_reset_notes(timing_level), maximum_reach=profile.get("maximumLinkedReach")
            )
    elif args.preview or args.timing_edits is not None:
        rhythm_difficulty = rhythm_difficulty or args.source_difficulty
        rhythm_level = read_level(reviewed_source, rhythm_difficulty)
        timing_level = timing_link_context(
            rhythm_level, protected_reset_notes(rhythm_level), maximum_reach=(profile or {}).get("maximumLinkedReach")
        )
    if args.preview:
        level = timing_level
        pruning = {}
        if args.rhythm_mode == "best-guess":
            notes, pruning["merges"] = automatic_timings(level, profile, protected, **timing_options)
            retained = {n["id"] for n in notes}
            pruning["removedNoteIds"] = [n["id"] for n in level["notes"] if n["id"] not in retained]
            pruning["simplifiedLinkCount"] = retain_timing_links(
                level, apply_merges(level, notes, pruning["merges"])
            ).get("simplifiedLinkCount", 0)
        elif level.get("simplifiedLinkCount"):
            pruning["simplifiedLinkCount"] = level["simplifiedLinkCount"]
        preview = {
            **timeline(level),
            "sourceSha256": sha(reviewed_source / level["filename"]),
            "rhythmDifficulty": rhythm_difficulty,
            **pruning,
        }
        preview.update(editor_timing_links(level, groups(level["notes"]), preview["endBeat"]))
        print("TIMELINE_JSON: " + json.dumps(preview), flush=True)
        return
    if args.difficulty is None or not (args.in_place or args.output_root or args.check_timings):
        raise ValueError("Choose an output difficulty and WIP folder")
    rhythm = (
        args.rhythm_source.resolve(strict=True)
        if args.rhythm_mode == "authored" and args.rhythm_source
        else source
    )
    if args.in_place:
        if rhythm != source or not args.expected_info_sha256 or not args.expected_target_sha256:
            raise ValueError(
                "Updating a song requires current difficulty and Song Info fingerprints from that song."
            )
        checked_song(source, args.difficulty, args.expected_info_sha256, args.expected_target_sha256)
    elif args.output_root:
        root = args.output_root.resolve(strict=True)
        if not root.is_dir() or any(root == folder or folder in root.parents for folder in (source, rhythm)):
            parser.error("Choose an existing output folder outside the two input maps")

    if args.rhythm_mode == "authored":
        require_chart(rhythm, rhythm_difficulty, "Rhythm")
    if timing_level is not None:
        if args.rhythm_mode == "manual" or args.timing_edits is not None:
            if args.timing_edits is None:
                raise ValueError("Open the timing editor first")
            edits = load(args.timing_edits)
            if edits.get("sourceSha256") != sha(reviewed_source / timing_level["filename"]):
                raise ValueError("The timing source changed. Reopen the timing editor and review your edits.")
            if parents and edits.get("merges"):
                beats_by_id = {n["id"]: n["b"] for n in timing_level["notes"]}
                edits["merges"] = [
                    pair for pair in edits["merges"]
                    if all(survives_parents(beats_by_id.get(pair.get(key), -1)) for key in ("noteId", "targetId"))
                ]
            notes = edited_notes(timing_level, edits)
        else:
            retained, merges = automatic_timings(timing_level, profile, protected, **timing_options)
            notes = apply_merges(timing_level, retained, merges)
    elif not args.independent_timings:
        authored = read_level(pattern_folder if batch is not None else rhythm, rhythm_difficulty)
        notes = authored["notes"]
        if batch is not None:
            timing_level = timing_link_context(
                authored, protected_reset_notes(authored), maximum_reach=profile.get("maximumLinkedReach")
            )
    if parents and timing_level is not None:
        notes = [n for n in notes if survives_parents(n["b"])]
        if not notes:
            raise ValueError(f"Standard {args.difficulty} has no timings left after applying higher difficulties.")
    conflicts = (
        timing_conflicts(saved, args.difficulty, list(groups(notes))) if not args.independent_timings else []
    )
    if args.check_timings:
        print("TIMING_CHECK_JSON: " + json.dumps(conflicts), flush=True)
        return
    require_nested_timings(conflicts)

    if timing_level is None and "maximumLinkedReach" in profile:
        authored = read_level(rhythm, rhythm_difficulty)
        prepared = timing_link_context(authored, protected_reset_notes(authored), maximum_reach=profile["maximumLinkedReach"])
        if prepared.get("simplifiedLinkCount"):
            timing_level, notes = prepared, authored["notes"]
    if timing_level is not None:
        timing_level = retain_timing_links(timing_level, notes)

    title = info.get("song", {}).get("title", info.get("_songName", "Song"))
    stem = re.sub(r"[^\w -]+", "-", title).strip(" -")[:70] or "Song"
    tag = uuid4().hex[:10]
    name = f"{stem} - Downmap {args.difficulty} {tag}"
    reports = Path(os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()) / "bs-downmap" / "runs" / name
    reports.mkdir(parents=True)
    print(f"Reports: {reports}", flush=True)
    if timing_level is not None:
        rhythm = reports / "rhythm-input"
        export(timing_level, notes, rhythm, {"njsMultiplier": 1, "jumpOffsetAddition": 0}, rhythm_difficulty)
        write(
            reports / "rhythm-input.json",
            {
                "mode": args.rhythm_mode,
                "sourceDifficulty": rhythm_difficulty,
                "sourceNotes": len(timing_level["notes"]),
                "notes": len(notes),
                "simplifiedLinkCount": timing_level.get("simplifiedLinkCount", 0),
                **({"timingModel": args.timing_model} if args.timing_model else {}),
                **({"rhythmLinks": args.rhythm_links} if args.rhythm_links or args.timing_model == NESTED_MODEL else {}),
            },
        )
        print(
            f"{args.rhythm_mode} timing input from {rhythm_difficulty}: {len(notes)} of {len(timing_level['notes'])} notes",
            flush=True,
        )
    script = Path(__file__).with_name("downmap.py").resolve()
    engine = [sys.executable, str(script)]
    command = engine + [
        "build",
        "--source",
        str(pattern_folder),
        "--source-difficulty",
        args.source_difficulty,
        "--rhythm-source",
        str(rhythm),
        "--difficulty",
        args.difficulty,
        "--output",
        str(reports),
        "--title-suffix",
        f"Downmap {args.difficulty} {tag}",
    ]
    command.extend(["--rhythm-difficulty", rhythm_difficulty])
    if args.jump_settings:
        command.extend(["--jump-settings", str(args.jump_settings.resolve())])
    if args.pattern_model:
        command.extend(["--pattern-model", args.pattern_model])
        if args.pattern_model_settings:
            command.extend(["--pattern-model-settings", str(args.pattern_model_settings.resolve())])
    if args.rhythm_mode == "best-guess":
        command.append("--retry-placement")
    if args.settings:
        profile_path = reports / "effective-profile.json"
        write(profile_path, profile)
        command.extend(["--profile", str(profile_path)])
    for step in (command, engine + ["freeze", "--output", str(reports)]):
        run_step(step, script.parent.parent, reports / f"{step[2]}.log")

    if args.in_place:
        target = install_difficulty(
            source, reports, args.difficulty, args.expected_info_sha256, args.expected_target_sha256, pending=pending
        )
        print(f"Updated Standard {args.difficulty}: {target}", flush=True)
        return

    # Keep the playable folder out of the song list until its copy is complete.
    destination = root / name
    with tempfile.TemporaryDirectory(prefix=".bs-downmap-", dir=root) as temporary:
        staging = Path(temporary) / "candidate"
        shutil.copytree(reports / "candidate", staging)
        if destination.exists():
            raise FileExistsError(destination)
        staging.rename(destination)
    print(f"WIP candidate: {destination}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as error:
        print("DOWNMAP_ERROR: " + " ".join(str(error).splitlines()), file=sys.stderr, flush=True)
        sys.exit(1)
