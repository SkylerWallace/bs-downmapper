"""Run editor entry point regressions against a built engine host."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

HOST = None
ROOT = Path(__file__).resolve().parents[1]


class EditorHostChecks(unittest.TestCase):
    def setUp(self):
        workspace = tempfile.TemporaryDirectory()
        self.addCleanup(workspace.cleanup)
        self.workspace = Path(workspace.name)
        self.song = self.workspace / "song"
        self.song.mkdir()
        self.info = self.song / "Info.dat"
        self.chart = self.song / "Expert.dat"
        self.write(self.info, {
            "_beatsPerMinute": 120, "_songFilename": "song.ogg",
            "_difficultyBeatmapSets": [{
                "_beatmapCharacteristicName": "Standard",
                "_difficultyBeatmaps": [{"_difficulty": "Expert", "_beatmapFilename": "Expert.dat"}],
            }],
        })
        self.write(self.chart, {
            "version": "3.3.0",
            "colorNotes": [{"b": 2, "x": 1, "y": 1, "c": 0, "d": 0},
                           {"b": 4, "x": 2, "y": 1, "c": 1, "d": 1}],
        })

    @staticmethod
    def write(path, data):
        path.write_text(json.dumps(data), encoding="utf-8")

    def invoke(self, operation, **request):
        return subprocess.run(
            ["dotnet", str(HOST), operation],
            input=json.dumps(dict(engine=str(ROOT), source=str(self.song), **request)),
            encoding="utf-8", capture_output=True, timeout=30,
        )

    def test_editor_preview_returns_saved_beats_hands_and_source_hash(self):
        result = self.invoke("editor-preview", difficulty="Expert", **{"rhythm-mode": "existing"})
        self.assertEqual(result.returncode, 0, result.stderr)
        prefix = "TIMELINE_JSON: "
        self.assertTrue(result.stdout.startswith(prefix))
        timeline = json.loads(result.stdout[len(prefix):])
        self.assertEqual([(2, [0]), (4, [1])], [(n["beat"], n["hands"]) for n in timeline["timings"]])
        self.assertEqual([], timeline["removedNoteIds"])
        self.assertEqual(hashlib.sha256(self.chart.read_bytes()).hexdigest(), timeline["sourceSha256"])

    def test_editor_generation_rejects_stale_info_before_side_effects(self):
        plan = self.workspace / "plan.json"
        self.write(plan, {
            "infoSha256": hashlib.sha256(self.info.read_bytes()).hexdigest(),
            "foundation": "ExpertPlus",
            "targets": [{"difficulty": "Expert", "targetSha256": hashlib.sha256(self.chart.read_bytes()).hexdigest()}],
        })
        self.info.write_bytes(self.info.read_bytes() + b"\n")
        before = {path.name: path.read_bytes() for path in self.song.iterdir()}
        reports = self.workspace / "reports"
        result = self.invoke("editor-generate", reports=str(reports), **{"batch-plan": str(plan)})
        self.assertEqual(1, result.returncode)
        self.assertIn("DOWNMAP_ERROR: Song Info changed.", result.stderr)
        self.assertEqual(before, {path.name: path.read_bytes() for path in self.song.iterdir()})
        self.assertFalse(reports.exists())


if __name__ == "__main__":
    HOST = Path(sys.argv.pop(1)).resolve()
    unittest.main()
