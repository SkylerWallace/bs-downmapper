"""Read section bookmarks from embedded map metadata and Official Editor sidecars."""
import json
import math
from pathlib import Path
import shutil


def bookmark_files(folder):
    return (path for path in (Path(folder) / "Bookmarks").glob("*")
            if path.is_file() and path.name.lower().endswith(".bookmarks.dat"))


def copy_bookmarks(source, destination):
    for path in bookmark_files(source):
        target = Path(destination) / "Bookmarks"
        target.mkdir(exist_ok=True)
        shutil.copyfile(path, target / path.name)


def bookmark_positions(chart, folder=None, difficulty=None):
    def positions(rows):
        for row in rows if isinstance(rows, list) else ():
            if isinstance(row, dict):
                beat = row.get("beat", row.get("b", row.get("_time")))
                if type(beat) in (float, int) and math.isfinite(beat) and beat >= 0:
                    yield beat

    beats = set()
    for data in (chart, chart.get("customData"), chart.get("_customData")):
        if isinstance(data, dict):
            for field in ("bookmarks", "_bookmarks"):
                beats.update(positions(data.get(field)))
    if folder is not None and difficulty is not None:
        for path in bookmark_files(folder):
            try:
                data = json.loads(path.read_text(encoding="utf-8-sig"))
            except (OSError, ValueError):
                continue
            if (isinstance(data, dict) and str(data.get("characteristic", "")).lower() == "standard"
                    and str(data.get("difficulty", "")).lower() == difficulty.lower()):
                beats.update(positions(data.get("bookmarks")))
    return sorted(beats)
