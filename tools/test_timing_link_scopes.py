import unittest

from meter_repeats import local_downbeats
from timing_link_scopes import compare_windows, editor_timing_links
from timing_sections import chart_timing_sections


class TimingLinkScopeTests(unittest.TestCase):
    motif = [0, .5, 1.25, 2, 2.5, 3.25, 4, 4.75, 5.5, 6.75, 7.25, 8, 8.75, 9.5, 10.5, 11.25]

    def chart(self, notes, bookmarks=None):
        raw = {} if bookmarks is None else {"_customData": {"_bookmarks": [
            {"_time": beat, "_name": name} for beat, name in bookmarks
        ]}}
        timings = dict(sorted(notes.items()))
        return {"raw": raw}, timings

    def test_different_bookmark_names_can_link_without_propagating_fills_or_local_edits(self):
        notes = {
            section + offset + beat: [0]
            for section in (0, 32, 64)
            for offset in (0, 16)
            for beat in self.motif
        }
        notes[34.125] = [0]
        notes[66] = [0, 1]
        level, timings = self.chart(notes, [(0, "Chorus 1"), (32, "check this"), (64, "Z")])
        preview = editor_timing_links(level, timings, 96)
        beats = list(timings)
        section_links = [{beats[index] for index in link} for link in preview["markerLinks"]]
        local_links = [{beats[index] for index in item["markers"]} for item in preview["subMarkerLinks"]]
        self.assertEqual([item["start"] for item in preview["sections"]], [0, 32, 64])
        self.assertIn({4, 36, 68}, section_links)
        self.assertIn({4, 20}, local_links)
        self.assertIn({36, 52}, local_links)
        self.assertFalse({34.125, 66} & set.union(*section_links, *local_links))

    def test_matching_names_do_not_link_different_rhythms(self):
        notes = {beat: [0] for beat in self.motif}
        notes.update({32.375 + beat * 1.5: [0] for beat in self.motif})
        level, timings = self.chart(notes, [(0, "Chorus 1"), (32, "Chorus 2")])
        preview = editor_timing_links(level, timings, 64)
        self.assertEqual(preview["markerLinks"], [])
        self.assertEqual(preview["subMarkerLinks"], [])

    def test_unmarked_chart_keeps_rhythm_only_sections_and_local_scopes(self):
        notes = {
            section + offset + beat: [0]
            for section in (0, 48)
            for offset in (0, 12)
            for beat in self.motif
        }
        level, timings = self.chart(notes)
        preview = editor_timing_links(level, timings, 80)
        sections, links = chart_timing_sections(level, timings, include_variations=True)
        self.assertEqual(preview["sections"], sections)
        self.assertEqual(preview["markerLinks"], links)
        self.assertTrue(preview["subMarkerLinks"])
        beats = list(timings)
        for item in preview["subMarkerLinks"]:
            scope = preview["subScopes"][item["scope"]]
            self.assertTrue(all(scope["start"] <= beats[index] < scope["end"] for index in item["markers"]))

    def test_long_unmarked_repeat_unites_fragments_and_excludes_fill_and_count_changes(self):
        phrase = [
            beat + offset
            for beat in range(64)
            if beat % 11 != 3
            for offset in ((0, .25) if (beat * beat + beat) % 13 < 5 else (0, .5) if beat % 7 < 2 else (0,))
        ]
        notes = {beat: [0] for beat in phrase}
        notes.update({112 + beat: [0, 1] if beat == 43 else [0] for beat in phrase if beat != 39})
        notes[163.75] = [0]
        level, timings = self.chart(notes)
        baseline = editor_timing_links(level, timings, 180, include_local=False)
        result = editor_timing_links(level, timings, 180, long_repeats=True, include_local=False)
        self.assertGreater(len(baseline["sections"]), len(result["sections"]))
        self.assertEqual([item["start"] for item in result["sections"]], [0, 112])
        self.assertTrue(all(item["end"] - item["start"] > 63 for item in result["sections"]))
        beats = list(timings)
        links = [{beats[i] for i in link} for link in result["markerLinks"]]
        self.assertIn({0, 112}, links)
        self.assertIn({63, 175}, links)
        self.assertFalse({43, 155, 163.75} & set.union(*links))
        unusable, _ = self.chart(notes, [(2, "Solo"), (3, "Chorus"), (4, "Chorus")])
        self.assertEqual(
            editor_timing_links(unusable, timings, 180, long_repeats=True, include_local=False), result
        )

    def test_long_search_does_not_link_a_uniform_stream(self):
        level, timings = self.chart({beat / 2: [0] for beat in range(300)})
        result = editor_timing_links(level, timings, 151, long_repeats=True, include_local=False)
        self.assertEqual(result, {"sections": [], "markerLinks": []})

    def test_downbeat_can_be_shifted_and_use_ternary_meter(self):
        notes = {
            1.25 + bar * 3 + offset: 2 if offset == 0 else 1
            for bar in range(10)
            for offset in (0, .5, 1.25, 2.5)
        }
        downbeats = local_downbeats(list(notes), list(notes.values()))
        for beat in (1.25, 4.25, 7.25, 10.25):
            self.assertIn(3, downbeats[beat])
        self.assertFalse(any(3 in periods for beat, periods in downbeats.items() if beat % 3 == 0))

    def test_extra_beat_at_phrase_ending_is_outside_verified_links(self):
        motif = [beat + 1.25 for beat in self.motif]
        extended = [*motif, 17.5]
        beats = [*extended, *(beat + 17 for beat in motif)]
        counts = [1] * len(beats)
        first, second = (1.25, 18.25), (18.25, 34.25)
        self.assertFalse(compare_windows(beats, counts, first, second, local=False))
        links = compare_windows(beats, counts, first, second, local=False, allow_extra_beat=True)
        self.assertEqual(len(links), len(motif))
        self.assertFalse(any(beats[i] == 17.5 for link in links for i in link))
        unrelated = [*extended, *(18.25 + index * .75 for index in range(len(motif)))]
        self.assertFalse(compare_windows(unrelated, counts, first, second, local=False, allow_extra_beat=True))
