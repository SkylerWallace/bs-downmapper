"""Native v2/v3/v4 Standard map I/O and a v4 diagnostic bridge.

Linked objects and their authored colored-note anchors survive generation.
"""

import copy, hashlib, math, shutil
from pathlib import Path
import soundfile as sf
from analyze_standard_spreads import load, write, TOL
from beat_clock import BeatClock
from jump_settings import DEFAULT_NJS


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class DifficultyNotFound(ValueError):
    pass


def standard_entries(info):
    if "difficultyBeatmaps" in info:
        standard = [m for m in info["difficultyBeatmaps"] if m["characteristic"] == "Standard"]
        key = "difficulty"
    else:
        standard = [
            m
            for s in info["_difficultyBeatmapSets"]
            if s["_beatmapCharacteristicName"] == "Standard"
            for m in s["_difficultyBeatmaps"]
        ]
        key = "_difficulty"
    return {("ExpertPlus" if m[key] == "Expert+" else m[key]): m for m in standard}


def metadata(info, difficulty):
    entries = standard_entries(info)
    if difficulty in entries:
        return entries[difficulty]
    available = ", ".join(entries) or "none"
    raise DifficultyNotFound(f"Missing Standard {difficulty}. Available Standard difficulties: {available}.")


def normalize(data):
    version = data.get("version", data.get("_version", ""))
    if data.get("spawnRotations") or data.get("rotationEvents"):
        raise ValueError("Spawn rotations are unsupported")
    if not version.startswith("2.") and (
        data.get("_BPMChanges") or data.get("_customData", {}).get("_BPMChanges")
    ):
        raise ValueError("Legacy BPM metadata is only supported on v2 charts")
    if any(e.get("_type") in [14, 15, 100] for e in data.get("_events", [])):
        raise ValueError("Rotation/BPM gameplay events are unsupported")
    if data.get("_waypoints"):
        raise ValueError("Waypoints are unsupported")
    if version.startswith("2."):
        if any(n.get("_customData") for n in data["_notes"]):
            raise ValueError("Per-note custom data is unsupported")
        if any(n["_type"] not in [0, 1, 3] for n in data["_notes"]):
            raise ValueError("Unknown note type")
        ns = [
            {
                "b": float(n["_time"]),
                "x": n["_lineIndex"],
                "y": n["_lineLayer"],
                "c": n["_type"],
                "d": n["_cutDirection"],
                "a": 0,
                "id": i,
            }
            for i, n in enumerate(data["_notes"])
            if n["_type"] in [0, 1]
        ]
        bombs = [
            {"b": float(n["_time"]), "x": n["_lineIndex"], "y": n["_lineLayer"]}
            for n in data["_notes"]
            if n["_type"] == 3
        ]
        walls = []
        for w in data["_obstacles"]:
            if w["_type"] not in [0, 1] or w.get("_customData"):
                raise ValueError("Custom obstacle geometry is unsupported")
            walls.append(
                {
                    "b": w["_time"],
                    "x": w["_lineIndex"],
                    "y": 0 if w["_type"] == 0 else 2,
                    "d": w["_duration"],
                    "w": w["_width"],
                    "h": 5 if w["_type"] == 0 else 3,
                }
            )
    elif version.startswith("3."):
        if any(n.get("customData") for n in data["colorNotes"]):
            raise ValueError("Per-note custom data is unsupported")
        ns = [
            {k: n.get(k, 0) for k in ["b", "x", "y", "c", "d", "a"]} | {"id": i}
            for i, n in enumerate(data["colorNotes"])
        ]
        bombs = [{k: n.get(k, 0) for k in ["b", "x", "y"]} for n in data.get("bombNotes", [])]
        walls = [{k: n.get(k, 0) for k in ["b", "x", "y", "d", "w", "h"]} for n in data.get("obstacles", [])]
    elif version.startswith("4."):
        if any(n.get("customData") for n in data["colorNotes"] + data["colorNotesData"]):
            raise ValueError("Per-note custom data is unsupported")
        if any(
            e.get("r", 0) for key in ("colorNotes", "bombNotes", "obstacles") for e in data.get(key, [])
        ) or any(e.get("hr", 0) or e.get("tr", 0) for e in data.get("chains", [])):
            raise ValueError("Object rotation lanes are unsupported")
        ns = []
        for i, e in enumerate(data["colorNotes"]):
            n = data["colorNotesData"][e.get("i", 0)]
            ns.append(
                {"b": float(e.get("b", 0)), **{k: n.get(k, 0) for k in ["x", "y", "c", "d", "a"]}, "id": i}
            )
        bombs = [
            {"b": e.get("b", 0), **{k: data["bombNotesData"][e.get("i", 0)].get(k, 0) for k in ["x", "y"]}}
            for e in data.get("bombNotes", [])
        ]
        walls = [
            {
                "b": e.get("b", 0),
                **{k: data["obstaclesData"][e.get("i", 0)].get(k, 0) for k in ["x", "y", "d", "w", "h"]},
            }
            for e in data.get("obstacles", [])
        ]
    else:
        raise ValueError(f"Unsupported beatmap version {version}")
    if not ns:
        raise ValueError("No colored notes")
    for n in ns:
        if not (
            0 <= n["x"] <= 3
            and 0 <= n["y"] <= 2
            and n["c"] in [0, 1]
            and 0 <= n["d"] <= 8
            and all(math.isfinite(n[k]) for k in ["b", "x", "y", "a"])
        ):
            raise ValueError("Nonstandard grid/direction or non-finite note")
    return sorted(ns, key=lambda n: (n["b"], n["c"], n["x"], n["y"])), bombs, walls, version


