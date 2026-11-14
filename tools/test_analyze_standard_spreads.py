import unittest
from analyze_standard_spreads import notes, pair, seconds, peak


class AnalysisTests(unittest.TestCase):
    def test_v4_defaults_and_index(self):
        self.assertEqual(
            notes({"colorNotes": [{"b": 2, "i": 1}, {}], "colorNotesData": [{}, {"x": 3, "c": 1, "d": 8}]}),
            [
                {"b": 0.0, "x": 0, "y": 0, "c": 0, "d": 0, "a": 0},
                {"b": 2.0, "x": 3, "y": 0, "c": 1, "d": 8, "a": 0},
            ],
        )

    def test_simultaneous_stack_matches_despite_array_order(self):
        a = notes({"colorNotes": [{}, {"i": 1}], "colorNotesData": [{"x": 1}, {"x": 2}]})
        counts, _ = pair(a, list(reversed(a)))
        self.assertEqual(counts["unchanged"], 2)

    def test_hand_change_is_not_exact_retention(self):
        a = notes({"colorNotes": [{}], "colorNotesData": [{}]})
        b = [dict(a[0], c=1)]
        counts, _ = pair(a, b)
        self.assertEqual(counts["sameTimeSameHandPairs"], 0)
        self.assertEqual(counts["targetTimingGroupsPresentInSource"], 1)

    def test_timing_tolerance_and_added_timing(self):
        a = notes({"colorNotes": [{"b": 1}], "colorNotesData": [{}]})
        c, _ = pair(a, [dict(a[0], b=1.00005), dict(a[0], b=2)])
        self.assertEqual(c["unchanged"], 1)
        self.assertEqual(c["targetTimingGroupsAbsentInSource"], 1)

    def test_variable_tempo(self):
        audio = {
            "songFrequency": 100,
            "bpmData": [{"ei": 100, "eb": 2}, {"si": 100, "ei": 300, "sb": 2, "eb": 4}],
        }
        self.assertEqual(seconds(3, audio), 2)

    def test_half_open_peak_window(self):
        self.assertEqual(peak([0, 0.5, 1], 1), 2)


if __name__ == "__main__":
    unittest.main()
