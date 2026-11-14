import unittest

from beat_clock import BeatClock
from nested_priority import fraction_budget, match_repeat_priorities, priority_order, select_prefix


class NestedPriorityTests(unittest.TestCase):
    def setUp(self):
        self.notes = [{"id": i, "b": i / 2, "c": i % 2} for i in range(20)]
        self.reference = {"notes": self.notes, "clock": BeatClock(120)}
        self.plans = {
            "Easy": self.notes[::5],
            "Normal": self.notes[::2],
            "Hard": self.notes[2:16],
            "Expert": self.notes[2:20],
        }
        self.scores = {
            tier: {note["b"]: 1 - note["id"] / 20 for note in self.notes}
            for tier in self.plans
        }
        self.order, self.budgets = priority_order(self.reference, self.plans, self.scores, {1})

    def selected(self, budget, eligible=None, required=()):
        return select_prefix(
            self.reference, self.order, self.notes if eligible is None else eligible,
            budget, {1}, required, source_is_reference=True,
        )

    def test_every_smaller_budget_is_a_subset_of_the_larger_prefix(self):
        higher = None
        for percent in range(100, 0, -1):
            notes = self.selected(fraction_budget(self.reference, percent / 100))
            ids = {note["id"] for note in notes}
            self.assertIn(1, ids)
            if higher is not None:
                self.assertLessEqual(ids, higher)
            higher = ids

    def test_easy_first_matches_full_spread_and_saved_lower_chart_can_reserve_a_hit(self):
        direct = self.selected(self.budgets["Easy"])
        eligible = self.notes
        for tier in ("Expert", "Hard", "Normal", "Easy"):
            eligible = self.selected(self.budgets[tier], eligible, [note["b"] for note in direct])
        self.assertEqual(eligible, direct)
        outside = self.notes[self.order[-1]]
        self.assertNotIn(outside, direct)
        self.assertIn(outside, self.selected(self.budgets["Easy"], required=[outside["b"]]))
        with self.assertRaisesRegex(ValueError, "saved lower difficulty"):
            self.selected(self.budgets["Easy"], eligible=direct, required=[outside["b"]])

    def test_repeat_matching_changes_only_one_cutoff_and_keeps_the_priority_order_nested(self):
        beats = [0, 1, 1.3, 1.75, 3, 8, 9, 9.3, 11]
        notes = [{"id": i, "b": beat, "c": 0} for i, beat in enumerate(beats)]
        level = {"notes": notes, "anchorIds": set(), "clock": BeatClock(120)}
        order = [0, 3, 4, 2, 5, 6, 8, 1, 7]
        budgets = {"Normal": 3, "Hard": 7, "Expert": 9}
        scores = {tier: dict.fromkeys(beats, .6) for tier in budgets}
        scores["Hard"].update({1: .56, 9.3: .46})
        contexts = dict.fromkeys(budgets, (level, set()))
        repeats = (beats, [{"start": 0, "end": 4}, {"start": 8, "end": 12}], [[1, 6], [2, 7]])
        changed = match_repeat_priorities(level, order, budgets, scores, contexts, set(), repeats)
        self.assertEqual(changed[:3], order[:3])
        self.assertEqual(changed, [0, 3, 4, 1, 5, 6, 8, 2, 7])
        self.assertEqual(set(changed), set(order))
        self.assertIn(3, changed[:7])  # The unmatched fill stays in place.
        self.assertEqual(
            match_repeat_priorities(level, order, budgets, scores, contexts, {2}, repeats), order
        )
        scores["Hard"][1] = .52
        self.assertEqual(
            match_repeat_priorities(level, order, budgets, scores, contexts, set(), repeats), order
        )