def legacy_grid_clock(raw, bpm):
    """Read the v2 editor grid without reinterpreting stored object times."""
    changes = {}
    for container in (raw, raw.get("_customData", {})):
        for key in ("_BPMChanges", "_bpmChanges"):
            for event in container.get(key, []):
                beat, tempo = event["_time"], event["_BPM"]
                if not math.isfinite(beat) or not math.isfinite(tempo) or beat < 0 or tempo <= 0:
                    raise ValueError("Invalid legacy BPM change")
                changes.setdefault(beat, tempo)
    if not changes:
        return None
    grid_beat = previous_beat = 0
    previous_tempo = bpm
    events = []
    for beat, tempo in sorted(changes.items()):
        grid_beat += (beat - previous_beat) * previous_tempo / bpm
        events.append({"b": grid_beat, "m": tempo})
        previous_beat, previous_tempo = beat, tempo
    return BeatClock(bpm, events=events)


def linked_objects(data):
    """Yield each native object with its attachable note endpoints."""
    version = data.get("version", data.get("_version", ""))
    if version.startswith("4."):
        table = data.get("colorNotesData", [])
        for key, endpoints in [("arcs", [("hb", "hi"), ("tb", "ti")]), ("chains", [("hb", "i")])]:
            for obj in data.get(key, []):
                points = []
                for beat, index in endpoints:
                    # Only a centered arc endpoint can attach to a centered note.
                    if key == "arcs" and obj.get("hr" if beat == "hb" else "tr", 0):
                        continue
                    n = table[obj.get(index, 0)]
                    points.append((obj.get(beat, 0), n.get("c", 0), n.get("x", 0), n.get("y", 0)))
                yield key, obj, points
    elif version.startswith("3."):
        for arc in data.get("sliders", []):
            yield (
                "sliders",
                arc,
                [(arc["b"], arc["c"], arc["x"], arc["y"]), (arc["tb"], arc["c"], arc["tx"], arc["ty"])],
            )
        for chain in data.get("burstSliders", []):
            yield "burstSliders", chain, [(chain["b"], chain["c"], chain["x"], chain["y"])]
    elif version.startswith("2."):
        for arc in data.get("_sliders", []):
            yield (
                "_sliders",
                arc,
                [
                    (arc["_headTime"], arc["_colorType"], arc["_headLineIndex"], arc["_headLineLayer"]),
                    (arc["_tailTime"], arc["_colorType"], arc["_tailLineIndex"], arc["_tailLineLayer"]),
                ],
            )


def linked_endpoints(data):
    """Return attachable endpoints, including standalone arcs and chain heads."""
    for _, _, points in linked_objects(data):
        yield from points


def linked_anchors(ns, endpoints):
    return {
        n["id"]
        for n in ns
        if any(
            abs(n["b"] - beat) <= TOL and (n["c"], n["x"], n["y"]) == (hand, x, y)
            for beat, hand, x, y in endpoints
        )
    }


