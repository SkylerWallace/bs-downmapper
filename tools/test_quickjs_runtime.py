import json
from pathlib import Path
import tempfile
import unittest

from downmap import QUICKJS, run_quickjs


@unittest.skipUnless((QUICKJS / "qjs.exe").is_file(), "Requires the packaged QuickJS runner")
class QuickJsRuntimeTests(unittest.TestCase):
    def test_parity_accepts_and_flags_the_same_synthetic_swings(self):
        with tempfile.TemporaryDirectory(prefix="bs-downmap-方向-") as temporary:
            output = Path(temporary) / "parity.json"
            for directions, expected in (([1, 0, 1], 0), ([1, 0, 1, 1], 1)):
                chart = {
                    "version": "4.0.0",
                    "colorNotes": [{"b": i / 2, "i": i} for i in range(len(directions))],
                    "colorNotesData": [{"x": 1, "y": 0, "c": 0, "d": cut} for cut in directions],
                }
                result = run_quickjs("check_parity", json.dumps({
                    "chart": chart, "bpm": 140, "reentries": [],
                }), output)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(output.read_bytes())["errors"], expected)

    def test_invalid_direction_request_does_not_produce_a_result(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "directions.json"
            result = run_quickjs("plan_directions", '{"notes":[],"bombs":[]}', output)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Explicit BPM required", result.stderr)
            self.assertFalse(output.exists())
