from pathlib import Path
import json
import tempfile
import unittest

from bookmark_files import bookmark_positions, copy_bookmarks


class BookmarkFilesTests(unittest.TestCase):
    def test_merges_formats_and_limits_sidecars_to_the_source_chart(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "source"
            destination = Path(temporary) / "destination"
            folder = source / "Bookmarks"
            folder.mkdir(parents=True)
            destination.mkdir()
            for name, characteristic, difficulty, beat in (
                ("first", "Standard", "ExpertPlus", 16),
                ("second", "Standard", "expertplus", 48),
                ("other", "Standard", "Expert", 64),
                ("nonstandard", "Legacy", "ExpertPlus", 96),
            ):
                (folder / f"{name}.bookmarks.dat").write_text(json.dumps({
                    "characteristic": characteristic,
                    "difficulty": difficulty,
                    "bookmarks": [{"beat": beat, "label": "Unrelated label"}, {"beat": "bad"}],
                }), encoding="utf-8")
            (folder / "bad.bookmarks.dat").write_text("{", encoding="utf-8")
            chart = {"_bookmarks": [{"_time": 8}],
                     "customData": {"bookmarks": [{"b": 16}]},
                     "_customData": {"_bookmarks": [{"_time": 32}]}}
            copy_bookmarks(source, destination)
            self.assertEqual(bookmark_positions(chart, destination, "ExpertPlus"), [8, 16, 32, 48])
            self.assertEqual(bookmark_positions({}, destination, "Easy"), [])
