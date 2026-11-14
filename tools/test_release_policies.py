import unittest

from jump_settings import DEFAULT_NJS, hjd, jump_settings, verified_jump_settings
from timing_benchmark import score


class JumpSettingsTests(unittest.TestCase):
    def test_native_jump_settings_must_match_the_generation_policy(self):
        expected = jump_settings("Hard", 140, 18)
        supplied = dict(zip(("njs", "offset", "halfJumpDuration", "jumpDistance"), expected))
        self.assertEqual(verified_jump_settings("Hard", 140, 18, supplied), expected)
        with self.assertRaisesRegex(ValueError, "differ from the generation policy"):
            verified_jump_settings("Hard", 140, 18, {**supplied, "offset": supplied["offset"] + .25})

    def test_speed_policy_and_rounding(self):
        expected = {"Easy": 10, "Normal": 10, "Hard": 12.5, "Expert": 15, "ExpertPlus": 18}
        for difficulty, speed in expected.items():
            self.assertEqual(jump_settings(difficulty, 140, 18)[0], speed)

    def test_invalid_expert_plus_speed_uses_target_fallback(self):
        for invalid in (None, 9, True, "16", float("inf"), float("nan")):
            for difficulty, speed in DEFAULT_NJS.items():
                self.assertEqual(jump_settings(difficulty, 105, invalid)[0], speed)

    def test_quarter_beat_offsets_and_distance_match_the_audit(self):
        for bpm in (77, 105, 126.87, 140, 180, 220):
            for difficulty in DEFAULT_NJS:
                njs, offset, duration, distance = jump_settings(difficulty, bpm, 17)
                self.assertEqual(offset * 4, round(offset * 4))
                self.assertEqual(duration, hjd(bpm, njs, offset))
                self.assertAlmostEqual(distance, 2 * njs * duration * 60 / bpm)
                self.assertTrue(20 <= distance <= 32)


class TimingComplianceTests(unittest.TestCase):
    def test_beat_recall_and_note_multiplicity_are_separate(self):
        authored = [{"b": 1}, {"b": 1}, {"b": 2}]
        generated = [{"b": 1.00004}, {"b": 3}]
        result = score(authored, generated)
        self.assertEqual(result["beat_compliance"], 0.5)
        self.assertEqual(result["note_compliance"], 1 / 3)
        self.assertEqual(result["missed_beats"], [2])
        self.assertEqual(result["extra_beats"], [3])
