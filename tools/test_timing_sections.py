import unittest

from timing_sections import extend_repeat_groups, find_timing_sections, matching_markers


class TimingSectionTests(unittest.TestCase):
    phrase = [0, .5, 1.25, 2, 2.5, 3.25, 4, 4.75, 5.5, 6.75, 7.25, 8, 8.75, 9.5, 10.5, 11.25]

    def test_shared_hits_link_across_three_occurrences_but_fills_stay_independent(self):
        first = [(beat, 1) for beat in self.phrase]
        second = [(20 + beat, 1) for beat in self.phrase if beat != 9.5]
        second.extend([(29.25, 1), (29.75, 1)])
        third = [(42 + beat, 2 if beat == 8.75 else 1) for beat in self.phrase]
        markers = [{"beat": beat, "count": count} for beat, count in sorted(first + second + third)]
        sections, groups = find_timing_sections(markers)
        self.assertEqual([section["start"] for section in sections], [0, 20, 42])
        self.assertEqual({section["group"] for section in sections}, {0})
        links = [set(markers[i]["beat"] for i in group) for group in groups]
        self.assertIn({4.75, 24.75, 46.75}, links)
        self.assertIn({9.5, 51.5}, links)
        self.assertIn({8.75, 28.75}, links)
        self.assertFalse({29.25, 29.75, 50.75} & set.union(*links))
        flat = [i for group in groups for i in group]
        self.assertEqual(len(flat), len(set(flat)))

    def test_uniform_stream_does_not_define_distinct_sections(self):
        markers = [{"beat": index / 2, "count": 1} for index in range(100)]
        self.assertEqual(find_timing_sections(markers), ([], []))
        self.assertEqual(find_timing_sections(markers, include_variations=True), ([], []))

    def test_variant_sections_leave_count_changes_and_fills_independent(self):
        phrase = [*self.phrase, 12, 13, 14.5, 15]
        first = [(beat, 1) for beat in phrase]
        second = [(40 + beat, 2 if index % 4 == 1 else 1)
                  for index, beat in enumerate(phrase) if index not in {10, 16}]
        markers = [{"beat": beat, "count": count}
                   for beat, count in sorted(first + second + [(47.5, 1), (52.5, 1)])]
        self.assertEqual(find_timing_sections(markers), ([], []))
        sections, groups = find_timing_sections(markers, include_variations=True)
        self.assertEqual([section["start"] for section in sections], [0, 40])
        links = [{markers[index]["beat"] for index in group} for group in groups]
        self.assertIn({4, 44}, links)
        self.assertFalse({.5, 40.5, 47.5, 52.5} & set.union(*links))

    def test_short_anchors_find_repeats_when_fills_break_long_fingerprints(self):
        phrase = [0, 1.25, 2.5, 3, 4, 5.25, 6.5, 7.5, 8.75, 9.75, 10.5, 11.25,
                  12.25, 13, 13.5, 14.5, 15.25, 16.25, 16.75, 17.25, 18.25, 19.5, 20, 21]
        fills = {40 + (phrase[i] + phrase[i + 1]) / 2 for i in range(2, len(phrase) - 1, 4)}
        markers = [{"beat": beat, "count": 1}
                   for beat in sorted(phrase + [40 + beat for beat in phrase] + list(fills))]
        self.assertEqual(find_timing_sections(markers), ([], []))
        sections, groups = find_timing_sections(markers, include_variations=True)
        self.assertEqual([section["start"] for section in sections], [0, 40])
        self.assertEqual(len(groups), len(phrase))
        self.assertFalse(fills & {markers[index]["beat"] for group in groups for index in group})

    def test_close_fill_does_not_steal_an_aligned_hit(self):
        beats = [1, 20.97, 21]
        self.assertEqual(matching_markers(beats, [1, 1, 1], 20), [(0, 2)])
        self.assertEqual(matching_markers(beats, [1, 1, 1], -20), [(2, 0)])

    def test_long_group_cannot_replace_an_established_partner(self):
        short = [[{"start": 4, "end": 20}, {"start": 60, "end": 76}]]
        old_links = [[[1, 10], [2, 11]]]
        long = [[{"start": 0, "end": 44}, {"start": 56, "end": 100}]]
        proposed = [[[1, 11], [2, 10], [3, 12]]]
        self.assertEqual(extend_repeat_groups(short, old_links, long, proposed), list(zip(short, old_links)))

    def test_comparison_can_use_a_musical_grid_independent_of_saved_beat_coordinates(self):
        original = self.phrase + [20 + beat for beat in self.phrase]
        markers = [{"beat": beat * 2 + 1, "count": 1} for beat in original]
        sections, links = find_timing_sections(
            markers, to_grid=lambda beat: (beat - 1) / 2, from_grid=lambda beat: beat * 2 + 1
        )
        self.assertEqual([section["start"] for section in sections], [1, 41])
        self.assertIn([4, len(self.phrase) + 4], links)
