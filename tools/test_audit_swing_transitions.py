import unittest
from audit_swing_transitions import *


def n(b, d):
    return {"b": b, "d": d, "c": 0, "x": 0, "y": 0, "a": 0}


class TransitionTests(unittest.TestCase):
    def test_opposite_directions_mean_zero_rotation(self):
        self.assertEqual(rotation_lower_bound(n(0, 1), n(1, 0)), 0)

    def test_same_direction_requires_reset_rotation(self):
        self.assertEqual(rotation_lower_bound(n(0, 1), n(1, 1)), 180)

    def test_dot_is_unknown(self):
        self.assertIsNone(rotation_lower_bound(n(0, 8), n(1, 1)))

    def test_gap_changes_budget(self):
        self.assertLess(allowance(0.1), allowance(0.4))

    def test_horizontal_stream_separate_from_rotation(self):
        audio = {"songFrequency": 100, "bpmData": [{"ei": 100, "eb": 4}]}
        ns = [n(b, 2 if b % 2 else 3) for b in range(4)]
        self.assertEqual(len(horizontal_runs(ns, audio)), 1)
        self.assertTrue(all(rotation_lower_bound(a, b) == 0 for a, b in zip(ns, ns[1:])))


if __name__ == "__main__":
    unittest.main()
