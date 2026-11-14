import unittest

from beat_clock import BeatClock
from analyze_standard_spreads import groups
from rhythm_inputs import (
    apply_merges,
    best_guess_notes,
    easy_accent_grid,
    easy_phrase_cue_weights,
    edited_notes,
    fit_easy_note_budget,
    merge_nearby_singles,
    redistribute_easy_phrase_budget,
    refine_easy_pulse,
    repair_conflicting_links,
    retain_timing_links,
    simplify_easy_links,
    swap_easy_phrase_singles,
    timeline,
    timing_link_context,
)


class RhythmInputTests(unittest.TestCase):
    def setUp(self):
        self.level = {
            "clock": BeatClock(120, events=[{"b": 2, "m": 60}]),
            "audioData": None,
            "notes": [
                {"b": i / 2, "c": i % 2, "x": 1 + i % 2, "y": 0, "d": i % 2, "a": 0, "id": i}
                for i in range(9)
            ],
            "anchorIds": {4},
            "linkedEndpoints": [(2, 0, 1, 0), (2.25, 1, 2, 0)],
        }

    def test_hard_budget_uses_reference_density_without_reintroducing_hits(self):
        reference = [dict(b=i, c=i % 2, id=i, x=1, y=0) for i in range(12)]
        eligible = reference[1:-3]
        level = {**self.level, "notes": eligible, "anchorIds": set()}
        sections = [{"startBeat": 0, "endBeatExclusive": 12}]
        profile = {"minimumSourceNoteRatio": 0.6, "maximumSourceNoteRatio": 0.7}
        self.assertEqual(len(best_guess_notes(level, sections, [], profile)), 5)
        result = best_guess_notes(level, sections, [], profile, budget_reference={"notes": reference})
        self.assertEqual(result, eligible)

    def test_beginner_links_keep_compact_gestures_only_when_attacks_survive(self):
        notes = [
            dict(b=i, c=0, x=x, y=y, id=i) for i, (x, y) in enumerate(((0, 0), (1, 0), (0, 0), (3, 2)))
        ]
        short = dict(b=0, c=0, x=0, y=0, tb=1, tx=1, ty=0)
        long = dict(b=2, c=0, x=0, y=0, tb=3, tx=3, ty=2)
        raw = dict(version="3.3.0", sliders=[short, long])
        level = dict(self.level, notes=notes, raw=raw)
        prepared = timing_link_context(level, set(), maximum_reach=1.4)
        self.assertEqual(prepared["linkAccentBeats"], {0, 1, 2, 3})
        self.assertEqual(prepared["anchorIds"], set())
        self.assertEqual(retain_timing_links(prepared, notes)["raw"]["sliders"], [short])
        self.assertEqual(retain_timing_links(prepared, notes[1:])["raw"]["sliders"], [])
        self.assertEqual(retain_timing_links(prepared, [dict(notes[0], b=0.5), *notes[1:]])["simplifiedLinkCount"], 2)
        self.assertEqual(raw["sliders"], [short, long])

    def test_beginner_chain_head_is_optional_except_for_bomb_cues(self):
        notes = [dict(b=1, c=0, x=1, y=0, id=0), dict(b=2, c=1, x=3, y=2, id=1)]
        compact = dict(hb=1, tb=1.25, i=0)
        cue = dict(hb=2, tb=2.25, i=1, ci=1)
        raw = dict(version="4.0.0", colorNotesData=notes, chains=[compact, cue],
                   chainsData=[dict(tx=2), dict(tx=0, ty=0)])
        level = dict(self.level, notes=notes, raw=raw)
        prepared = timing_link_context(level, {1}, maximum_reach=1.4)
        self.assertEqual(prepared["anchorIds"], {1})
        self.assertEqual(retain_timing_links(prepared, notes[1:])["raw"]["chains"], [cue])
        self.assertEqual(timing_link_context(level, set(), maximum_reach=1.4)["raw"]["chains"], [compact])
        self.assertEqual(timing_link_context(level, set())["raw"]["chains"], [compact, cue])

    def test_dense_all_chain_phrase_can_be_pruned(self):
        notes = [dict(b=i / 4, c=i % 2, x=1 + i % 2, y=0, id=i) for i in range(16)]
        chains = [dict(b=n["b"], c=n["c"], x=n["x"], y=0, tb=n["b"] + 0.1, tx=n["x"], ty=0) for n in notes]
        level = dict(self.level, notes=notes, raw=dict(version="3.3.0", burstSliders=chains))
        prepared = timing_link_context(level, set())
        profile = {"minimumSourceNoteRatio": 0.6, "maximumSourceNoteRatio": 0.7}
        sections = [{"startBeat": 0, "endBeatExclusive": 4}]
        retained = best_guess_notes(prepared, sections, [], profile)
        self.assertEqual(len(retained), 10)
        self.assertEqual(len(retain_timing_links(prepared, retained)["raw"]["burstSliders"]), 10)

    def test_easy_budget_adds_only_eligible_hits_with_enough_space(self):
        notes = [dict(b=i / 2, c=i % 2, id=i, x=1, y=0) for i in range(13)]
        level = {**self.level, "notes": notes, "clock": BeatClock(120), "anchorIds": {0}}
        retained = notes[::4]
        result = fit_easy_note_budget(level, retained, 8, set(), 0.34, 0.74)
        # Every whole beat fits, but there is no safe eighth single or double.
        self.assertEqual(result, notes[::2])
        self.assertTrue(all(note in result for note in retained))

    def test_recurring_offbeat_handoff_scores_above_alternating_filler(self):
        for start in (3.125, 22.5):
            phrase = [(0, 0), (0.25, 0), (0.5, 0), (0.75, 1), (1.5, 1)]
            timings = [
                (start + period + beat, [{"c": hand}]) for period in (0, 2, 4) for beat, hand in phrase
            ]
            weights = easy_phrase_cue_weights(timings, [0])
            self.assertTrue(all(weights[i] > 0.5 for i in (3, 8, 13)))
            self.assertTrue(all(i not in weights for i in (1, 2, 6, 7, 11, 12)))
        stream = [(i * 0.25, [{"c": i % 2}]) for i in range(24)]
        self.assertEqual(easy_phrase_cue_weights(stream, [0]), {})

    def test_phrase_swaps_keep_counts_anchors_and_a_stable_pulse(self):
        beats = [0, 1.5, 2, 4, 4.00004, 12, 13, 14, 15, 16, 17, 18]
        notes = [dict(b=b, c=i % 2, id=i, x=1 + i % 2, y=0, d=1) for i, b in enumerate(beats)]
        timings = list(groups(notes).items())
        retained = [notes[i] for i in (0, 1, 4, 5, 7, 9, 11)]
        times = [b / 2 for b, _ in timings]
        swapped = swap_easy_phrase_singles(timings, times, [0, 4], retained, {0, 3, 4, 10}, 0.34, 1, {})
        self.assertEqual([n["b"] for n in swapped], [0, 2, 4.00004, 12, 14, 16, 18])
        self.assertEqual([len([n for n in seq if n["b"] < 8]) for seq in (retained, swapped)], [3, 3])
        self.assertIn(notes[4], swapped)

    def test_phrase_swaps_keep_a_whole_beat_ahead_of_repeated_filler(self):
        offsets = (0, 0.5, 1, 1.5, 1.75, 2, 2.5)
        beats = [bar + b for bar in range(0, 24, 4) for b in offsets]
        notes = [dict(b=b, c=0, id=i, x=1, y=0, d=1) for i, b in enumerate(beats)]
        anchors = {i for i in range(len(beats)) if i % len(offsets) in (0, 6)}
        retained = [n for n in notes if n["id"] in anchors or n["b"] % 4 == 1]
        times = [b * 60 / 105 for b in beats]
        swapped = swap_easy_phrase_singles(
            list(groups(notes).items()), times, [0], retained, anchors, 0.34, 0.74, {}
        )
        self.assertEqual(swapped, retained)

    def test_audio_ranks_existing_source_timings_at_fixed_count(self):
        notes = [dict(b=b, c=0, id=b, x=1, y=0, d=1) for b in range(4)]
        timings = list(groups(notes).items())
        retained = [notes[0], notes[2], notes[3]]
        args = (timings, [0, 0.5, 1, 1.5], [0], retained, {0, 3}, 0.34, 0.74, {})
        self.assertEqual(swap_easy_phrase_singles(*args), retained)
        self.assertEqual(
            swap_easy_phrase_singles(*args, onset_scores={0: 0, 1: 1, 2: 0, 3: 0}),
            [notes[0], notes[1], notes[3]],
        )

    def test_local_pulse_keeps_source_microtiming_and_existing_merge(self):
        beats = (0, 1.500123, 2.038777, 3, 5, 5.25, 7)
        notes = [dict(b=b, c=i % 2, id=i, x=1 + i % 2, y=0, d=1) for i, b in enumerate(beats)]
        level = dict(
            notes=notes,
            clock=BeatClock(105),
            anchorIds=set(),
            rankScores=dict.fromkeys(groups(notes), 0.5),
            onsetScores=dict.fromkeys(groups(notes), 0.5),
        )
        retained = [note for note in notes if note["id"] != 2]
        merges = [{"noteId": 5, "targetId": 4}]
        refined = refine_easy_pulse(level, retained, merges, {3}, 0.34, 0.74)
        self.assertEqual(refined, [note for note in notes if note["id"] != 1])
        self.assertEqual([n["b"] for n in apply_merges(level, refined, merges)], [0, 2.038777, 3, 5, 5, 7])

    def test_swung_riff_can_shift_an_established_even_pulse_without_changing_count(self):
        offsets = (0, 0.6875, 1, 1.6875, 2, 2.6875)
        beats = [bar + b for bar in range(0, 32, 4) for b in offsets] + [32]
        notes = [dict(b=b, c=0, id=i, x=1, y=0, d=1) for i, b in enumerate(beats)]
        timings = list(groups(notes).items())
        for beat, group in timings:
            if beat % 4 == 2.6875:
                group.append(dict(b=beat, c=1, id=len(beats) + group[0]["id"], x=2, y=0, d=1))
        retained = [n for n in notes if n["b"] % 2 == 0]
        times = [b * 60 / 162 for b in beats]
        swapped = swap_easy_phrase_singles(timings, times, [0], retained, {0, len(beats) - 1}, 0.34, 0.74, {})
        self.assertEqual(len(swapped), len(retained))
        self.assertEqual([n["b"] for n in swapped if 8 <= n["b"] < 12], [9, 10.6875])
        self.assertEqual((swapped[0], swapped[-1]), (retained[0], retained[-1]))

    def test_three_beat_chord_phase_needs_a_distinct_repetition(self):
        beats = [i / 2 for i in range(61)]
        times = [b / 3 for b in beats]
        for period in (1, 3):
            timings = [(b, [{"c": 0}, {"c": 1}] if (b - 1) % period == 0 else [{"c": 0}]) for b in beats]
            accents = easy_accent_grid(timings, beats, times, [0], 0.74)
            if period == 1:
                self.assertFalse(accents)
            else:
                self.assertIn(14, accents)
                self.assertNotIn(12, accents)

    def test_budget_transfer_preserves_total_anchors_and_minimum_gap(self):
        beats = list(range(9)) + [12 + i / 4 for i in range(33)]
        notes = [dict(b=b, c=0, id=i, x=1, y=0, d=1) for i, b in enumerate(beats)]
        timings = list(groups(notes).items())
        for i in (2, 4):
            timings[i][1].append(dict(b=beats[i], c=1, id=len(beats) + i, x=2, y=1, d=1))
        kept = {i: group[:] for i, (b, group) in enumerate(timings) if b % 2 == 0}
        fixed = kept[2][:]
        count = sum(map(len, kept.values()))
        times = [b / 2 for b in beats]
        costs = dict.fromkeys(range(len(beats)), 0)
        costs[beats.index(12.25)] = -10  # Strong accent with too little recovery.
        costs[beats.index(13)] = -1
        redistribute_easy_phrase_budget(timings, times, [0, 9], kept, {0, 2, 8, 9, 41}, costs, 0.34, 0.74)
        self.assertEqual(sum(map(len, kept.values())), count)
        self.assertEqual(kept[2], fixed)
        self.assertEqual(len(kept[4]), 1)
        self.assertIn(beats.index(13), kept)
        self.assertNotIn(beats.index(12.25), kept)

    def test_edit_keeps_other_hits_and_uses_a_distinct_cell_from_a_standalone_endpoint(self):
        notes = edited_notes(self.level, {"removed": [0.5], "added": [{"beat": 2.25, "hand": 1}]})
        self.assertEqual([n["b"] for n in notes], [0, 1, 1.5, 2, 2.25, 2.5, 3, 3.5, 4])
        added = next(n for n in notes if n["b"] == 2.25)
        self.assertEqual(added["c"], 1)
        self.assertNotEqual((added["x"], added["y"]), (2, 0))
        self.assertIn(self.level["notes"][4], notes)
        self.assertEqual(timeline(self.level)["regions"][1], {"beat": 2, "seconds": 1, "secondsPerBeat": 1})

    def test_cannot_delete_linked_timing(self):
        with self.assertRaisesRegex(ValueError, "arc or chain"):
            edited_notes(self.level, {"removed": [2]})

    def test_timing_preview_includes_both_hands_for_a_double(self):
        self.level["notes"].append({"b": 2, "c": 1, "id": 9})
        marker = next(m for m in timeline(self.level)["timings"] if m["beat"] == 2)
        self.assertEqual(marker["hands"], [0, 1])
        self.assertEqual(marker["noteIds"], [4, 9])

    def test_review_can_restore_a_pruned_note_at_its_original_beat_and_hand(self):
        other = {"b": 2, "c": 1, "x": 2, "y": 0, "d": 0, "a": 0, "id": 9}
        self.level["notes"].append(other)
        preview = edited_notes(self.level, {"removedNoteIds": [1, 9]})
        self.assertEqual([n["id"] for n in preview if n["b"] == 2], [4])
        reviewed = edited_notes(self.level, {"removedNoteIds": [1]})
        self.assertEqual([n["id"] for n in reviewed if n["b"] == 2], [4, 9])
        self.assertIn(other, reviewed)
        self.assertFalse(any(n["id"] == 1 for n in reviewed))
        with self.assertRaisesRegex(ValueError, "arc or chain"):
            edited_notes(self.level, {"removedNoteIds": [4]})

    def test_best_guess_thins_existing_hits_and_preserves_anchor(self):
        notes = best_guess_notes(
            self.level,
            [{"startBeat": 0, "endBeatExclusive": 5}],
            [],
            {"minimumSourceNoteRatio": 0.4, "maximumSourceNoteRatio": 0.6},
        )
        self.assertLess(len(notes), len(self.level["notes"]))
        self.assertIn(self.level["notes"][4], notes)
        self.assertTrue(all(n in self.level["notes"] for n in notes))

    def test_isolated_offbeat_can_merge_and_be_restored_independently(self):
        first = {"id": 0, "b": 3.75, "c": 0, "x": 0, "y": 0, "d": 1}
        accent = {"id": 1, "b": 4, "c": 1, "x": 3, "y": 0, "d": 1}
        notes = [first, accent]
        level = dict(
            self.level,
            clock=BeatClock(140),
            notes=notes,
            anchorIds=set(),
            linkedEndpoints=[],
            bombs=[],
            walls=[],
        )
        sections = [{"startBeat": 0, "endBeatExclusive": 5}]
        merges = merge_nearby_singles(level, notes, sections, [], [])
        self.assertEqual(merges, [{"noteId": 0, "targetId": 1}])
        self.assertEqual(
            [(n["b"], n["c"]) for n in edited_notes(level, {"merges": merges})], [(4, 0), (4, 1)]
        )
        self.assertEqual(edited_notes(level, {"merges": []}), notes)
        self.assertEqual(merge_nearby_singles(level, notes, sections, [{"noteIds": [0, 1]}], []), [])
        self.assertEqual(merge_nearby_singles(dict(level, bombs=[{"b": 4}]), notes, sections, [], []), [])
        self.assertEqual(merge_nearby_singles(dict(level, clock=BeatClock(100)), notes, sections, [], []), [])
        with self.assertRaisesRegex(ValueError, "available"):
            edited_notes(level, {"merges": merges, "removedNoteIds": [0]})

    def test_easy_keeps_the_pulse_and_response_of_a_repeated_half_beat_run(self):
        beats = [(8 + i / 2, 0) for i in range(7)] + [(11.5, 1), (12.5, 1)]
        notes = [dict(b=beat, c=hand, id=i, x=1 + hand, y=0, d=i % 2) for i, (beat, hand) in enumerate(beats)]
        level = dict(self.level, notes=notes, clock=BeatClock(127), anchorIds=set())
        profile = dict(automaticTimingPulseSeconds=0.74, automaticTimingMinimumGapSeconds=0.34)
        retained = best_guess_notes(level, [], [], profile)
        self.assertEqual([n["b"] for n in retained], [8, 9, 10, 11.5, 12.5])

    def test_easy_spreads_hits_through_a_stream_and_keeps_the_source_rest(self):
        beats = [i / 4 for i in range(65)] + [21, 22, 23]
        notes = [dict(b=beat, c=i % 2, id=i, x=1 + i % 2, y=0, d=1) for i, beat in enumerate(beats)]
        level = dict(self.level, notes=notes, clock=BeatClock(140), anchorIds=set())
        profile = dict(automaticTimingPulseSeconds=0.74, automaticTimingMinimumGapSeconds=0.34)
        retained = best_guess_notes(level, [], [], profile)
        times = [n["b"] for n in retained]
        self.assertIn(16, times)
        self.assertIn(21, times)
        self.assertTrue(all(0.75 <= b - a <= 2 for a, b in zip(times, times[1:]) if b <= 16))

    def test_easy_keeps_close_linked_hits_without_keeping_nearby_filler(self):
        beats = [0, 0.5, 1, 1.25, 1.5, 1.75, 2, 2.5, 3]
        notes = [dict(b=beat, c=i % 2, id=i, x=1 + i % 2, y=0, d=1) for i, beat in enumerate(beats)]
        level = dict(self.level, notes=notes, clock=BeatClock(140), anchorIds={2, 4})
        profile = dict(automaticTimingPulseSeconds=0.74, automaticTimingMinimumGapSeconds=0.34)
        retained = {n["id"] for n in best_guess_notes(level, [], [], profile)}
        self.assertTrue({2, 4} <= retained)
        self.assertTrue({1, 3, 5, 6}.isdisjoint(retained))

    def test_easy_leaves_room_for_sustained_wall_cues(self):
        notes = [dict(b=beat, c=i % 2, id=i, x=1 + i % 2, y=0, d=1) for i, beat in enumerate(range(24))]
        walls = [dict(b=beat, d=6) for beat in (0, 8, 16)]
        level = dict(self.level, notes=notes, walls=walls, clock=BeatClock(140), anchorIds=set())
        profile = dict(automaticTimingPulseSeconds=0.74, automaticTimingMinimumGapSeconds=0.34)
        self.assertEqual([n["b"] for n in best_guess_notes(level, [], [], profile)], [0, 8, 16])
        short_walls = dict(level, walls=[dict(w, d=0.5) for w in walls])
        self.assertGreater(len(best_guess_notes(short_walls, [], [], profile)), 3)

    def test_easy_leaves_a_gap_inside_repeated_bomb_fills(self):
        notes = [dict(b=i / 2, c=i % 2, id=i, x=1 + i % 2, y=0, d=1) for i in range(33)]
        bombs = [dict(b=start + offset) for start in (4, 12) for offset in (0.25, 0.375, 0.5)]
        level = dict(self.level, notes=notes, bombs=bombs, clock=BeatClock(140), anchorIds=set())
        profile = dict(automaticTimingPulseSeconds=0.74, automaticTimingMinimumGapSeconds=0.34)
        beats = {n["b"] for n in best_guess_notes(level, [], [], profile)}
        self.assertTrue({2, 6, 10, 14} <= beats)
        self.assertFalse(any(2 < b < 6 or 10 < b < 14 for b in beats))

    def test_easy_releases_only_the_anchors_of_redundant_links(self):
        raw = {
            "version": "4.0.0",
            "colorNotesData": [{"c": 0, "x": 1}, {"c": 1, "x": 2}],
            "arcs": [{"hb": 2, "tb": 3, "hi": 0, "ti": 0}, {"hb": 2.25, "tb": 3.25, "hi": 1, "ti": 1}],
            "chains": [{"hb": 4.83333, "tb": 4.85, "i": 0}, {"hb": 5.16667, "tb": 5.18, "i": 0}],
        }
        beats = [(2, 0), (2.25, 1), (3, 0), (3.25, 1), (4.83333, 0), (5.16667, 0)]
        notes = [dict(b=b, c=c, id=i, x=1 + c, y=0, d=1) for i, (b, c) in enumerate(beats)]
        level = dict(self.level, raw=raw, notes=notes, clock=BeatClock(120), anchorIds=set(range(6)))
        result = simplify_easy_links(level, 0.34, set())
        self.assertEqual(result["raw"]["arcs"], raw["arcs"][:1])
        self.assertEqual(result["raw"]["chains"], raw["chains"][1:])
        self.assertEqual(result["anchorIds"], {0, 2, 5})
        self.assertEqual(len(level["raw"]["arcs"]), 2)
        self.assertEqual(len(simplify_easy_links(level, 0.34, {1})["raw"]["arcs"]), 2)

    def test_joint_link_repair_keeps_notes_and_preserves_unrelated_links(self):
        notes = [
            dict(b=5, c=0, x=1, y=0, d=0, a=0, id=0),
            dict(b=5, c=0, x=2, y=2, d=0, a=0, id=1),
            dict(b=9, c=1, x=2, y=0, d=1, a=0, id=2),
        ]
        chains = [dict(hb=n["b"], tb=n["b"] + 0.25, i=n["id"]) for n in notes]
        raw = dict(version="4.0.0", colorNotesData=notes, chains=chains)
        level = dict(self.level, raw=raw, notes=notes)
        repaired, changes = repair_conflicting_links(level)
        self.assertEqual(repaired["notes"], notes)
        self.assertEqual(repaired["raw"]["chains"], [chains[0], chains[2]])
        self.assertEqual(repaired["anchorIds"], {0, 2})
        self.assertEqual(len(changes[0]["removedLinks"]), 1)
        alternate, _ = repair_conflicting_links(level, {5: 1})
        self.assertEqual(alternate["raw"]["chains"], [chains[1], chains[2]])
        self.assertEqual(level["raw"]["chains"], chains)

    def test_visibility_repair_can_free_either_anchor_without_changing_notes(self):
        notes = [
            dict(b=b, c=c, x=1, y=2, d=0, a=0, id=i) for i, (b, c) in enumerate(((4, 0), (4.5, 1), (9, 0)))
        ]
        chains = [dict(hb=n["b"], tb=n["b"] + 0.25, i=n["id"]) for n in notes]
        level = dict(self.level, notes=notes, raw=dict(version="4.0.0", colorNotesData=notes, chains=chains))
        for choice, kept in ((0, 1), (1, 0)):
            repaired, changes = repair_conflicting_links(level, visibility_choices={1: ([0], choice)})
            self.assertEqual(repaired["notes"], notes)
            self.assertEqual(repaired["raw"]["chains"], [chains[kept], chains[2]])
            self.assertEqual(repaired["anchorIds"], {kept, 2})
            self.assertEqual(changes[0]["releasedForVisibility"], 1)
        self.assertEqual(level["raw"]["chains"], chains)