def read_level(folder, difficulty="ExpertPlus"):
    folder = Path(folder)
    info = load(folder / "Info.dat")
    meta = metadata(info, difficulty)
    if meta.get("_customData", {}).get("_requirements") or meta.get("customData", {}).get("requirements"):
        raise ValueError("Required gameplay mods are unsupported")
    filename = meta.get("beatmapDataFilename", meta.get("_beatmapFilename"))
    for name in [
        filename,
        info.get("audio", {}).get("songFilename", info.get("_songFilename")),
        info.get("coverImageFilename", info.get("_coverImageFilename")),
    ]:
        if not name or Path(name).name != name:
            raise ValueError("Package filenames must be flat local filenames")
    raw = load(folder / filename)
    ns, bombs, walls, version = normalize(raw)
    if "audio" in info:
        bpm = info["audio"]["bpm"]
        audio = load(folder / info["audio"]["audioDataFilename"])
    else:
        bpm = info["_beatsPerMinute"]
        audio = None
    if bpm <= 0:
        raise ValueError("Positive BPM required")
    clock = BeatClock(bpm, audio, raw.get("bpmEvents", []))
    endpoints = list(linked_endpoints(raw))
    njs = meta.get("noteJumpMovementSpeed", meta.get("_noteJumpMovementSpeed"))
    if type(njs) not in (int, float) or not math.isfinite(njs) or njs <= 0:
        njs = DEFAULT_NJS[difficulty]
    offset = meta.get("noteJumpStartBeatOffset", meta.get("_noteJumpStartBeatOffset", 0))
    if type(offset) not in (int, float) or not math.isfinite(offset):
        offset = 0
    return {
        "folder": folder,
        "difficulty": difficulty,
        "info": info,
        "meta": meta,
        "filename": filename,
        "raw": raw,
        "notes": ns,
        "bombs": bombs,
        "walls": walls,
        "version": version,
        "bpm": bpm,
        "clock": clock,
        "legacyGridClock": legacy_grid_clock(raw, bpm) if version.startswith("2.") else None,
        "audioData": audio,
        "linkedEndpoints": endpoints,
        "anchorIds": linked_anchors(ns, endpoints),
        "njs": njs,
        "offset": offset,
    }


def gameplay(raw, ns):
    out = copy.deepcopy(raw)
    version = raw.get("version", raw.get("_version"))
    if version.startswith("2."):
        out["_notes"] = [copy.deepcopy(n) for n in raw["_notes"] if n["_type"] == 3]
        out["_notes"] += [
            {
                "_time": n["b"],
                "_lineIndex": n["x"],
                "_lineLayer": n["y"],
                "_type": n["c"],
                "_cutDirection": n["d"],
            }
            for n in ns
        ]
        out["_notes"].sort(key=lambda n: (n["_time"], n["_type"]))
    elif version.startswith("3."):
        out["colorNotes"] = [{k: n[k] for k in ["b", "x", "y", "c", "d", "a"]} for n in ns]
    else:
        # Arc/chain metadata indexes still point into the original prefix.
        keys = ("x", "y", "c", "d", "a")
        table = out["colorNotesData"]
        indices = {tuple(n.get(k, 0) for k in keys): i for i, n in enumerate(table)}
        out["colorNotes"] = []
        for n in ns:
            signature = tuple(n[k] for k in keys)
            event = raw["colorNotes"][n["id"]]
            original_index = event.get("i", 0)
            if signature == tuple(table[original_index].get(k, 0) for k in keys):
                out["colorNotes"].append({"b": n["b"], "i": original_index})
                continue
            if signature not in indices:
                indices[signature] = len(table)
                table.append(dict(zip(keys, signature)))
            out["colorNotes"].append({"b": n["b"], "i": indices[signature]})
    return out


DIFFICULTY_RANKS = {"Easy": 1, "Normal": 3, "Hard": 5, "Expert": 7, "ExpertPlus": 9}


