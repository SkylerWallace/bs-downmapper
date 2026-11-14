import copy, unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from beatmap_io import (
    normalize,
    gameplay,
    legacy_grid_clock,
    linked_anchors,
    linked_endpoints,
    metadata,
    read_level,
    export,
    DifficultyNotFound,
)
from beat_clock import BeatClock
from audit_placements import check_group
from downmap import (
    regular_streams,
    section_plan,
    wall_conflicts,
    geometry,
    max_reach,
    conflicts_with_linked_swing,
    stack_placements,
    open_reset_repair_cells,
    preserves_reset_cues,
    PlacementDeadEnd,
    weighted_repair_report,
    geometry_context,
    simplify_high_crossed_links,
    parallel_double_directions,
)
from analyze_standard_spreads import load, groups, write
from experimental_pruning import prune_proposals

PROFILE = load(Path("profiles/expert.json"))


def n(b, c, x=1, y=0, d=1, id=0):
    return dict(b=b, c=c, x=x, y=y, d=d, a=0, id=id)


class UniversalPolicyTests(unittest.TestCase):
    def test_windup_penalty_keeps_a_legal_double_when_it_has_no_other_cell(self):
        notes = [n(10, 0, x=1, d=2, id=1), n(10, 1, x=3, d=1, id=2)]
        for note in notes:
            note["intent"] = {"preservePosition": True}
        level = {"notes": notes, "bpm": 120, "njs": 12, "offset": 0, "bombs": [], "walls": []}
        profile = {**PROFILE, "comfortableDoubles": True, "doubleWindupClearanceWeight": 12}
        placed, _, _ = geometry(notes, level, [], profile)
        self.assertEqual(placed, notes)

    def test_hard_releases_only_demanding_links_without_editing_hits(self):
        red = n(10, 0, x=1, y=0, d=2, id=1)
        high = n(10, 1, x=1, y=2, d=4, id=2)
        isolated = n(15, 1, x=1, y=2, d=4, id=3)
        notes = [red, high, isolated]
        chains = [
            {"b": a["b"], "c": a["c"], "x": a["x"], "y": a["y"]} for a in (high, isolated)
        ]
        level = {"notes": notes, "bpm": 120, "clock": BeatClock(120), "bombs": [],
                 "raw": {"version": "3.3.0", "burstSliders": chains}, "anchorIds": {2, 3}}
        self.assertEqual(parallel_double_directions(notes), {1: 2, 2: 2})
        self.assertEqual(parallel_double_directions([{**red, "d": 3}, high]), {})
        prepared = simplify_high_crossed_links(level, {"simplifyHighCrossedLinks": True})
        self.assertEqual(prepared["raw"]["burstSliders"], chains[1:])
        self.assertEqual(prepared["anchorIds"], {3})
        self.assertEqual(prepared["notes"], notes)

    def test_bounded_search_can_price_a_fixed_middle_row_without_changing_rhythm(self):
        notes = [n(1, 0, x=1, y=1, id=1), n(2, 1, x=3, y=0, id=2)]
        notes[0]["intent"] = {"preservePosition": True}
        level = {
            "notes": notes,
            "clock": BeatClock(120),
            "bpm": 120,
            "njs": 12,
            "offset": 0,
            "walls": [],
            "bombs": [],
        }
        with self.assertRaises(PlacementDeadEnd):
            geometry(notes, level, [], PROFILE)
        profile = {**PROFILE, "boundedRepairBeats": [1]}
        placed, _, _ = geometry(notes, level, [], profile)
        self.assertEqual([(x["b"], x["c"]) for x in placed], [(1, 0), (2, 1)])
        report = weighted_repair_report(placed, geometry_context(placed, level, profile), level)
        self.assertEqual([entry["rules"] for entry in report], [{"central_readability_window": 3.0}])

    def test_reset_chain_relocation_keeps_both_original_bombs(self):
        notes = [n(0, 1, x=3, id=1), n(2, 1, x=0, id=2), n(4, 1, x=3, id=3)]
        for note in notes:
            note["intent"] = {"kind": "bomb_return_reset", "preservePosition": True}
        bombs = [{"b": 1, "x": 3, "y": 0}, {"b": 3, "x": 0, "y": 0}]
        resets = [
            dict(hand=1, startBeat=0, endBeat=2, noteIds=[1, 2], bombIndices=[0]),
            dict(hand=1, startBeat=2, endBeat=4, noteIds=[2, 3], bombIndices=[1]),
        ]
        clock = BeatClock(120)
        self.assertEqual(open_reset_repair_cells(notes, resets, bombs, {2}), resets)
        self.assertIn([1, 0], notes[1]["intent"]["allowedCells"])
        self.assertNotIn([2, 0], notes[1]["intent"]["allowedCells"])
        notes[1]["x"] = 1
        self.assertTrue(preserves_reset_cues(notes, bombs, clock, resets))
        replacement = [bombs[0], {**bombs[1], "b": 5}, bombs[1]]
        self.assertFalse(preserves_reset_cues(notes, replacement, clock, resets))

    def test_stack_search_keeps_aligned_alternatives_and_rejects_cell_overlap(self):
        bottom = n(12, 0, x=1, y=0, id=1)
        aligned = n(12, 0, x=1, y=1, id=2)
        misaligned = n(12, 0, x=2, y=1, id=2)
        duplicate = n(12, 0, x=1, y=0, id=2)
        options = [[(0, bottom)], [(0, misaligned), (1, duplicate), (2, aligned)]]
        self.assertEqual(stack_placements(options), [((0, bottom), (2, aligned))])

    def test_stream_keep_rhythm_at_different_locations(self):
        for start in [7.125, 43, 201.5]:
            ns = [n(start + i * 0.25, 1 - i % 2, d=1 if i % 4 < 2 else 0, id=i) for i in range(16)]
            sections, streams = section_plan(ns, 140, PROFILE)
            out, decisions = prune_proposals(ns, sections, streams, PROFILE)
            self.assertEqual(len(out), 16)
            self.assertEqual(streams[0]["startBeat"], start)
            self.assertTrue(streams[0]["sourceDownDownEntry"])
            self.assertFalse(decisions[0]["accepted"])
            self.assertEqual(decisions[0]["consideredRhythmReductionFraction"], 0.5)

    def test_mild_stack_pruning_keeps_each_timing(self):
        ns = [n(i, 1, id=i) for i in range(10)] + [n(4, 1, y=1, id=20)]
        sections = [{"startBeat": 0, "endBeatExclusive": 10, "notes": 11}]
        out, decisions = prune_proposals(ns, sections, [], PROFILE)
        self.assertEqual(len(out), 10)
        self.assertEqual(set(groups(ns)), set(groups(out)))
        # Small section cannot lose half its emphasis to meet a global quota.
        tiny = [n(0, 1, id=1), n(0, 1, y=1, id=2)]
        out, _ = prune_proposals(tiny, [{"startBeat": 0, "endBeatExclusive": 1, "notes": 2}], [], PROFILE)
        self.assertEqual(len(out), 2)

    def test_wall_clear_time_extends_past_wall_end(self):
        wall = {"b": 2, "d": 0.25, "x": 1, "y": 0, "w": 1, "h": 5}
        note = n(2.25, 0)
        self.assertFalse(wall_conflicts(note, [wall]))
        self.assertTrue(wall_conflicts(note, [wall], 0.5))

    def test_crossover_horizontal_reversal_requires_relocation(self):
        original = n(10, 1, x=0, y=1, d=2)
        directed = {**original, "d": 3}
        level = {"notes": [original], "bombs": [], "walls": [], "bpm": 123, "njs": 14, "offset": 0}
        out, _, _ = geometry([directed], level, [], PROFILE)
        self.assertGreaterEqual(out[0]["x"], 2)
        self.assertLessEqual(max_reach(out[0]), max_reach(original) + 0.01)

    def test_linked_double_checks_static_swing_in_direction_search(self):
        red = n(12, 0, x=0, y=1, d=7, id=1)
        anchor = {**n(12, 1, x=1, y=0, d=2, id=2), "t": 6, "linkedAnchor": True}
        self.assertTrue(conflicts_with_linked_swing(red, 6, [anchor]))
        self.assertFalse(conflicts_with_linked_swing({**red, "d": 3}, 6, [anchor]))
        stack = {**red, "c": 1, "d": 1}
        self.assertTrue(conflicts_with_linked_swing(stack, 6, [anchor]))


