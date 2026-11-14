using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal sealed record DirectionDomain(int[] Directions, CellRepair Repair, bool LinkedDirectionRepair);

    internal static class DirectionDomains
    {
        internal static IReadOnlyDictionary<int, DirectionDomain> Build(
            IReadOnlyList<Note> notes, PlacementPlan plan, IReadOnlyDictionary<int, int[]> allowed,
            ISet<int> clearance, bool repairEmptyDomains)
        {
            var domains = new Dictionary<int, DirectionDomain>();
            var anchors = notes.Where(note => plan.Domains[note.Id].LinkedAnchor)
                .Select(note => new TimedNote(note, plan.Seconds(note.Beat))).ToArray();
            var walls = plan.Walls.Select(wall => new TimedWall(wall, plan.Seconds(wall.Beat),
                plan.Seconds(wall.Beat + wall.Duration))).ToArray();
            var stack = notes.GroupBy(note => note.Hand).SelectMany(hand => NestedTimings.Group(hand)
                .Where(group => group.Notes.Count > 1).SelectMany(group => group.Notes))
                .Select(note => note.Id).ToHashSet();
            foreach (var note in notes)
            {
                var domain = plan.Domains[note.Id];
                if (domain.LinkedAnchor)
                {
                    domains.Add(note.Id, new DirectionDomain(new[] { (int)note.Direction }, CellRepair.None, false));
                    continue;
                }
                double time = plan.Seconds(note.Beat);
                var nearby = walls.Where(wall => wall.Start <= time + plan.ApproachSeconds &&
                    wall.End >= time - plan.ApproachSeconds).ToArray();
                var anchorNeighbors = anchors.Where(anchor => Math.Abs(anchor.Note.Beat - note.Beat) <= .0001 ||
                    anchor.Note.Hand != note.Hand && Math.Abs(anchor.Time - time) <= 2 * MotionVisibility.CutHalfDuration).ToArray();
                int[] possible = allowed.GetValueOrDefault(note.Id) ??
                    (note.Direction == 8 ? new[] { 8 } : Enumerable.Range(0, 8).ToArray());
                var repairs = new[] { CellRepair.None };
                if (repairEmptyDomains)
                {
                    CellRepair fallback = domain.PreservePosition &&
                        plan.Cues.GetValueOrDefault(note.Id)?.Intent?.Kind == "bomb_return_reset" ?
                        CellRepair.ResetCell : CellRepair.Clearance;
                    repairs = clearance.Contains(note.Id) && !domain.PreservePosition ?
                        new[] { stack.Contains(note.Id) ? CellRepair.StackClearance : CellRepair.Clearance } :
                        new[] { CellRepair.None, fallback };
                }
                foreach (var repair in repairs)
                {
                    var directions = possible.Where(direction =>
                    {
                        var candidate = note with { Direction = direction, Angle = direction == note.Direction ? note.Angle : 0 };
                        return PlacementDomains.Enumerate(candidate, plan.Source[note.Id], domain with { Repair = repair },
                            plan.Cues.GetValueOrDefault(note.Id)?.Intent, plan.Seconds, MotionVisibility.Reach, _ => 0,
                            trial => anchorNeighbors.Any(anchor =>
                                MotionVisibility.BlocksLinkedSwing(new TimedNote(trial, time), anchor)),
                            trial => MotionVisibility.LateVisibility(new TimedNote(trial, time),
                                Array.Empty<TimedNote>(), nearby, plan.JumpSpeed, plan.ApproachSeconds).Any()).Any();
                    }).ToArray();
                    if (directions.Length == 0)
                        continue;
                    bool linkedRepair = !directions.Contains((int)note.Direction) &&
                        anchorNeighbors.Any(anchor => Math.Abs(anchor.Note.Beat - note.Beat) <= .0001);
                    domains.Add(note.Id, new DirectionDomain(directions, repair, linkedRepair));
                    break;
                }
                if (!domains.ContainsKey(note.Id))
                    throw new PlacementDeadEnd(note.Beat, new[] { note }, "No geometric direction domain");
            }
            return domains;
        }
    }
}
