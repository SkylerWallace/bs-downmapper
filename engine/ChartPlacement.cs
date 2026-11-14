using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BsDownmap.Engine
{
    internal sealed record PlacementCue(
        PlacementIntent Intent, int? PairId = null, int? PreviousResetNoteId = null);

    internal sealed record PlacementPlan(
        IReadOnlyDictionary<int, Note> Source, IReadOnlyDictionary<int, CellDomain> Domains,
        IReadOnlyDictionary<int, PlacementCue> Cues, IReadOnlyDictionary<int, double> RepairWindows,
        IReadOnlyList<Wall> Walls, Func<double, double> Seconds, Func<Note, double> BombVisibility,
        double ApproachSeconds, double JumpSpeed, bool CompactPairedCells,
        bool RepairLargeStacks, bool ComfortableDoubles, double WindupWeight);

    internal static class ChartPlacement
    {
        internal static IReadOnlyList<Note> Place(IReadOnlyList<Note> notes, PlacementPlan plan, CancellationToken cancellation = default)
        {
            var timing = NestedTimings.Group(notes);
            var seconds = plan.Seconds;
            var anchors = notes.Where(note => plan.Domains[note.Id].LinkedAnchor)
                .Select(note => new TimedNote(note, seconds(note.Beat))).ToArray();
            var anchorIds = anchors.Select(anchor => anchor.Note.Id).ToHashSet();
            var repairIds = plan.RepairWindows.Keys.ToHashSet();
            var walls = plan.Walls.Select(wall => new TimedWall(wall, seconds(wall.Beat),
                seconds(wall.Beat + wall.Duration))).ToArray();
            var options = new Dictionary<int, CellOption[]>();

            foreach (var group in timing)
            {
                cancellation.ThrowIfCancellationRequested();
                double time = seconds(group.Beat);
                var nearbyWalls = walls.Where(wall => wall.Start <= time + plan.ApproachSeconds &&
                    wall.End >= time - plan.ApproachSeconds).ToArray();
                foreach (var note in group.Notes)
                {
                    var domain = plan.Domains[note.Id];
                    var linked = anchors.Where(anchor => Math.Abs(anchor.Note.Beat - note.Beat) <= .0001 ||
                        anchor.Note.Hand != note.Hand && Math.Abs(anchor.Time - seconds(note.Beat)) <=
                        2 * MotionVisibility.CutHalfDuration).ToArray();
                    var choices = PlacementDomains.Enumerate(note, plan.Source[note.Id], domain,
                        plan.Cues.GetValueOrDefault(note.Id)?.Intent, seconds, MotionVisibility.Reach,
                        plan.BombVisibility, BlocksLink, BlocksWall).ToArray();
                    if (choices.Length == 0)
                        throw new PlacementDeadEnd(group.Beat, group.Notes, "No source-relative geometry");
                    options.Add(note.Id, choices);

                    bool BlocksLink(Note trial) => linked.Any(anchor =>
                        MotionVisibility.BlocksLinkedSwing(new TimedNote(trial, time), anchor));

                    bool BlocksWall(Note trial) => MotionVisibility.LateVisibility(new TimedNote(trial, time),
                        Array.Empty<TimedNote>(), nearbyWalls, plan.JumpSpeed, plan.ApproachSeconds).Any();
                }
            }

            foreach (var pair in notes.Where(note => PairId(note) != null).GroupBy(note => PairId(note).Value))
            {
                var shared = options[pair.First().Id].Select(option => Cell(option.Note)).ToHashSet();
                foreach (var note in pair.Skip(1))
                    shared.IntersectWith(options[note.Id].Select(option => Cell(option.Note)));
                if (shared.Count > 0)
                    foreach (var note in pair)
                        options[note.Id] = options[note.Id].Where(option => shared.Contains(Cell(option.Note))).ToArray();
            }

            return PlacementSearch.Solve(notes, plan.RepairWindows, Layouts);

            int? PairId(Note note) => plan.Cues.GetValueOrDefault(note.Id)?.PairId;

            IEnumerable<PlacedAttack> Layouts(int index, IReadOnlyList<Note> prefix)
            {
                cancellation.ThrowIfCancellationRequested();
                var group = timing[index];
                double time = seconds(group.Beat);
                var prior = prefix.Reverse().TakeWhile(note => time - seconds(note.Beat) <= plan.ApproachSeconds)
                    .Reverse().Select(note => new TimedNote(note, seconds(note.Beat))).ToArray();
                var last = new Note?[2];
                var cells = new Dictionary<int, (double X, double Y)>();
                for (int previousIndex = prefix.Count - 1; previousIndex >= 0; previousIndex--)
                {
                    var previous = prefix[previousIndex];
                    last[previous.Hand] ??= previous;
                    if (PairId(previous) is int pair)
                        cells.TryAdd(pair, Cell(previous));
                }
                bool pairedRelease = plan.CompactPairedCells && group.Notes.Count == 2 &&
                    group.Notes[0].Hand != group.Notes[1].Hand && group.Notes[0].Direction == group.Notes[1].Direction &&
                    group.Notes.All(note => plan.Source[note.Id].X is 0 or 3 && plan.Source[note.Id].Y > 0) &&
                    last.Any(note => note.HasValue && PairId(note.Value) != null && time - seconds(note.Value.Beat) <= .65);

                return PlacementDomains.JointLayouts(group.Notes, plan.Source, Score,
                    trial => MotionVisibility.ValidSequence(prior, trial, time, anchorIds), anchorIds, repairIds,
                    plan.RepairLargeStacks, plan.ComfortableDoubles, plan.WindupWeight);

                IEnumerable<CellOption> Score(Note note)
                {
                    var controls = plan.Domains[note.Id].Context.Placement.Controls;
                    double inlineWindow = Math.Max(controls["sameHandInlineSeconds"], controls["oppositeInlineSeconds"]);
                    var recent = prior.Where(other => time - other.Time <= inlineWindow).ToArray();
                    var following = index + 1 < timing.Count && seconds(timing[index + 1].Beat) - time <= inlineWindow
                        ? timing[index + 1].Notes : Enumerable.Empty<Note>();
                    int? pair = PairId(note);
                    var previous = last[note.Hand];
                    var occluders = new HashSet<int>();
                    bool legal = false;
                    foreach (var option in options[note.Id])
                    {
                        var candidate = option.Note;
                        double cost = option.ShapeCost;
                        if (previous is { } old)
                        {
                            if (plan.Cues.GetValueOrDefault(note.Id)?.PreviousResetNoteId == old.Id && Cell(old) == Cell(candidate))
                                continue;
                            var delta = new SwingClearance.Point(candidate.X - old.X, candidate.Y - old.Y);
                            cost += .25 * delta.Length;
                            double gap = seconds(candidate.Beat) - seconds(old.Beat);
                            if (!anchorIds.Contains(note.Id) && gap <= .25 && old.Direction is 2 or 3 && candidate.Y > old.Y)
                                cost += controls.GetValueOrDefault("quickHorizontalRiseCost");
                            if (plan.CompactPairedCells && pair == null && PairId(old) != null && gap <= .65)
                                cost += 2.1 * Math.Abs(delta.X);
                        }

                        var blockers = MotionVisibility.LateVisibility(new TimedNote(candidate, time), prior,
                            Array.Empty<TimedWall>(), plan.JumpSpeed, plan.ApproachSeconds).ToArray();
                        if (blockers.Length > 0)
                        {
                            occluders.UnionWith(blockers.Where(anchorIds.Contains));
                            continue;
                        }
                        if (pair.HasValue && cells.TryGetValue(pair.Value, out var sameCell) && sameCell != Cell(candidate))
                            cost += controls.GetValueOrDefault("pairSpacingCost", 3);
                        if (pairedRelease && !anchorIds.Contains(note.Id))
                            cost += 2.5 * (Math.Abs(candidate.X - candidate.Hand - 1) + candidate.Y);
                        cost += recent.Sum(other => InlineCost(candidate, other.Note, controls));
                        foreach (var other in following)
                        {
                            var next = options[other.Id];
                            cost += next.Min(nextOption =>
                            {
                                var nextNote = nextOption.Note;
                                bool overlap = plan.CompactPairedCells && candidate.Hand != nextNote.Hand &&
                                    MotionVisibility.ControllerOverlap(new TimedNote(candidate, time),
                                        new TimedNote(nextNote, seconds(nextNote.Beat)));
                                return nextOption.ShapeCost + InlineCost(candidate, nextNote, controls) + (overlap ? 9 : 0);
                            }) - next.Min(nextOption => nextOption.ShapeCost);
                        }
                        legal = true;
                        yield return option with { ShapeCost = cost };
                    }
                    if (!legal)
                    {
                        bool linked = anchorIds.Contains(note.Id) && occluders.Count > 0;
                        throw new PlacementDeadEnd(group.Beat, group.Notes, "No source-relative geometry",
                            linked ? note.Id : null, linked ? occluders.OrderBy(id => id).ToArray() : null);
                    }
                }
            }

            double InlineCost(Note first, Note second, IReadOnlyDictionary<string, double> controls)
            {
                if (first.X != second.X)
                    return 0;
                bool sameHand = first.Hand == second.Hand;
                double window = controls[sameHand ? "sameHandInlineSeconds" : "oppositeInlineSeconds"];
                double gap = Math.Abs(seconds(first.Beat) - seconds(second.Beat));
                if (gap <= 0 || gap > window)
                    return 0;
                return first.Y == second.Y ? 5 : sameHand ? 0 : 1.5;
            }

            static (double X, double Y) Cell(Note note) => (note.X, note.Y);
        }
    }
}
