using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal sealed record PlacedAttack(Note[] Notes, double ShapeCost, double RepairCost, int RelaxedNotes);

    internal sealed class PlacementDeadEnd : InvalidOperationException
    {
        internal PlacementDeadEnd(double beat, IReadOnlyList<Note> notes, string reason,
            int? visibilityTarget = null, IReadOnlyList<int> blockers = null)
            : base($"{reason} at beat {beat:R}.")
        {
            Beat = beat;
            Notes = notes;
            Reason = reason;
            VisibilityTarget = visibilityTarget;
            Blockers = blockers;
        }

        internal double Beat { get; }
        internal IReadOnlyList<Note> Notes { get; }
        internal string Reason { get; }
        internal int? VisibilityTarget { get; }
        internal IReadOnlyList<int> Blockers { get; }
    }

    internal static class PlacementSearch
    {
        private const int CheckpointCount = 4;
        private const int RetryLimit = 24;

        private sealed record Checkpoint(int Attack, int PrefixCount, IEnumerator<PlacedAttack> Alternatives);
        private sealed record Path(PlacedAttack[] Attacks, double RepairCost, int RelaxedNotes, double ShapeCost);

        internal static IReadOnlyList<Note> Solve(
            IReadOnlyList<Note> notes, IReadOnlyDictionary<int, double> windows,
            Func<int, IReadOnlyList<Note>, IEnumerable<PlacedAttack>> choices)
        {
            var attacks = NestedTimings.Group(notes);
            var bounds = WindowBounds(attacks, windows);
            var output = new List<Note>(notes.Count);
            var checkpoints = new List<Checkpoint>();
            int retries = 0, index = 0;
            try
            {
                while (index < attacks.Count)
                {
                    if (bounds.TryGetValue(index, out int stop))
                    {
                        foreach (var attack in SearchWindow(index, stop))
                            output.AddRange(attack.Notes);
                        ClearCheckpoints(0);
                        index = stop;
                        continue;
                    }

                    IEnumerator<PlacedAttack> alternatives = null;
                    PlacedAttack chosen = null;
                    PlacementDeadEnd failure = null;
                    try
                    {
                        alternatives = Choices(index, output).ToList().GetEnumerator();
                        if (alternatives.MoveNext())
                            chosen = alternatives.Current;
                    }
                    catch (PlacementDeadEnd deadEnd)
                    {
                        failure = deadEnd;
                    }

                    if (chosen == null)
                    {
                        alternatives?.Dispose();
                        for (int slot = checkpoints.Count - 1; slot >= 0 && retries < RetryLimit; slot--)
                        {
                            var checkpoint = checkpoints[slot];
                            if (!checkpoint.Alternatives.MoveNext())
                                continue;

                            index = checkpoint.Attack;
                            output.RemoveRange(checkpoint.PrefixCount, output.Count - checkpoint.PrefixCount);
                            chosen = checkpoint.Alternatives.Current;
                            alternatives = checkpoint.Alternatives;
                            ClearCheckpoints(slot + 1);
                            checkpoints.RemoveAt(slot);
                            retries++;
                            break;
                        }
                        if (chosen == null)
                            throw failure ?? DeadEnd(index, "No joint geometry");
                    }

                    checkpoints.Add(new Checkpoint(index, output.Count, alternatives));
                    if (checkpoints.Count > CheckpointCount)
                    {
                        checkpoints[0].Alternatives.Dispose();
                        checkpoints.RemoveAt(0);
                    }
                    output.AddRange(chosen.Notes);
                    index++;
                }
                return output;
            }
            finally
            {
                ClearCheckpoints(0);
            }

            void ClearCheckpoints(int start)
            {
                for (int slot = start; slot < checkpoints.Count; slot++)
                    checkpoints[slot].Alternatives.Dispose();
                checkpoints.RemoveRange(start, checkpoints.Count - start);
            }

            PlacementDeadEnd DeadEnd(int attack, string reason) =>
                new(attacks[attack].Beat, attacks[attack].Notes, reason);

            IEnumerable<PlacedAttack> Choices(int attack, IReadOnlyList<Note> prefix)
            {
                var expected = attacks[attack].Notes.OrderBy(note => note.Id)
                    .Select(note => (note.Id, note.Beat, note.Hand)).ToArray();
                foreach (var choice in choices(attack, prefix))
                {
                    if (!expected.SequenceEqual(choice.Notes.OrderBy(note => note.Id)
                        .Select(note => (note.Id, note.Beat, note.Hand))))
                        throw new InvalidOperationException("A placement changed the approved timing or hand signature.");
                    if (!double.IsFinite(choice.ShapeCost) || !double.IsFinite(choice.RepairCost) ||
                        choice.RepairCost < 0 || choice.RelaxedNotes < 0)
                        throw new InvalidOperationException("A placement returned an invalid search cost.");
                    yield return choice;
                }
            }

            PlacedAttack[] SearchWindow(int start, int stop)
            {
                bool stack = Enumerable.Range(start, stop - start).Any(index =>
                    attacks[index].Notes.GroupBy(note => note.Hand).Any(hand => hand.Count() > 1));
                int branchWidth = stack ? 2 * PlacementPolicy.BranchWidth : PlacementPolicy.BranchWidth;
                int beamWidth = stack ? 2 * PlacementPolicy.BeamWidth : PlacementPolicy.BeamWidth;
                var beam = new[] { new Path(Array.Empty<PlacedAttack>(), 0, 0, 0) };
                for (int attack = start; attack < stop; attack++)
                {
                    var expanded = new List<Path>();
                    PlacementDeadEnd failure = null;
                    foreach (var path in beam)
                    {
                        var prefix = output.Concat(path.Attacks.SelectMany(layout => layout.Notes)).ToArray();
                        try
                        {
                            foreach (var option in Choices(attack, prefix).OrderBy(choice => choice.RepairCost)
                                .ThenBy(choice => choice.ShapeCost).Take(branchWidth))
                            {
                                double cost = path.RepairCost + option.RepairCost;
                                int count = path.RelaxedNotes + option.RelaxedNotes;
                                if (PlacementPolicy.WithinRepairBudget(cost, count))
                                    expanded.Add(new Path(path.Attacks.Append(option).ToArray(), cost, count,
                                        path.ShapeCost + option.ShapeCost));
                            }
                        }
                        catch (PlacementDeadEnd deadEnd)
                        {
                            failure = deadEnd;
                        }
                    }
                    if (expanded.Count == 0)
                        throw failure ?? DeadEnd(attack, "Bounded placement budget exhausted");
                    beam = expanded.OrderBy(path => path.RepairCost).ThenBy(path => path.RelaxedNotes)
                        .ThenBy(path => path.ShapeCost).Take(beamWidth).ToArray();
                }
                return beam[0].Attacks;
            }
        }

        private static Dictionary<int, int> WindowBounds(
            IReadOnlyList<NestedTimings.BeatGroup> attacks, IReadOnlyDictionary<int, double> windows)
        {
            var centers = attacks.Select(attack => windows.TryGetValue(attack.Notes[0].Id, out double center)
                ? (double?)center : null).ToArray();
            var bounds = new Dictionary<int, int>();
            for (int index = 0; index < centers.Length; index++)
            {
                if (!centers[index].HasValue || index > 0 && centers[index] == centers[index - 1])
                    continue;
                int stop = index + 1;
                while (stop < centers.Length && centers[stop] == centers[index])
                    stop++;
                if (stop < centers.Length && !centers[stop].HasValue)
                    stop++;
                bounds.Add(index, stop);
            }
            return bounds;
        }
    }
}
