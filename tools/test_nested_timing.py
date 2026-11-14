import unittest

from beat_clock import BeatClock
from nested_timing import fit_last_easy_double, refine_nested_timing


def note(note_id, beat, hand=0):
    return {"id": note_id, "b": beat, "c": hand, "x": hand + 1, "y": 0, "d": 1, "a": 0}


class NestedTimingTests(unittest.TestCase):
    def test_source_scores_swap_only_to_an_eligible_note_for_the_same_hand(self):
        start, weak, accent, end = (note(i, beat) for i, beat in enumerate((0, 0.65, 1, 2)))
        reference_only = note(4, 1.25)
        level = {"notes": [start, weak, accent, end], "anchorIds": set(), "clock": BeatClock(120)}
        reference = {"notes": [*level["notes"], reference_only]}
        evidence = ({1: 0.8, 1.25: 1.0}, {}, {1, 1.25})
        retained = [start, weak, end]
        result = refine_nested_timing(level, retained, [], set(), reference, evidence, "Normal")
        self.assertEqual(result, [start, accent, end])
        result = refine_nested_timing(level, retained, [], {weak["id"]}, reference, evidence, "Normal")
        self.assertEqual(result, retained)

    def test_double_donation_preserves_note_and_hand_counts(self):
        start, left, right, accent, end = (note(0, 0), note(1, 1), note(2, 1, 1), note(3, 2, 1), note(4, 3))
        level = {"notes": [start, left, right, accent, end], "anchorIds": set(), "clock": BeatClock(120)}
        result = refine_nested_timing(
            level, [start, left, right, end], [], set(), level, ({2: 1}, {}, {2}), "Normal"
        )
        self.assertEqual(result, [start, left, accent, end])

    def test_fixed_easy_budget_can_use_one_spaced_double_but_protects_anchors(self):
        first, left, right, last = note(0, 0), note(1, 1), note(2, 1, 1), note(3, 2)
        level = {"notes": [first, left, right, last], "anchorIds": set(), "clock": BeatClock(120)}
        retained = [first, left, last]
        self.assertIn(right, fit_last_easy_double(level, retained, [], set(), 4, {}))
        self.assertEqual(fit_last_easy_double(level, retained, [], {left["id"]}, 4, {}), retained)
