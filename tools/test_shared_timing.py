import unittest

from beat_clock import BeatClock
from shared_timing import prefer_linked_repeats, select_shared_timings


def note(i, beat, hand=0):
    return {"id": i, "b": beat, "c": hand, "x": hand + 1, "y": 0}


class SharedTimingTests(unittest.TestCase):
    def test_repeat_preference_trades_locally_and_keeps_a_unique_fill(self):
        beats = [0, 1, 1.3, 1.75, 3, 8, 9, 9.3, 11]
        notes = [note(i, beat) for i, beat in enumerate(beats)]
        level = {"notes": notes, "anchorIds": set(), "clock": BeatClock(120)}
        selected = [notes[i] for i in (0, 2, 3, 4, 5, 6, 8)]
        repeats = (beats, [{"start": 0, "end": 4}, {"start": 8, "end": 12}], [[1, 6], [2, 7]])
        scores = dict.fromkeys(beats, 0.6)
        scores[1] = 0.55
        scores[9.3] = 0.46
        output = prefer_linked_repeats(level, selected, set(), "Hard", scores, repeats)
        self.assertEqual([n["b"] for n in output], [0, 1, 1.75, 3, 8, 9, 11])

        for protected, required in (({2}, ()), (set(), (1.3,))):
            output = prefer_linked_repeats(level, selected, protected, "Hard", scores, repeats, required_beats=required)
            self.assertEqual(output, selected)
        scores[1] = 0.35
        self.assertEqual(prefer_linked_repeats(level, selected, set(), "Hard", scores, repeats), selected)
    def test_trade_keeps_the_song_count_and_protected_head(self):
        start, pickup, weak, end = [note(i, beat) for i, beat in enumerate((0, 0.75, 1.5, 3))]
        level = {"notes": [start, pickup, weak, end], "anchorIds": {0}, "clock": BeatClock(120)}
        beats = [0, 0.75, 1.5, 3]
        rank = {0: 1, 0.75: 1, 1.5: 0, 3: 1}
        selected = select_shared_timings(
            level, [start, weak, end], set(), "Easy", {"Easy": rank}, dict.fromkeys(beats, 0), [0.75], beats
        )
        self.assertEqual([n["id"] for n in selected], [0, 1, 3])

        selected = select_shared_timings(
            level, [start, weak, end], {2}, "Easy", {"Easy": rank}, dict.fromkeys(beats, 0), [0.75], beats
        )
        self.assertEqual([n["id"] for n in selected], [0, 2, 3])

    def test_a_highly_ranked_beat_cannot_bypass_parent_eligibility_or_spacing(self):
        start, too_close, kept, end = [note(i, beat) for i, beat in enumerate((0, 0.25, 1.5, 3))]
        level = {"notes": [start, too_close, kept, end], "anchorIds": set(), "clock": BeatClock(120)}
        reference = [0, 0.25, 0.75, 1.5, 3]
        rank = {0: 1, 0.25: 1, 0.75: 1, 1.5: 0, 3: 1}
        selected = select_shared_timings(
            level,
            [start, kept, end],
            set(),
            "Easy",
            {"Easy": rank},
            dict.fromkeys(reference, 0),
            [0.25, 0.75],
            reference,
        )
        self.assertEqual([n["id"] for n in selected], [0, 2, 3])
