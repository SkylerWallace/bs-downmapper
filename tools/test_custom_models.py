import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from custom_models import model_callback, pattern_seeds, timing_selection


class CustomModelTests(unittest.TestCase):
    def setUp(self):
        self.notes = [
            {"id": i, "b": float(i), "c": i % 2, "x": i, "y": 0, "d": 1, "a": 0}
            for i in range(3)
        ]
        self.level = {"notes": self.notes}

    def test_model_package_uses_overrides_and_cannot_mutate_eligible_notes(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "model"
            folder.mkdir()
            (folder / "model.json").write_text(json.dumps({
                "apiVersion": 1, "id": "custom-test", "kind": "timing", "label": "Test", "entry": "model.py",
                "settings": {"chosen": {"type": "number", "min": 0, "max": 2, "default": 0}},
            }), encoding="utf-8")
            (folder / "model.py").write_text(
                'def select_timings(context, settings):\n'
                '    context["level"]["notes"][0]["b"] = 999\n'
                '    return [settings["chosen"]]\n',
                encoding="utf-8",
            )
            with patch.dict("os.environ", BS_DOWNMAP_MODEL_DIR=directory):
                select = model_callback("timing", "custom-test", {"chosen": 1})
                selected = timing_selection(select, self.level, "Easy", {0}, {2}, 1, None)
                self.assertEqual([note["b"] for note in selected], [0, 1, 2])
                with self.assertRaisesRegex(ValueError, "Invalid value"):
                    model_callback("timing", "custom-test", {"chosen": 3})

    def test_timing_rejects_non_source_or_duplicate_ids(self):
        for invalid in ([99], [1, 1]):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                timing_selection(lambda _: invalid, self.level, "Easy", set(), set(), 1, None)

    def test_pattern_applies_geometry_only_and_preserves_protected_seeds(self):
        result = pattern_seeds(
            lambda _: {0: {"y": 2}, 1: {"y": 1, "d": 0}}, self.notes, self.level, self.level, "Hard", {0}
        )
        self.assertEqual(result, [self.notes[0], {**self.notes[1], "y": 1, "d": 0}, self.notes[2]])
        with self.assertRaisesRegex(ValueError, "Invalid pattern seed field"):
            pattern_seeds(lambda _: {1: {"c": 0}}, self.notes, self.level, self.level, "Hard", set())
