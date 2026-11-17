using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal readonly record struct CellOption(Note Note, double ShapeCost, double RepairCost);
    internal readonly record struct LinkedEndpoint(double Beat, int Hand, double X, double Y);

    internal sealed record CellDomain(
        CellContext Context, double Bpm, double WallClearance, double ReachLimit, bool LinkedAnchor,
        bool InRepairWindow, double RepairCostScale, CellRepair Repair, bool FlattenOutward, bool PreservePosition,
        bool RetainOpeningDot, ISet<(double X, double Y)> AllowedCells, IReadOnlyList<LinkedEndpoint> LinkedEndpoints);

    internal static class PlacementDomains
    {
        internal static IEnumerable<PlacedAttack> JointLayouts(
            IReadOnlyList<Note> notes, IReadOnlyDictionary<int, Note> source,
            Func<Note, IEnumerable<CellOption>> choices, Func<IReadOnlyList<Note>, bool> validSequence,
            ISet<int> anchors, ISet<int> repairIds, bool repairLargeStacks,
            bool comfortableDoubles, double windupWeight)
        {
            var domains = new Dictionary<int, CellOption[]>();
            foreach (var note in notes)
            {
                int limit = repairIds.Contains(note.Id) || notes.Count > 2 && repairLargeStacks ? 12 : 8;
                var options = choices(note).OrderBy(option => option.ShapeCost).Take(limit).ToArray();
                if (options.Length == 0)
                    yield break;
                domains.Add(note.Id, options);
            }

            var left = Stacks(notes.Where(note => note.Hand == 0).Select(note => domains[note.Id]));
            var right = Stacks(notes.Where(note => note.Hand == 1).Select(note => domains[note.Id]));
            var layouts = new List<PlacedAttack>();
            foreach (var red in left)
            foreach (var blue in right)
            {
                if (red.Any(a => blue.Any(b =>
                    (a.Note.X > b.Note.X && source[a.Note.Id].X <= source[b.Note.Id].X) ||
                    !Compatible(a.Note, b.Note))))
                    continue;

                var combined = red.Concat(blue).ToArray();
                var trial = combined.Select(option => option.Note).ToArray();
                if (!validSequence(trial))
                    continue;
                double shape = combined.Sum(option => option.ShapeCost);
                if (comfortableDoubles)
                    shape += PlacementPolicy.DoubleComfort(trial, anchors, windupWeight);
                layouts.Add(new PlacedAttack(trial, shape, combined.Sum(option => option.RepairCost),
                    combined.Count(option => option.RepairCost > 0)));
            }
            foreach (var layout in layouts.OrderBy(layout => layout.ShapeCost))
                yield return layout;
        }

        private static IReadOnlyList<CellOption[]> Stacks(IEnumerable<CellOption[]> domains)
        {
            IReadOnlyList<CellOption[]> stacks = new[] { Array.Empty<CellOption>() };
            foreach (var options in domains)
                stacks = stacks.SelectMany(stack => options.Where(option =>
                    stack.All(other => Compatible(other.Note, option.Note)))
                    .Select(option => stack.Append(option).ToArray())).ToArray();
            return stacks;
        }

        private static bool Compatible(Note first, Note second) =>
            !SwingClearance.GroupRules(first, second).Any(rule => rule != "G04");

        internal static IEnumerable<CellOption> Enumerate(
            Note note, Note source, CellDomain domain, PlacementIntent intent,
            Func<double, double> seconds, Func<Note, double> reach, Func<Note, double> bombVisibility,
            Func<Note, bool> blocksLinkedSwing, Func<Note, bool> obscuredByWall)
        {
            if (domain.LinkedAnchor)
            {
                if (!domain.Context.Walls.Any(wall => SwingClearance.BlocksWallApproach(note, wall)))
                    yield return new CellOption(note, 0, 0);
                yield break;
            }

            var context = domain.Context;
            var controls = context.Placement.Controls;
            var sourceBombs = SwingClearance.BombClearances(source, context.NearbyBombs, seconds, domain.Bpm);
            var sourceWalls = SwingClearance.WallConflicts(source, context.Walls, seconds, domain.WallClearance).ToHashSet();
            var wallInterior = PlacementPolicy.WallStartInterior(note.Beat, context.Walls);
            bool reversedCross = !OwnSide(source) && source.Direction is 2 or 3 &&
                note.Direction is 2 or 3 && source.Direction != note.Direction;
            for (int x = 0; x < 4; x++)
            for (int y = 0; y < 3; y++)
            {
                var candidate = note with { X = x, Y = y };
                bool moved = x != source.X || y != source.Y;
                if (domain.PreservePosition && moved ||
                    domain.AllowedCells != null && !domain.AllowedCells.Contains((x, y)) ||
                    reversedCross && !OwnSide(candidate))
                    continue;

                var reasons = Exclusions(candidate, domain.Repair).ToArray();
                double wallGap = PlacementPolicy.SideWallRecoveryGap(candidate, context.Walls, context.GapSeconds);
                double? repairCost = PlacementPolicy.RelaxationCost(candidate, context.Placement, false,
                    domain.InRepairWindow, wallGap, domain.RepairCostScale, reasons);
                if (!repairCost.HasValue || blocksLinkedSwing(candidate))
                    continue;

                double candidateReach = reach(candidate);
                if (candidateReach > domain.ReachLimit + controls["reachSlackMeters"] ||
                    controls.GetValueOrDefault("centeredReachOnly") != 0 && candidateReach > MotionVisibility.ArmReachMeters)
                    continue;
                var clearances = SwingClearance.BombClearances(candidate, context.NearbyBombs, seconds, domain.Bpm);
                if (clearances.Any(pair => pair.Value < .8 && sourceBombs.GetValueOrDefault(pair.Key, double.PositiveInfinity) >= .8 ||
                    sourceBombs.TryGetValue(pair.Key, out double baseline) && pair.Value < Math.Min(.8, baseline) - .05))
                    continue;
                if (context.Walls.Any(wall => SwingClearance.BlocksWallApproach(candidate, wall)) ||
                    SwingClearance.WallConflicts(candidate, context.Walls, seconds, domain.WallClearance)
                    .Any(index => !sourceWalls.Contains(index)) || obscuredByWall(candidate))
                    continue;

                double shapeCost = PlacementPolicy.Preference(candidate, source, null, intent, context.Placement,
                    bombVisibility(candidate)) + 5 * Math.Max(0, candidateReach - domain.ReachLimit);
                if (x < wallInterior.Left || x >= wallInterior.Right)
                    shapeCost += controls.GetValueOrDefault("outsideWallStartCost");
                if (candidate.Y == 2 && controls.GetValueOrDefault("softTopRowSpacing") != 0)
                {
                    double spacing = controls[candidate.Direction switch
                    {
                        8 => "topDotSpacingSeconds",
                        1 or 6 or 7 => "topPalmSpacingSeconds",
                        _ => "topRowSpacingSeconds",
                    }];
                    double preferredGap = spacing / (.5 + controls["height"]);
                    if (preferredGap > 0)
                        shapeCost += 6 * Math.Max(0, 1 - context.Placement.Gap / preferredGap);
                }
                if (domain.RetainOpeningDot && moved)
                    shapeCost += 25;
                if (domain.Repair is CellRepair.Clearance or CellRepair.StackClearance)
                    shapeCost += 2 * Exclusions(candidate, CellRepair.None).Count();
                yield return new CellOption(candidate, shapeCost, repairCost.Value);
            }

            IEnumerable<string> Exclusions(Note candidate, CellRepair repair)
            {
                if (domain.LinkedEndpoints.Any(endpoint => Math.Abs(endpoint.Beat - candidate.Beat) <= .0001 &&
                    endpoint.Hand == candidate.Hand && endpoint.X == candidate.X && endpoint.Y == candidate.Y))
                    return new[] { "new_linked_endpoint_attachment" };
                return PlacementExclusions.Evaluate(candidate, context, repair, domain.FlattenOutward);
            }

            static bool OwnSide(Note value) => value.Hand == 0 ? value.X < 2 : value.X >= 2;
        }
    }
}
