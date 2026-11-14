using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal static class ParityRhythmOptions
    {
        internal sealed record Edit(double Beat, int? Remove, Note? Add, double Cost);
        internal sealed record Option(IReadOnlyList<Note> Notes, double Cost, int EditCount);

        internal static IEnumerable<Option> Around(PreparedTiming timing, IReadOnlyList<Note> placed,
            double beat, int hand, ISet<int> protectedIds, ISet<int> resetIds)
        {
            var level = timing.Level;
            var clock = level.Clock;
            var groups = NestedTimings.Group(placed);
            var authored = NestedTimings.Group(level.Gameplay.Notes).ToDictionary(group => group.Beat);
            int nextId = Math.Max(level.Gameplay.Notes.Max(note => note.Id), placed.Max(note => note.Id));
            var edits = new List<Edit>();
            
            foreach (var group in groups.Where(group => Math.Abs(clock.Gap(beat, group.Beat)) <= 1.75))
            {
                double start = Math.Min(beat, group.Beat), end = Math.Max(beat, group.Beat);
                if (group.Notes.Any(note => protectedIds.Contains(note.Id)) ||
                    placed.Any(note => note.Beat >= start && note.Beat <= end && resetIds.Contains(note.Id)) ||
                    level.Gameplay.Bombs.Any(bomb => bomb.Beat >= start && bomb.Beat <= end))
                    continue;
                double cost = Math.Abs(clock.Gap(beat, group.Beat)) * .4;
                bool local = Math.Abs(clock.Gap(beat, group.Beat)) <= 1.75;
                if (group.Notes.Count == 2 && group.Notes[0].Hand != group.Notes[1].Hand)
                {
                    if (local)
                        foreach (var note in group.Notes)
                            edits.Add(new Edit(group.Beat, note.Id, null, cost + (note.Hand == hand ? 1 : 1.4) +
                                (authored.GetValueOrDefault(group.Beat)?.Notes.Select(hit => hit.Hand).Distinct().Count() == 2 ? .3 : 0)));
                }
                if (group.Notes.Count != 1)
                    continue;
                var single = group.Notes[0];
                var source = authored.GetValueOrDefault(group.Beat)?.Notes
                    .Where(note => note.Hand != single.Hand).Select(note => (Note?)note).FirstOrDefault();
                var opposite = source ?? single with
                {
                    Hand = 1 - single.Hand, X = 3 - single.X, Angle = -single.Angle,
                    Direction = single.Direction switch { 2 => 3, 3 => 2, 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => single.Direction },
                };
                opposite = opposite with { Id = ++nextId };
                double sourceCost = source.HasValue ? 0 : .35;
                var complete = new Edit(group.Beat, null, opposite, cost + 1 + sourceCost);
                if (local)
                {
                    edits.Add(complete);
                    edits.Add(new Edit(group.Beat, single.Id, opposite, cost + 1.2 + sourceCost));
                }
            }
            var ordered = edits.OrderBy(edit => edit.Cost).ThenBy(edit => edit.Beat).Take(20).ToArray();
            foreach (var edit in ordered)
                yield return Apply(edit);
            for (int i = 0; i < ordered.Length; i++)
            for (int j = i + 1; j < ordered.Length; j++)
                if (ordered[i].Beat != ordered[j].Beat)
                    yield return Apply(ordered[i], ordered[j]);
            // Small phase changes can require three swings to reconnect the exit.
            foreach (var window in ordered.GroupBy(edit => edit.Beat).OrderBy(group => group.Key)
                .Select(group => group.ToArray()).WindowedTriples())
            foreach (var first in window.First)
            foreach (var middle in window.Middle)
            foreach (var last in window.Last)
                yield return Apply(first, middle, last);

            Option Apply(params Edit[] changes)
            {
                var removed = changes.Where(edit => edit.Remove.HasValue).Select(edit => edit.Remove.Value).ToHashSet();
                var added = changes.Where(edit => edit.Add.HasValue).Select(edit => edit.Add.Value);
                return new Option(placed.Where(note => !removed.Contains(note.Id)).Concat(added)
                    .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray(), changes.Sum(edit => edit.Cost), changes.Length);
            }
        }

        private static IEnumerable<(Edit[] First, Edit[] Middle, Edit[] Last)> WindowedTriples(
            this IEnumerable<Edit[]> groups)
        {
            var ordered = groups.ToArray();
            for (int i = 0; i + 2 < ordered.Length; i++)
                yield return (ordered[i], ordered[i + 1], ordered[i + 2]);
        }
    }
}