def export(level, ns, destination, profile, difficulty="Expert", title_suffix="", *, constant_njs=False, foundation=None):
    rank = DIFFICULTY_RANKS[difficulty]
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    info = copy.deepcopy(level["info"])
    meta = copy.deepcopy(level["meta"])
    foundation = level if foundation is None else foundation
    suffix = title_suffix.strip()
    if suffix:
        title_metadata, title_key = (info["song"], "title") if "audio" in info else (info, "_songName")
        title_metadata[title_key] = f"{title_metadata[title_key]} [{suffix}]"
    if "audio" in info:
        filename = f"{difficulty}.beatmap.dat"
        meta.update(
            difficulty=difficulty,
            beatmapDataFilename=filename,
            noteJumpMovementSpeed=level["njs"] * profile["njsMultiplier"],
            noteJumpStartBeatOffset=level["offset"] + profile["jumpOffsetAddition"],
        )
        meta["beatmapAuthors"] = {
            "mappers": copy.deepcopy(foundation["meta"].get("beatmapAuthors", {}).get("mappers", [])),
            "lighters": meta.get("beatmapAuthors", {}).get("lighters", []),
        }
        info["difficultyBeatmaps"] = [meta]
        files = {
            info["audio"]["songFilename"],
            info["audio"]["audioDataFilename"],
            info["coverImageFilename"],
            info["songPreviewFilename"],
            meta["lightshowDataFilename"],
        }
    else:
        filename = f"{difficulty}Standard.dat"
        meta.update(
            _difficulty=difficulty,
            _difficultyRank=rank,
            _beatmapFilename=filename,
            _noteJumpMovementSpeed=level["njs"] * profile["njsMultiplier"],
            _noteJumpStartBeatOffset=level["offset"] + profile["jumpOffsetAddition"],
        )
        info["_difficultyBeatmapSets"] = [
            {"_beatmapCharacteristicName": "Standard", "_difficultyBeatmaps": [meta]}
        ]
        if "_levelAuthorName" in foundation["info"]:
            info["_levelAuthorName"] = foundation["info"]["_levelAuthorName"]
        files = {info["_songFilename"], info["_coverImageFilename"]}
    for name in files:
        if Path(name).name != name:
            raise ValueError("Non-flat referenced filename")
        shutil.copyfile(level["folder"] / name, destination / name)
    raw = gameplay(level["raw"], ns)
    if constant_njs and "njsEvents" in raw:
        raw["njsEvents"] = []
        if "njsEventData" in raw:
            raw["njsEventData"] = []
    write(destination / filename, raw)
    write(destination / "Info.dat", info)
    return destination / filename


def diagnostic_package(level, ns, destination, njs=None, offset=None):
    """A v4 mirror for the existing audits; never replaces native deliverable."""
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    info = level["info"]
    song = info.get("audio", {}).get("songFilename", info.get("_songFilename"))
    cover = info.get("coverImageFilename", info.get("_coverImageFilename"))
    audioinfo = sf.info(level["folder"] / song)
    duration = audioinfo.frames / audioinfo.samplerate
    audio = level.get("audioData")
    if audio is None:
        clock = level["clock"]
        beats = clock.starts[-1] + (duration - clock.times[-1]) / clock.rates[-1]
        bounds = [*clock.starts, beats]
        audio = {
            "songChecksum": "",
            "songSampleCount": audioinfo.frames,
            "songFrequency": audioinfo.samplerate,
            "bpmData": [
                {
                    "si": round(clock.seconds(a) * audioinfo.samplerate),
                    "ei": round(clock.seconds(b) * audioinfo.samplerate),
                    "sb": a,
                    "eb": b,
                }
                for a, b in zip(bounds, bounds[1:])
                if b > a
            ],
        }
    raw = {
        "version": "4.0.0",
        "colorNotes": [{"b": n["b"], "i": i} for i, n in enumerate(ns)],
        "colorNotesData": [{k: n[k] for k in ["x", "y", "c", "d", "a"]} for n in ns],
        "bombNotes": [{"b": n["b"], "i": i} for i, n in enumerate(level["bombs"])],
        "bombNotesData": [{k: n[k] for k in ["x", "y"]} for n in level["bombs"]],
        "obstacles": [{"b": n["b"], "i": i} for i, n in enumerate(level["walls"])],
        "obstaclesData": [{k: n[k] for k in ["x", "y", "d", "w", "h"]} for n in level["walls"]],
        "arcs": [],
        "arcsData": [],
        "chains": [],
        "chainsData": [],
        "spawnRotations": [],
        "spawnRotationsData": [],
        "njsEvents": [],
        "njsEventData": [],
    }
    metadata4 = {
        "version": "4.0.1",
        "audio": {"bpm": level["bpm"], "songFilename": song, "audioDataFilename": "AudioData.dat"},
        "coverImageFilename": cover,
        "difficultyBeatmaps": [
            {
                "characteristic": "Standard",
                "difficulty": "Expert",
                "beatmapDataFilename": "Expert.beatmap.dat",
                "noteJumpMovementSpeed": level["njs"] if njs is None else njs,
                "noteJumpStartBeatOffset": level["offset"] if offset is None else offset,
            }
        ],
    }
    write(destination / "Info.dat", metadata4)
    write(destination / "AudioData.dat", audio)
    write(destination / "Expert.beatmap.dat", raw)
    return destination
