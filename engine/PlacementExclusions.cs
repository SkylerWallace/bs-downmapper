using System;
using System.Collections.Generic;

namespace BsDownmap.Engine
{
    internal enum CellRepair { None, Clearance, StackClearance, ResetCell }

    internal sealed record CellContext(
        PlacementContext Placement, IReadOnlyList<Wall> Walls, IReadOnlyList<Bomb> NearbyBombs,
        Func<double, double, double> GapSeconds, double NextGap, double JumpSpeed, double CellMeters);

    internal static class PlacementExclusions
    {
        internal static IEnumerable<string> Evaluate(
            Note note, CellContext context, CellRepair repair, bool flattenOutward)
        {
            var controls = context.Placement.Controls;
            bool middle = note.Y == 1;
            bool top = note.Y == 2;
            bool palm = note.Direction is 1 or 6 or 7;
            bool clearanceRepair = repair is CellRepair.Clearance or CellRepair.StackClearance;
            double height = controls["height"];
            double middleRecovery = controls["crouchMiddleRecoverySeconds"];
            var gap = context.GapSeconds;

            if (controls["bottomRowDownstrokes"] != 0 && middle && palm &&
                repair is not (CellRepair.ResetCell or CellRepair.StackClearance))
                yield return "middle_row_downstroke";
            if (controls["bottomRowOuterInward"] != 0 && middle && note.X is 0 or 3 &&
                note.Direction == (note.X == 0 ? 3 : 2))
                yield return "middle_row_outer_inward";

            if (middle && middleRecovery != 0)
            {
                foreach (var wall in context.Walls)
                {
                    double end = wall.Beat + wall.Duration;
                    if (wall.Y < 2 || wall.Width < 3 || wall.Beat > note.Beat || gap(end, note.Beat) >= middleRecovery)
                        continue;
                    if (clearanceRepair && end < note.Beat &&
                        gap(wall.Beat, end) < controls["resetCueMinimumCrouchSeconds"])
                        continue;
                    yield return "middle_row_during_crouch_recovery";
                    break;
                }
            }
            if ((controls["ownSideOutward"] != 0 || flattenOutward) && note.Direction == (note.Hand == 0 ? 2 : 3) &&
                PlacementPolicy.CrossoverDepth(note) > 0)
                yield return "outward_cut_on_crossed_side";

            double sideRecovery = controls["sideWallRecoverySeconds"];
            if (sideRecovery != 0 &&
                PlacementPolicy.SideWallRecoveryGap(note, context.Walls, gap) < sideRecovery)
                yield return "side_wall_recovery_lane";

            if (top)
            {
                bool resetUp = repair == CellRepair.ResetCell && note.Direction is 0 or 4 or 5;
                if (height == 0 && !resetUp)
                    yield return "height_disabled";
                double minimum = note.Direction == 8 ? controls["topDotSpacingSeconds"] :
                    palm ? controls["topPalmSpacingSeconds"] : controls["topRowSpacingSeconds"];
                if (context.Placement.Gap < minimum / (.5 + height) && !resetUp)
                    yield return "top_row_recovery";

                foreach (var wall in context.Walls)
                {
                    if (wall.Y >= 2 && wall.Width >= 3 && wall.Beat <= note.Beat &&
                        gap(wall.Beat + wall.Duration, note.Beat) <= controls["crouchRecoverySeconds"])
                    {
                        yield return "top_row_during_crouch_recovery";
                        break;
                    }
                }
            }
            if (note.X is 1 or 2 && middle && context.NextGap <= controls["centerVisionWindowSeconds"] && !clearanceRepair)
                yield return "central_readability_window";

            var cut = note.Direction == 8 ? (SwingClearance.Segment?)null : SwingClearance.Cut(note, 1.5);
            double cellMeters = context.CellMeters;
            double clearance = controls["bombSwingClearanceMeters"];
            bool swingFlagged = false;
            foreach (var bomb in context.NearbyBombs)
            {
                double depth = context.JumpSpeed * Math.Abs(gap(note.Beat, bomb.Beat));
                var offset = new SwingClearance.Point(note.X - bomb.X, note.Y - bomb.Y);
                double separation = offset.Length * cellMeters;
                bool sameHead = Math.Abs(note.Beat - bomb.Beat) <= .0001 && note.X == bomb.X && note.Y == bomb.Y;
                if (sameHead || new SwingClearance.Point(separation, depth).Length < controls["noteBombHeadClearanceMeters"])
                {
                    yield return "note_bomb_head_overlap";
                    break;
                }
                if (!cut.HasValue || depth >= clearance || swingFlagged)
                    continue;

                double lateral = SwingClearance.Distance(new SwingClearance.Point(bomb.X, bomb.Y), cut.Value) * cellMeters;
                bool gridParallel = note.Direction is 0 or 1 or 2 or 3 && note.Angle == 0;
                bool parallel = clearanceRepair && lateral >= cellMeters - 1e-9 &&
                    (gridParallel || lateral >= separation - 1e-9);
                if (!parallel && new SwingClearance.Point(lateral, depth).Length < clearance - 1e-9)
                {
                    yield return "bomb_swing_clearance";
                    swingFlagged = true;
                }
            }
        }
    }
}
