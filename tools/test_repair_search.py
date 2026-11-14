import unittest
from pathlib import Path
from subprocess import CompletedProcess
from unittest.mock import patch

from beat_clock import BeatClock
from downmap import PlacementDeadEnd, parallel_double_constraints, plan_geometry
from repair_search import relaxation_costs, within_window_budget


class RepairPolicyTests(unittest.TestCase):
    def test_bomb_clearance_is_never_relaxable(self):
        note = {"id": 1, "d": 1}
        policy = {
            "repairWindows": {1: 12},
            "repairCostScale": 1,
            "controls": {},
        }
        reasons = ["central_readability_window", "note_bomb_head_overlap", "bomb_swing_clearance"]
        self.assertEqual(relaxation_costs(note, policy, reasons), {"central_readability_window": 3.0})
        self.assertEqual(relaxation_costs({**note, "linkedAnchor": True}, policy, reasons), {})
        self.assertFalse(within_window_budget(25, 3))
        self.assertFalse(within_window_budget(15, 5))
        self.assertTrue(within_window_budget(20, 4))

    def test_top_row_recovery_relaxation_uses_elapsed_time_across_tiers(self):
        note = {"id": 1, "d": 1}
        policy = {
            "repairWindows": {1: 12},
            "repairCostScale": 1,
            "controls": {"topPalmSpacingSeconds": 1.0, "height": 0.55},
            "gaps": {1: 0.8},
        }
        reasons = ["top_row_recovery"]
        expert = relaxation_costs(note, policy, reasons)
        policy["controls"]["height"] = 0.40
        hard = relaxation_costs(note, policy, reasons)
        self.assertGreater(hard["top_row_recovery"], expert["top_row_recovery"])
        policy["gaps"][1] = 0.7
        self.assertEqual(relaxation_costs(note, policy, reasons), {})

    def test_side_wall_relaxation_requires_time_after_the_wall_ends(self):
        note = {"id": 1, "b": 2.4, "x": 0}
        policy = {
            "repairWindows": {1: 2.4},
            "repairCostScale": 1,
            "controls": {"sideWallRecoverySeconds": 0.45},
            "clock": BeatClock(60),
            "walls": [{"b": 1.8, "d": 0.2, "x": 0, "y": 0, "w": 2, "h": 3}],
        }
        reasons = ["side_wall_recovery_lane"]
        self.assertAlmostEqual(relaxation_costs(note, policy, reasons)[reasons[0]], 6.5)
        for beat in (1.9, 2.0, 2.1):
            with self.subTest(beat=beat):
                self.assertEqual(relaxation_costs({**note, "b": beat}, policy, reasons), {})

    def test_joint_retry_restores_an_authored_parallel_double(self):
        source = {
            1: {"id": 1, "d": 2, "geometryDirections": [0, 2, 3]},
            2: {"id": 2, "d": 2, "geometryDirections": [0, 2]},
        }
        split = [{"id": 1, "c": 0, "d": 2}, {"id": 2, "c": 1, "d": 0}]
        self.assertEqual(parallel_double_constraints(split, source, set()), {(1, 0), (1, 3), (2, 0)})
        self.assertIsNone(parallel_double_constraints(split, source, {(2, 2)}))
        diagonal = {
            1: {"id": 1, "d": 5, "geometryDirections": [0, 5]},
            2: {"id": 2, "d": 5, "geometryDirections": [0, 5]},
        }
        self.assertIsNone(parallel_double_constraints(split, diagonal, set()))
        self.assertEqual(parallel_double_constraints(split, diagonal, set(), (4, 5, 6, 7)), {(1, 0), (2, 0)})
        split[1]["linkedAnchor"] = True
        self.assertIsNone(parallel_double_constraints(split, source, set()))

    def test_joint_retry_carries_earlier_direction_choices_to_the_next_double(self):
        notes = [
            {"id": i, "b": beat, "c": hand, "d": 2, "x": hand, "y": 0, "geometryDirections": [0, 2]}
            for beat, first_id in ((10, 1), (20, 3))
            for hand, i in enumerate((first_id, first_id + 1))
        ]
        files = {}
        run = Path("unused")

        def plan(run):
            exclusions = files[run / "direction-input.json"]["excludedDirections"]
            planned = [{**note, "d": 2 if 0 in exclusions.get(note["id"], []) else 0} for note in notes]
            files[run / "direction-search.json"] = {"notes": planned}
            return CompletedProcess([], 0)

        def place(planned, *args):
            for i in (0, 2):
                group = planned[i : i + 2]
                if any(note["d"] != 2 for note in group):
                    raise PlacementDeadEnd(group[0]["b"], group, "No joint geometry")
            return planned, [], []

        policy = {"clock": BeatClock(60), "repairWindows": {1: 10, 2: 10}}
        profile = {"boundedRepairBeats": [10], "repairEmptyGeometryDomains": True}
        with (
            patch("downmap.geometry_context", return_value=policy),
            patch("downmap.placement_repair_costs", return_value={}),
            patch("downmap.write", side_effect=files.__setitem__),
            patch("downmap.load", side_effect=files.__getitem__),
            patch("downmap.run_direction_search", side_effect=plan),
            patch("downmap.geometry", side_effect=place),
        ):
            self.assertEqual(plan_geometry(run, {"notes": notes}, {}, [], profile)[0], notes)

        attempts = files[run / "placement-search.json"]
        self.assertEqual(len(attempts), 3)
        self.assertEqual(set(attempts[-1]["excludedDirections"]), {1, 2, 3, 4})
