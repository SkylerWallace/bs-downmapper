import unittest
from pathlib import Path
from analyze_standard_spreads import load
from audit_placements import check_group, segment_distance


class PlacementTests(unittest.TestCase):
    def test_all_user_examples_flagged(self):
        fixture = load(Path(__file__).with_name("fixtures") / "rejected-placements.json")
        for index, case in enumerate(fixture):
            with self.subTest(case=index):
                self.assertTrue(check_group(case["notes"]))

    def test_parallel_separated_vertical_double(self):
        self.assertFalse(
            check_group([{"b": 0, "c": 0, "x": 0, "y": 0, "d": 1}, {"b": 0, "c": 1, "x": 3, "y": 0, "d": 1}])
        )

    def test_aligned_same_hand_diagonal(self):
        self.assertFalse(
            check_group([{"b": 0, "c": 0, "x": 0, "y": 0, "d": 5}, {"b": 0, "c": 0, "x": 1, "y": 1, "d": 5}])
        )

    def test_outward_followthrough_and_preswing(self):
        self.assertTrue(
            check_group([{"b": 0, "c": 0, "x": 1, "y": 0, "d": 2}, {"b": 0, "c": 1, "x": 2, "y": 0, "d": 3}])
        )

    def test_geometry_symmetry(self):
        self.assertEqual(segment_distance((0, 0), (1, 1), (0, 1), (1, 0)), 0)
        self.assertEqual(segment_distance((0, 0), (0, 1), (3, 0), (3, 1)), 3)


if __name__ == "__main__":
    unittest.main()
