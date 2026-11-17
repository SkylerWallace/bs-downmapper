using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record PlacementIntent(string Kind, double X, double Y, double? Weight = null);
    public sealed record PlacementContext(
        IReadOnlyDictionary<string, double> Controls, double Gap, double RampScale, double SparseAllowance);

    public static class PlacementPolicy
    {
        public const int BeamWidth = 6;
        public const int BranchWidth = 6;
        public const int MaximumRepairWindows = 9;

        public static double CrossoverDepth(Note note) =>
            Math.Max(0, note.Hand == 0 ? note.X - 1 : 2 - note.X);

        internal static double DoubleComfort(
            IReadOnlyList<Note> notes, ISet<int> linkedAnchors, double windupWeight)
        {
            if (notes.Count != 2 || notes[0].Hand == notes[1].Hand || notes.Any(note => note.Direction == 8) ||
                notes.All(note => linkedAnchors.Contains(note.Id)))
                return 0;
            var (left, right) = notes[0].Hand == 0 ? (notes[0], notes[1]) : (notes[1], notes[0]);
            double height = 3 * notes.Count(note => note.Y == 2 && !linkedAnchors.Contains(note.Id));
            if (left.Direction == right.Direction && left.Direction is 2 or 3 && left.Angle == 0 && right.Angle == 0)
            {
                var (lead, trail) = left.Direction == 2 ? (left, right) : (right, left);
                return height + (lead.Y <= trail.Y ? 3 : 0) + .65 * Math.Abs(lead.X - trail.X) +
                    1.4 * Math.Abs(lead.Y - 1) + 1.4 * trail.Y;
            }

            var first = SwingClearance.Cut(left, 1.25);
            var second = SwingClearance.Cut(right, 1.25);
            var relative = (first.End - first.Start) - (second.End - second.Start);
            double closing = (right.X - left.X) * relative.X;
            if (windupWeight != 0)
                closing = Math.Abs(closing + (right.Y - left.Y) * relative.Y);
            if (closing <= .1)
                return height;
            double weight = windupWeight == 0 ? 5 : windupWeight;
            double margin = windupWeight == 0 ? 1.15 : 1.5;
            return height + weight * Math.Max(0, margin - SwingClearance.Distance(first, second));
        }

        public static double Preference(
            Note candidate, Note source, Note? previous, PlacementIntent intent, PlacementContext context,
            double bombVisibility)
        {
            var controls = context.Controls;
            double x = intent?.X ?? source.X, y = intent?.Y ?? source.Y;
            if (intent?.Kind is "crossover_return" or "projected_bomb_return")
            {
                double limit = controls.GetValueOrDefault("crossoverReturnDepth", 2);
                x = candidate.Hand == 0 ? Math.Min(x, 1 + limit) : Math.Max(x, 2 - limit);
            }
            bool preserve = intent?.Kind is "paired_hand_call_response" or "bomb_return_reset" or
                "crossover_return" or "projected_bomb_return";
            double weight = intent?.Weight ?? (preserve ? 3 : 1);
            double position = (Math.Abs(candidate.X - x) + .8 * Math.Abs(candidate.Y - y)) *
                controls.GetValueOrDefault("sourcePositionCost", 1) * weight;

            bool down = candidate.Direction is 1 or 6 or 7;
            double depth = CrossoverDepth(candidate);
            double height = candidate.Y == 2 ? (1 - controls.GetValueOrDefault("height", .35)) * .8 : 0;
            if (candidate.Y == 1 && down)
            {
                height += controls.GetValueOrDefault("middleRowDownstrokeCost");
                if (candidate.X is 0 or 3) height += controls.GetValueOrDefault("outerMiddleDownstrokeCost");
            }
            if (candidate.Y == 2 && down)
            {
                height += controls.GetValueOrDefault("topDownstrokeCost");
                if (depth > 0) height += controls.GetValueOrDefault("topCrossedDownstrokeCost");
            }

            bool ownOuter = candidate.X == candidate.Hand * 3;
            double width = ownOuter ? (1 - controls.GetValueOrDefault("width", .75)) * (5.0 / 3) : 0;

            double sourceDepth = CrossoverDepth(source);
            double crossover = (1 - controls.GetValueOrDefault("crossover", .55)) * 3 *
                (preserve ? Math.Sign(depth) : depth);
            if (!preserve && depth == 2)
            {
                if (down && context.Gap <= .35) crossover += candidate.Direction is 6 or 7 ? 1.8 : .8;
                crossover += controls.GetValueOrDefault("deepCrossoverCost");
            }
            if (!preserve && depth == 1 && sourceDepth == 2)
                crossover -= controls.GetValueOrDefault("shallowCrossoverPreference");

            double sparseScale = 1 - context.SparseAllowance;
            double travel = 0;
            if (previous.HasValue)
            {
                double dx = candidate.X - previous.Value.X, dy = candidate.Y - previous.Value.Y;
                travel = .25 * Math.Sqrt(dx * dx + dy * dy);
            }
            return position + (height + width) * context.RampScale * sparseScale +
                crossover * context.RampScale * (depth == 1 && sourceDepth == 1 ? sparseScale : 1) +
                travel + bombVisibility;
        }

        public static double SideWallRecoveryGap(
            Note note, IReadOnlyList<Wall> walls, Func<double, double, double> gapSeconds)
        {
            double gap = double.PositiveInfinity;
            foreach (var wall in walls)
                if (wall.Y == 0 && wall.Height >= 3 && wall.Width < 4 && wall.X <= note.X &&
                    note.X < wall.X + wall.Width && wall.Beat <= note.Beat)
                    gap = Math.Min(gap, gapSeconds(wall.Beat + wall.Duration, note.Beat));
            return gap;
        }

        internal static (double Left, double Right) WallStartInterior(double beat, IReadOnlyList<Wall> walls)
        {
            double left = double.NegativeInfinity, right = double.PositiveInfinity;
            foreach (var wall in walls)
            {
                if (wall.Y != 0 || wall.Height < 3 || wall.Width <= 0 || wall.Width >= 4 ||
                    wall.Duration <= 0 || Math.Abs(wall.Beat - beat) > .0001)
                    continue;
                double center = wall.X + (wall.Width - 1) / 2;
                if (center < 1.5)
                    left = Math.Max(left, wall.X);
                else if (center > 1.5)
                    right = Math.Min(right, wall.X + wall.Width);
            }
            return (left, right);
        }

        public static IReadOnlyDictionary<int, double> RepairWindows(
            IReadOnlyList<Note> notes, IReadOnlyList<double> centers, Func<double, double, double> gapSeconds)
        {
            var windows = new Dictionary<int, double>();
            if (centers.Count == 0) return windows;
            var groups = NestedTimings.Group(notes);
            if (groups.Count == 0) return windows;
            foreach (double center in centers)
            {
                int middle = 0;
                for (int i = 1; i < groups.Count; i++)
                    if (Math.Abs(gapSeconds(center, groups[i].Beat)) < Math.Abs(gapSeconds(center, groups[middle].Beat)))
                        middle = i;
                for (int i = Math.Max(0, middle - 7); i < Math.Min(groups.Count, middle + 8); i++)
                {
                    double gap = gapSeconds(center, groups[i].Beat);
                    if (gap < -.9 || gap > .75) continue;
                    foreach (var note in groups[i].Notes)
                        windows.TryAdd(note.Id, center);
                }
            }
            return windows;
        }

        public static double? RelaxationCost(
            Note note, PlacementContext context, bool linkedAnchor, bool inRepairWindow, double wallGap,
            double costScale, IEnumerable<string> rejections)
        {
            var reasons = rejections.ToArray();
            if (reasons.Length == 0) return 0;
            if (linkedAnchor || !inRepairWindow) return null;
            var controls = context.Controls;
            double wallPreferred = controls.GetValueOrDefault("sideWallRecoverySeconds");
            double spacing = note.Direction switch
            {
                8 => controls.GetValueOrDefault("topDotSpacingSeconds", .9),
                1 or 6 or 7 => controls.GetValueOrDefault("topPalmSpacingSeconds", 1),
                _ => controls.GetValueOrDefault("topRowSpacingSeconds", .55),
            };
            double topRequired = spacing / (.5 + controls.GetValueOrDefault("height", .35));
            double total = 0;
            foreach (string reason in reasons)
            {
                switch (reason)
                {
                    case "central_readability_window": total += 3; break;
                    case "middle_row_downstroke": total += 5; break;
                    case "middle_row_outer_inward": total += 6; break;
                    case "side_wall_recovery_lane" when wallGap >= Math.Max(.30, wallPreferred - .20) && wallGap < wallPreferred:
                        total += 6 + 10 * (wallPreferred - wallGap);
                        break;
                    case "top_row_recovery" when context.Gap >= Math.Max(.5, topRequired - .35):
                        total += topRequired == 0 ? 6 : 6 + 8 * Math.Max(0, 1 - context.Gap / topRequired);
                        break;
                    default: return null;
                }
            }
            return total * costScale;
        }

        public static bool WithinRepairBudget(double cost, int notes) =>
            double.IsFinite(cost) && cost <= 24 + 1e-9 && notes <= 4;
    }
}
