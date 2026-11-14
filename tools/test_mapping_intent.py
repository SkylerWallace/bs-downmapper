import copy, unittest
from beat_clock import BeatClock
from mapping_intent import (
    seed_from_rhythm,
    detect_features,
    rhythm_signature,
    project_bomb_returns,
    mark_crossover_support,
    mark_bomb_reentries,
)


def n(b, c, x, y, d, i):
    return dict(b=b, c=c, x=x, y=y, d=d, a=0, id=i)


class ManualRhythmTests(unittest.TestCase):
    def test_manual_geometry_never_seeds_generation(self):
        source = [n(10, 0, 0, 0, 1, 0), n(10.5, 1, 3, 1, 0, 1)]
        mask = [n(10, 0, 3, 2, 4, 20), n(10.5, 0, 3, 2, 4, 21), n(10.5, 0, 0, 0, 7, 22)]
        a, log = seed_from_rhythm(source, mask)
        changed = copy.deepcopy(mask)
        for x in changed:
            x.update(x=2, y=2, d=8)
        b, _ = seed_from_rhythm(source, changed)
        self.assertEqual(a, b)
        self.assertEqual(rhythm_signature(a), rhythm_signature(mask))
        self.assertEqual(len(a), 3)
        self.assertEqual(a[1]["x"], 0)  # Other-hand source is mirrored.
        self.assertTrue(log[1]["mirroredOtherHandSeed"])

    def test_shifted_paired_feature_keeps_hand_roles(self):
        for start in [7.125, 41, 203.5]:
            source = [
                n(start, 0, 2, 0, 6, 0),
                n(start + 0.5, 0, 3, 1, 5, 1),
                n(start + 1, 1, 1, 0, 7, 2),
                n(start + 1.5, 1, 0, 1, 4, 3),
                n(start + 2, 0, 2, 0, 6, 4),
                n(start + 2.5, 0, 3, 1, 5, 5),
            ]
            marked, features = detect_features(source, [], 101)
            self.assertEqual(len(features), 1)
            self.assertEqual(features[0]["kind"], "paired_hand_call_response")
            self.assertEqual([n["intent"]["targetDirection"] for n in marked], [1, 0, 1, 0, 1, 0])
            self.assertEqual(rhythm_signature(source), rhythm_signature(marked))

    def test_structured_bomb_reset_and_negative_controls(self):
        for start in [9, 46.125, 211.5]:
            source = [n(start, 1, 2, 0, 6, 0), n(start + 0.75, 1, 1, 0, 6, 1), n(start + 1.5, 1, 3, 0, 5, 2)]
            bombs = [dict(b=start + 0.5, x=2, y=0)]
            marked, features = detect_features(source, bombs, 101)
            resets = [f for f in features if f["kind"] == "bomb_return_reset"]
            self.assertEqual(len(resets), 1)
            self.assertEqual(resets[0]["noteIds"], [0, 1])
            self.assertTrue(marked[1]["intent"]["preservePosition"])
            self.assertEqual(detect_features(source, [], 101)[1], [])
            self.assertEqual(detect_features(source, [dict(b=start + 0.5, x=0, y=2)], 101)[1], [])
            alternating = copy.deepcopy(source)
            alternating[1]["d"] = 5
            self.assertFalse(
                any(f["kind"] == "bomb_return_reset" for f in detect_features(alternating, bombs, 101)[1])
            )

    def test_reset_exit_depends_on_remaining_bomb_cues(self):
        for start in [8, 43.125, 210]:
            ns = [
                n(start, 1, 2, 0, 6, 0),
                n(start + 0.75, 1, 1, 0, 6, 1),
                n(start + 1.5, 1, 0, 0, 6, 2),
                n(start + 2, 1, 1, 0, 5, 3),
            ]
            marked, features = detect_features(ns, [dict(b=start + 0.5, x=2, y=0)], 101)
            self.assertEqual(marked[2]["intent"]["targetDirection"], 5)
            self.assertEqual(marked[2]["intent"]["preferredCell"], [3, 1])
            self.assertEqual(marked[3]["intent"]["targetDirection"], 1)
            self.assertTrue(any(f["kind"] == "reset_exit" for f in features))
            continued, _ = detect_features(
                ns, [dict(b=start + 0.5, x=2, y=0), dict(b=start + 1.25, x=1, y=0)], 101
            )
            self.assertEqual(continued[2]["intent"]["kind"], "bomb_return_reset")
            self.assertEqual(continued[2]["intent"]["targetDirection"], 6)

    def test_source_reset_survives_removal_of_bombs_and_middle_hit(self):
        start = 43.125
        original = [n(start, 1, 2, 0, 6, 0), n(start + 0.75, 1, 1, 0, 6, 1), n(start + 1.5, 1, 0, 0, 6, 2)]
        source = {
            "notes": original,
            "bombs": [dict(b=start + 0.5, x=2, y=0), dict(b=start + 1.25, x=1, y=0)],
            "bpm": 101,
        }
        for hand in (0, 1):
            mask = [original[0], {**original[1], "c": 0}, {**original[2], "c": hand}]
            marked, log = seed_from_rhythm(original, mask)
            features = []
            project_bomb_returns(marked, features, source, log, vertical=True)
            self.assertEqual(rhythm_signature(marked), rhythm_signature(mask))
            if hand == 0:
                self.assertFalse(features)
            else:
                self.assertEqual([f["noteIds"] for f in features], [[0, 2]])
                self.assertEqual(marked[-1]["intent"]["resetFromBeat"], start)
                self.assertEqual(marked[-1]["intent"]["allowedDirections"], [1])

    def test_crossover_support_preserves_the_low_reciprocal_shape(self):
        start = 42.125
        source = [
            n(start, 1, 3, 0, 1, 0),
            n(start + 0.25, 0, 2, 0, 7, 1),
            n(start + 0.5, 1, 3, 2, 4, 2),
            n(start + 0.75, 0, 3, 0, 4, 3),
        ]
        marked, features = detect_features(source, [], 101)
        mark_crossover_support(marked, features, 101)
        self.assertEqual(features[-1]["noteIds"], [0, 2])
        self.assertEqual(marked[2]["intent"]["preferredCell"], [3, 0])
        self.assertTrue(marked[0]["intent"]["entry"])
        slow, features = detect_features(source, [], 60)
        mark_crossover_support(slow, features, 60)
        self.assertFalse(any(f["kind"] == "interleaved_crossover_support" for f in features))

    def test_rested_bomb_reentry_requires_clearance_and_an_alternating_arc_lead_in(self):
        ns = [
            n(7, 1, 2, 0, 1, 0),
            n(10, 1, 2, 0, 0, 1),
            n(10.5, 1, 2, 0, 1, 2),
            n(11, 1, 2, 0, 0, 3),
            {**n(11.5, 1, 2, 0, 1, 4), "linkedAnchor": True},
        ]
        clock = BeatClock(120)
        bomb = {"b": 8.5, "x": 2, "y": 0}
        cues = mark_bomb_reentries(copy.deepcopy(ns), [bomb], clock)
        self.assertEqual([(f["startBeat"], f["endBeat"]) for f in cues], [(10, 11.5)])
        self.assertEqual(mark_bomb_reentries(copy.deepcopy(ns), [{**bomb, "b": 9.75}], clock), [])
        repeated = copy.deepcopy(ns)
        repeated[-2]["d"] = 1
        self.assertEqual(mark_bomb_reentries(repeated, [bomb], clock), [])


if __name__ == "__main__":
    unittest.main()
