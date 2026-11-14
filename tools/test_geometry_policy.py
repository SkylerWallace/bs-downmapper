import unittest, copy
from geometry_policy import (
    context,
    exclusions,
    preference,
    crouch_recovery_cue,
    double_comfort_cost,
    introduces_hand_inversion,
    inline_readability_cost,
)


def n(b, c=0, x=0, y=0, d=0, i=0):
    return dict(b=b, c=c, x=x, y=y, d=d, a=0, id=i)


def ctx(ns, walls=None, bombs=None, settings=None, sections=()):
    return context(
        ns,
        {"bpm": 100, "njs": 15, "offset": 0, "walls": walls or [], "bombs": bombs or []},
        {"njsMultiplier": 1, "geometry": settings or {}},
        sections,
    )


class GeometryPolicyTests(unittest.TestCase):
    def test_double_comfort_prefers_leading_hand_above_and_non_converging_swings(self):
        for direction, lead in [(2, 0), (3, 1)]:
            good = [n(10, c=hand, x=1, y=int(hand == lead), d=direction) for hand in (0, 1)]
            reversed_order = [{**note, "y": 1 - note["y"]} for note in good]
            self.assertLess(double_comfort_cost(good), double_comfort_cost(reversed_order))
        converging = [n(10, x=1, d=3), n(10, c=1, x=3, d=1)]
        separating = [{**converging[0], "d": 2}, converging[1]]
        self.assertGreater(double_comfort_cost(converging), double_comfort_cost(separating))
        self.assertEqual(double_comfort_cost(converging[:1]), 0)

    def test_hard_counts_tight_windup_on_either_side(self):
        tight = [n(10, x=1, d=2), n(10, c=1, x=3, d=1)]
        roomy = [{**tight[0], "x": 0, "y": 1}, tight[1]]
        mirrored = [{**note, "x": 3 - note["x"], "c": 1 - note["c"],
                     "d": 3 if note["d"] == 2 else note["d"]} for note in tight]
        self.assertEqual(double_comfort_cost(tight), 0)
        self.assertGreater(double_comfort_cost(tight, windup_weight=12), 0)
        self.assertEqual(double_comfort_cost(roomy, windup_weight=12), 0)
        self.assertAlmostEqual(
            double_comfort_cost(tight, windup_weight=12), double_comfort_cost(mirrored, windup_weight=12)
        )

    def test_staggered_double_cannot_avoid_windup_cost_by_sharing_a_column(self):
        left = n(10, x=1, y=0, d=2)
        right = n(10, c=1, x=1, y=2, d=0)
        staggered = [left, right]
        roomy = [left, {**right, "x": 3}]
        self.assertGreater(
            double_comfort_cost(staggered, windup_weight=12),
            double_comfort_cost(roomy, windup_weight=12),
        )

    def test_spread_controls_affect_separate_axes(self):
        for hand in (0, 1):
            for x in (hand * 3, hand + 1):
                for y in range(3):
                    note = n(10, c=hand, x=x, y=y)
                    low = preference(note, note, None, ctx([note], settings={"width": 0.2, "height": 0.2}))
                    wide = preference(note, note, None, ctx([note], settings={"width": 0.8, "height": 0.2}))
                    high = preference(note, note, None, ctx([note], settings={"width": 0.2, "height": 0.8}))
                    self.assertAlmostEqual(low["width"] - wide["width"], int(x == hand * 3))
                    self.assertAlmostEqual(low["height"] - high["height"], 0.48 if y == 2 else 0)
                    self.assertEqual(low["height"], wide["height"])
                    self.assertEqual(low["width"], high["width"])

    def test_bomb_overlap_not_grandfathered_even_for_dots(self):
        for start in [7.125, 54, 218.75]:
            note = n(start, x=1, y=0, d=8)
            c = ctx([note], bombs=[dict(b=start, x=1, y=0)])
            self.assertIn("note_bomb_head_overlap", exclusions(note, c))
            self.assertNotIn("note_bomb_head_overlap", exclusions({**note, "x": 2}, c))
            self.assertIn(
                "note_bomb_head_overlap",
                exclusions(
                    note,
                    ctx([note], bombs=[dict(b=start, x=1, y=0)], settings={"noteBombHeadClearanceMeters": 0}),
                ),
            )

    def test_crowded_top_palms_and_spaced_dot_exception(self):
        note = n(10, y=2, d=1)
        nextnote = n(11, i=1)
        self.assertIn("top_row_recovery", exclusions(note, ctx([note, nextnote], settings={"height": 0.55})))
        spaced = [note, n(13, i=1)]
        self.assertNotIn(
            "top_row_recovery", exclusions({**note, "d": 8}, ctx(spaced, settings={"height": 0.55}))
        )
        self.assertIn("height_disabled", exclusions(note, ctx(spaced, settings={"height": 0})))

    def test_shifted_crouch_recovery_and_short_wall_negative(self):
        for start in [6, 27.5, 211]:
            wall = dict(b=start, x=0, y=2, d=0.5, w=4, h=3)
            note = n(start + 1.5, y=2)
            c = ctx([note], walls=[wall])
            self.assertIn("top_row_during_crouch_recovery", exclusions(note, c))
            self.assertTrue(crouch_recovery_cue(note, c))
            short = {**wall, "d": 0.05}
            self.assertFalse(crouch_recovery_cue(n(start + 0.5), ctx([n(start + 0.5)], walls=[short])))

    def test_source_central_blocker_is_rejected_with_following_target(self):
        note = n(10, x=1, y=1)
        follow = n(11, c=1, i=1)
        self.assertIn("central_readability_window", exclusions(note, ctx([note, follow])))
        self.assertNotIn("central_readability_window", exclusions(note, ctx([note])))

    def test_controls_are_independent_preference_terms(self):
        note = n(10, x=3, y=2)
        original = dict(note)
        high = preference(
            note, original, None, ctx([note], settings={"height": 1, "width": 1, "crossover": 1})
        )
        low = preference(
            note, original, None, ctx([note], settings={"height": 0, "width": 0, "crossover": 0})
        )
        for key in ["height", "crossover"]:
            self.assertGreater(low[key], high[key])
        self.assertEqual(low["width"], high["width"])
        with self.assertRaises(ValueError):
            ctx([note], settings={"height": 2})

    def test_horizontal_spread_only_changes_own_outer_lane_cost(self):
        for hand in (0, 1):
            for column in range(4):
                note = n(10, c=hand, x=column)
                narrow = preference(note, note, None, ctx([note], settings={"width": 0}))
                wide = preference(note, note, None, ctx([note], settings={"width": 1}))
                self.assertEqual(narrow["crossover"], wide["crossover"])
                if column == hand * 3:
                    self.assertGreater(narrow["width"], wide["width"])
                else:
                    self.assertEqual(narrow["width"], wide["width"])

    def test_bomb_sweep_excludes_diagonal_with_clear_note_head(self):
        note = n(10, x=1, d=6)
        policy = ctx([note], bombs=[dict(b=10, x=0, y=0)])
        self.assertEqual(exclusions(note, policy), ["bomb_swing_clearance"])
        self.assertEqual(exclusions({**note, "d": 1}, policy), [])

    def test_empty_domain_bomb_repair_requires_a_parallel_clear_path(self):
        note = n(10, x=1, d=1)
        policy = ctx([note], bombs=[dict(b=10, x=0, y=0)], settings={"bombSwingClearanceMeters": 0.65})
        self.assertIn("bomb_swing_clearance", exclusions(note, policy))
        repaired = {**note, "domainRepair": "clearance"}
        self.assertEqual(exclusions(repaired, policy), [])
        self.assertIn("bomb_swing_clearance", exclusions({**repaired, "d": 6}, policy))
        self.assertIn("note_bomb_head_overlap", exclusions({**repaired, "x": 0}, policy))

    def test_only_introduced_double_inversion_is_rejected(self):
        red, blue = n(10, x=0), n(10, c=1, x=1, i=1)
        inverted = [{**red, "x": 3}, {**blue, "x": 2}]
        self.assertTrue(introduces_hand_inversion(inverted, {0: red, 1: blue}))
        self.assertFalse(introduces_hand_inversion(inverted, {note["id"]: note for note in inverted}))
        self.assertFalse(introduces_hand_inversion([{**red, "x": 2}, {**blue, "x": 3}], {0: red, 1: blue}))

    def test_opt_in_ramp_relaxes_late_costs_and_keeps_exclusions(self):
        first, last = n(10, x=3), n(20, x=3, i=1)
        notes = [first, last]
        level = {"bpm": 100, "njs": 15, "walls": [], "bombs": [dict(b=20, x=3, y=0)]}
        base = context(notes, level, {"njsMultiplier": 1})
        enabled = context(notes, level, {"njsMultiplier": 1, "difficultyRamp": {"enabled": True}})
        self.assertEqual(preference(first, first, None, base), preference(first, first, None, enabled))
        self.assertLess(
            preference(last, last, None, enabled)["crossover"],
            preference(last, last, None, base)["crossover"],
        )
        self.assertEqual(exclusions(last, base), exclusions(last, enabled))

    def test_side_wall_recovery_excludes_even_a_seeded_wall_lane(self):
        note = n(14.75, c=1, x=2, d=1)
        wall = dict(b=14, x=2, y=0, d=0.25, w=2, h=5)
        policy = ctx([note], walls=[wall], settings={"sideWallRecoverySeconds": 0.45})
        self.assertIn("side_wall_recovery_lane", exclusions(note, policy))
        self.assertNotIn("side_wall_recovery_lane", exclusions({**note, "x": 1}, policy))
        self.assertNotIn("side_wall_recovery_lane", exclusions({**note, "b": 15.25}, policy))

    def test_beginner_outward_requires_own_side(self):
        for hand, own, cross, d in [(0, 0, 2, 2), (1, 3, 1, 3)]:
            note = n(10, c=hand, x=own, d=d)
            policy = ctx([note], settings={"ownSideOutward": True})
            self.assertEqual(exclusions(note, policy), [])
            self.assertIn("outward_cut_on_crossed_side", exclusions({**note, "x": cross}, policy))

    def test_sparse_budget_does_not_apply_to_dense_or_wall_adjacent_notes(self):
        sparse, dense, near_wall = n(12), n(24, i=1), n(36, i=2)
        sections = [
            {"startBeat": start, "endBeatExclusive": start + 8, "nps": nps}
            for start, nps in [(10, 1.0), (20, 2.0), (30, 1.0)]
        ]
        notes = [sparse, dense, near_wall]
        walls = [dict(b=35.5, x=3, y=0, d=0.25, w=1, h=5)]
        base = ctx(notes, walls=walls)
        allowed = ctx(notes, walls=walls, settings={"sparseSectionAllowance": 0.25}, sections=sections)
        self.assertEqual(allowed["sparseAllowance"], {sparse["id"]: 0.25})
        self.assertLess(
            preference(sparse, sparse, None, allowed)["width"],
            preference(sparse, sparse, None, base)["width"],
        )
        self.assertEqual(preference(dense, dense, None, allowed), preference(dense, dense, None, base))

    def test_quick_opposite_color_inline_is_harder_to_read(self):
        before = n(10, x=1)
        after = n(10.5, c=1, x=1, i=1)
        policy = ctx([before, after], settings={"oppositeInlineSeconds": 0.36, "sameHandInlineSeconds": 0.16})
        self.assertGreater(
            inline_readability_cost(before, after, policy),
            inline_readability_cost(before, {**after, "y": 1}, policy),
        )
        self.assertEqual(inline_readability_cost(before, {**after, "x": 0}, policy), 0)
        self.assertEqual(inline_readability_cost(before, {**after, "c": 0}, policy), 0)
        self.assertEqual(inline_readability_cost(before, {**after, "b": 11}, policy), 0)

    def test_beginner_row_limits_and_bomb_margin(self):
        note = n(10, c=1, x=3, y=1, d=1)
        wall = dict(b=9.25, x=0, y=2, d=0.125, w=4, h=3)
        policy = ctx(
            [note],
            walls=[wall],
            settings={
                "bottomRowDownstrokes": True,
                "bottomRowOuterInward": True,
                "crouchMiddleRecoverySeconds": 0.55,
            },
        )
        self.assertIn("middle_row_downstroke", exclusions(note, policy))
        self.assertIn("middle_row_outer_inward", exclusions({**note, "d": 2}, policy))
        self.assertIn("middle_row_during_crouch_recovery", exclusions(note, policy))
        self.assertEqual(exclusions({**note, "y": 0}, policy), [])
        bomb_policy = ctx([note], bombs=[dict(b=10, x=2, y=1)], settings={"bombSwingClearanceMeters": 0.65})
        self.assertIn("bomb_swing_clearance", exclusions(note, bomb_policy))


if __name__ == "__main__":
    unittest.main()
