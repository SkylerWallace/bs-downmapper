import unittest
from audit_motion_visibility import *


def n(b, c, x, y, d=1):
    return {"b": b, "t": b, "c": c, "x": x, "y": y, "d": d, "a": 0}


class ExpandedTests(unittest.TestCase):
    def test_lower_first_crossed_posture(self):
        ns = [n(0, 0, 3, 2), n(0, 1, 0, 0), n(0.25, 1, 2, 2)]
        self.assertTrue(sequence_findings(ns, DEFAULT))

    def test_top_first_unwinds(self):
        self.assertFalse(
            sequence_findings([n(0, 0, 3, 2), n(0, 1, 0, 0), n(0.25, 0, 0, 2), n(0.5, 1, 3, 0)], DEFAULT)
        )

    def test_stale_crossing_does_not_persist(self):
        self.assertFalse(sequence_findings([n(0, 0, 3, 2), n(0, 1, 0, 0), n(2, 1, 2, 2)], DEFAULT))

    def test_different_times_no_capsule_overlap(self):
        self.assertIsNone(timed_distance(n(0, 0, 2, 1), n(1, 1, 1, 1), DEFAULT))

    def test_same_trajectory_distance_zero(self):
        a = n(0, 0, 1, 1)
        self.assertAlmostEqual(timed_distance(a, dict(a), DEFAULT), 0)

    def test_ray_hits_and_misses(self):
        self.assertTrue(ray_box((0, 0, 0), (0, 0, 10), (-1, -1, 2), (1, 1, 3)))
        self.assertFalse(ray_box((0, 0, 0), (0, 0, 10), (2, 2, 2), (3, 3, 3)))

    def test_aligned_notes_occlude(self):
        p = {**DEFAULT, "eyeHeightMeters": 1.4}
        ns = [n(1, 0, 1.5, 1), n(1.5, 1, 1.5, 1)]
        self.assertTrue(any(f["rule"] == "V01" for f in vision_findings(ns, [], 15, 0.85, p)))

    def test_separated_notes_do_not_occlude(self):
        self.assertFalse(vision_findings([n(1, 0, 0, 0), n(1.5, 1, 3, 2)], [], 15, 0.85, DEFAULT))

    def test_hjd(self):
        self.assertEqual(hjd(140, 17, -0.25), 1.75)
        self.assertEqual(hjd(140, 15, 0), 2)
        with self.assertRaises(ValueError):
            hjd(140, 0, 0)

    def test_blade_extension_not_note_as_hand(self):
        tip = (0, 1.4, 1.3)
        anchor = (0, 1.4, 0)
        g = tip_to_grip(tip, anchor, DEFAULT)
        self.assertAlmostEqual(norm(tip, g), DEFAULT["bladeOffsetMeters"])

    def test_point_segment_3d(self):
        self.assertEqual(distance3((1, 1, 1), (0, 0, 0), (0, 0, 2)), math.sqrt(2))

    def test_wall_occlusion(self):
        p = {**DEFAULT, "eyeHeightMeters": 1.4}
        ns = [n(1.5, 0, 1.5, 1)]
        wall = {"b": 1, "start": 1, "end": 2, "x": 0, "y": 0, "w": 4, "h": 5}
        self.assertTrue(any(f["rule"] == "V02" for f in vision_findings(ns, [wall], 15, 0.85, p)))

    def test_head_clearance_surrogate_can_flag(self):
        p = {**DEFAULT, "bladeOffsetMeters": 0.1, "hitPlaneMeters": 0.15, "controllerRadiusMeters": 0.2}
        self.assertTrue(any(f["rule"] == "C02" for f in physical_findings([n(0, 0, 1.5, 1)], p)))

    def test_bomb_reset_invalidates_crossing_inference(self):
        ns = [n(0, 0, 3, 2), n(0, 1, 0, 0), n(0.25, 1, 2, 2)]
        findings = sequence_findings(ns, DEFAULT, [{"t": 0.1}])
        self.assertTrue(findings)
        self.assertEqual(findings[0]["rule"], "S02")

    def test_dot_motion_is_ambiguous(self):
        findings = sequence_findings([n(0, 0, 3, 2), n(0, 1, 0, 0), n(0.25, 1, 2, 2, 8)], DEFAULT)
        self.assertEqual(findings[0]["rule"], "S02")

    def test_early_occlusion_is_not_late_warning(self):
        p = {**DEFAULT, "eyeHeightMeters": 1.4}
        findings = vision_findings([n(1, 0, 1.5, 1), n(1.5, 1, 1.5, 1)], [], 15, 0.85, p)
        self.assertTrue(findings)
        self.assertEqual(findings[0]["severity"], "early_occlusion_info")

    def test_upper_already_cut_is_not_lower_first_proof(self):
        f = sequence_findings([n(0, 0, 1, 0), n(0.25, 1, 0, 1, 2), n(0.5, 0, 1, 0, 0)], DEFAULT)
        self.assertTrue(f)
        self.assertEqual(f[0]["rule"], "S02")


if __name__ == "__main__":
    unittest.main()