class FormatTests(unittest.TestCase):
    def test_difficulty_lookup_reports_only_standard_charts(self):
        v4 = {
            "difficultyBeatmaps": [
                {"characteristic": "Standard", "difficulty": "ExpertPlus"},
                {"characteristic": "Legacy", "difficulty": "Expert"},
            ]
        }
        v2 = {
            "_difficultyBeatmapSets": [
                {"_beatmapCharacteristicName": "Legacy", "_difficultyBeatmaps": [{"_difficulty": "Expert"}]},
                {
                    "_beatmapCharacteristicName": "Standard",
                    "_difficultyBeatmaps": [{"_difficulty": "ExpertPlus"}],
                },
            ]
        }
        for info in (v4, v2):
            with self.assertRaisesRegex(
                DifficultyNotFound, r"Missing Standard Expert\. Available Standard difficulties: ExpertPlus\."
            ):
                metadata(info, "Expert")

    def test_native_v2_roundtrip_retains_lights_bombs_walls(self):
        raw = {
            "_version": "2.2.0",
            "_notes": [
                {"_time": 3, "_lineIndex": 0, "_lineLayer": 1, "_type": 0, "_cutDirection": 1},
                {"_time": 4, "_lineIndex": 1, "_lineLayer": 2, "_type": 3, "_cutDirection": 8},
            ],
            "_obstacles": [{"_time": 5, "_lineIndex": 2, "_type": 0, "_duration": 0.25, "_width": 1}],
            "_events": [{"_time": 0, "_type": 1, "_value": 5}],
        }
        ns, bombs, walls, _ = normalize(raw)
        ns[0]["x"] = 1
        out = gameplay(raw, ns)
        self.assertEqual(out["_events"], raw["_events"])
        self.assertEqual(out["_obstacles"], raw["_obstacles"])
        self.assertEqual([n for n in out["_notes"] if n["_type"] == 3], [raw["_notes"][1]])
        self.assertEqual(normalize(out)[0][0]["x"], 1)
        self.assertEqual(raw["_notes"][0]["_lineIndex"], 0)

    def test_v3_and_v4_normalize_same_geometry(self):
        raw3 = {"version": "3.3.0", "colorNotes": [dict(b=2, x=0, y=1, c=0, d=6, a=0)]}
        raw4 = {"version": "4.0.0", "colorNotes": [{"b": 2}], "colorNotesData": [dict(x=0, y=1, c=0, d=6)]}
        self.assertEqual(normalize(raw3)[0], normalize(raw4)[0])

    def test_tempo_clock_integrates_across_region_boundary(self):
        audio = {
            "songFrequency": 1000,
            "bpmData": [{"sb": 0, "eb": 4, "si": 0, "ei": 2000}, {"sb": 4, "eb": 8, "si": 2000, "ei": 6000}],
        }
        for clock in (BeatClock(120, audio), BeatClock(120, events=[{"b": 4, "m": 60}])):
            self.assertEqual(clock.seconds(4), 2)
            self.assertEqual(clock.gap(3, 5), 1.5)

    def test_legacy_grid_keeps_stored_object_times_and_tracks_editor_beats(self):
        raw = {
            "_version": "2.0.0",
            "_notes": [{"_time": 10, "_lineIndex": 0, "_lineLayer": 0, "_type": 0, "_cutDirection": 1}],
            "_obstacles": [],
            "_customData": {"_BPMChanges": [{"_time": 4, "_BPM": 240}, {"_time": 8, "_BPM": 60}]},
        }
        clock = legacy_grid_clock(raw, 120)
        notes, _, _, _ = normalize(raw)
        self.assertEqual(clock.beat_at_seconds(BeatClock(120).seconds(notes[0]["b"])), 13)
        self.assertEqual(gameplay(raw, notes), raw)

    def test_tempo_clock_joins_small_sample_offsets_and_skips_an_inverted_clip_trim(self):
        audio = {
            "songFrequency": 1000,
            "songSampleCount": 3500,
            "bpmData": [
                {"sb": 0, "eb": 4, "si": 0, "ei": 2000},
                {"sb": 4, "eb": 8, "si": 2008, "ei": 4008},
                {"sb": 8, "eb": 7, "si": 4009, "ei": 3500},
            ],
        }
        clock = BeatClock(120, audio)
        self.assertEqual(clock.starts, [0, 4])
        self.assertAlmostEqual(clock.seconds(4), 2.008)
        self.assertAlmostEqual(clock.gap(3, 5), 1.002)
        audio["bpmData"][-1]["ei"] = 3400
        with self.assertRaisesRegex(ValueError, "positive and contiguous"):
            BeatClock(120, audio)
        audio["bpmData"].pop()
        audio["bpmData"][1]["si"] = 2020
        with self.assertRaisesRegex(ValueError, "positive and contiguous"):
            BeatClock(120, audio)

    def test_v4_variable_njs_is_readable_and_only_cleared_for_constant_export(self):
        with TemporaryDirectory() as temp:
            song = Path(temp) / "song"
            info = {
                "audio": {"bpm": 120, "songFilename": "song.ogg", "audioDataFilename": "AudioData.dat"},
                "coverImageFilename": "cover.png",
                "songPreviewFilename": "song.ogg",
                "difficultyBeatmaps": [
                    {
                        "characteristic": "Standard",
                        "difficulty": "ExpertPlus",
                        "beatmapDataFilename": "chart.dat",
                        "lightshowDataFilename": "lights.dat",
                        "noteJumpMovementSpeed": 0,
                    }
                ],
            }
            raw = {
                "version": "4.1.0",
                "colorNotes": [{"b": 2}, {"b": 4, "i": 1}],
                "colorNotesData": [{"x": 1, "d": 1}, {"x": 1, "d": 0}],
                "njsEvents": [{"b": 2}],
                "njsEventData": [{"d": 2}],
                "arcs": [{"hb": 2, "tb": 4, "ti": 1, "tr": 2}],
                "arcsData": [{}],
            }
            write(song / "Info.dat", info)
            write(song / "chart.dat", raw)
            write(
                song / "AudioData.dat",
                {"songFrequency": 1000, "bpmData": [{"sb": 0, "eb": 8, "si": 0, "ei": 4000}]},
            )
            for asset in ("song.ogg", "cover.png", "lights.dat"):
                (song / asset).touch()
            level = read_level(song)
            self.assertEqual(level["njs"], 16)
            self.assertEqual(level["anchorIds"], {0})
            profile = {"njsMultiplier": 0.625, "jumpOffsetAddition": 1}
            timing = load(export(level, level["notes"], Path(temp) / "timing", profile))
            self.assertEqual(timing["njsEvents"], raw["njsEvents"])
            out = Path(temp) / "out"
            candidate = load(export(level, level["notes"], out, profile, "Easy", constant_njs=True))
            self.assertEqual(candidate["njsEvents"], [])
            self.assertEqual(candidate["njsEventData"], [])
            self.assertEqual(candidate["arcs"], raw["arcs"])
            self.assertEqual(read_level(out, "Easy")["njs"], 10)
            self.assertEqual(load(song / "chart.dat"), raw)

    def test_v4_arc_and_chain_metadata_survives_reindexing(self):
        raw = {
            "version": "4.0.0",
            "colorNotes": [{"b": 2}, {"b": 3}, {"b": 4, "i": 1}],
            "colorNotesData": [{"x": 1, "d": 6, "a": 45}, {"x": 0, "d": 1}, {"x": 1, "d": 6, "a": 45}],
            "arcs": [{"hb": 2, "tb": 4, "ti": 1}],
            "arcsData": [{}],
            "chains": [{"hb": 4, "tb": 4.25, "i": 1}],
            "chainsData": [{}],
        }
        ns, _, _, _ = normalize(raw)
        ns[1]["x"] = 2
        out = gameplay(raw, ns)
        endpoints = list(linked_endpoints(raw))
        self.assertEqual(linked_anchors(ns, endpoints), {0, 2})
        self.assertEqual(list(linked_endpoints(out)), endpoints)
        self.assertEqual(out["colorNotesData"][:3], raw["colorNotesData"])
        self.assertEqual(out["colorNotes"][0]["i"], 0)
        self.assertEqual(normalize(out)[0], ns)

    def test_angle_offset_equivalent_directions_share_a_swing(self):
        red = n(4, 0, x=1, y=0, d=6)
        # Down-left rotated +45 is down, so the second cut must be vertically stacked.
        same = {**red, "y": 1, "d": 1}
        self.assertEqual(check_group([{**red, "a": 45}, same]), [])

    def test_v3_embedded_arcs_chains_and_tempo_survive_note_changes(self):
        raw = {
            "version": "3.3.0",
            "colorNotes": [{"b": 2, "x": 1, "d": 1}, {"b": 3, "x": 1, "d": 0}],
            "sliders": [{"b": 2, "c": 0, "x": 1, "y": 0, "tb": 4, "tx": 2, "ty": 1}],
            "burstSliders": [{"b": 2, "c": 0, "x": 1, "y": 0, "tb": 2.25}],
            "bpmEvents": [{"b": 4, "m": 75}],
        }
        ns, _, _, _ = normalize(raw)
        self.assertEqual(linked_anchors(ns, list(linked_endpoints(raw))), {0})
        ns[1]["x"] = 2
        out = gameplay(raw, ns)
        for key in ("sliders", "burstSliders", "bpmEvents"):
            self.assertEqual(out[key], raw[key])

    def test_unsupported_gameplay_is_explicit(self):
        for raw in [
            {"version": "3.3.0", "colorNotes": [], "rotationEvents": [{}]},
            {"_version": "2.2.0", "_notes": [], "_events": [{"_type": 100}]},
        ]:
            with self.assertRaises(ValueError):
                normalize(raw)


if __name__ == "__main__":
    unittest.main()
