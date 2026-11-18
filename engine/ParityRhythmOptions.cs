using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal static class ParityRhythmOptions
    {
        internal sealed record Edit(double Beat, int? Remove, Note? Add, double Cost);
        internal sealed record Option(IReadOnlyList<Note> Notes, double Cost, int EditCount);

        internal static IEnumerable<Option> CompoundDirectionRepair(StandardLevel level, IReadOnlyList<Note> seeds,
            double beat, int hand, ISet<int> protectedIds)
        {
            var authored = NestedTimings.Group(level.Gameplay.Notes).ToDictionary(group => group.Beat);
            var attacks = NestedTimings.Group(seeds);
            var singles = attacks.Where(group => group.Beat < beat).TakeLast(5)
                .Concat(attacks.Where(group => group.Beat >= beat).Take(5))
                .Where(group => group.Notes.Count == 1 && !protectedIds.Contains(group.Notes[0].Id)).ToArray();
            int nextId = Math.Max(seeds.Max(note => note.Id), level.Gameplay.Notes.Max(note => note.Id));
            var doubles = new List<(Note Single, Note Added)>();
            foreach (var group in singles)
            {
                var single = group.Notes[0];
                var counterpart = authored.GetValueOrDefault(group.Beat)?.Notes
                    .Where(note => note.Hand != single.Hand).Select(note => (Note?)note).FirstOrDefault();
                if (counterpart.HasValue)
                    doubles.Add((single, counterpart.Value with { Id = ++nextId }));
            }
            foreach (var first in doubles.Where(item => item.Single.Beat < beat))
            foreach (var last in doubles.Where(item => item.Single.Beat > beat))
            foreach (var swapped in singles.Select(group => group.Notes[0]).Where(note =>
                note.Beat > first.Single.Beat && note.Beat < beat && note.Hand != hand)
                .OrderBy(note => Math.Abs(beat - note.Beat)))
            {
                var replacement = swapped with { Hand = hand, X = 3 - swapped.X, Angle = -swapped.Angle,
                    Direction = swapped.Direction switch { 2 => 3, 3 => 2, 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => swapped.Direction } };
                yield return new Option(seeds.Select(note => note.Id == swapped.Id ? replacement : note)
                    .Append(first.Added).Append(last.Added).OrderBy(note => note.Beat).ThenBy(note => note.Hand)
                    .ToArray(), Math.Abs(beat - first.Single.Beat) + Math.Abs(last.Single.Beat - beat), 3);
            }
        }

        internal static IEnumerable<Option> PrecedingAttackEdits(IReadOnlyList<Note> notes, double beat,
            int hand, ISet<int> protectedIds, int sourceMaxId, IReadOnlyList<Note> foundation)
        {
            int id = Math.Max(sourceMaxId, notes.Max(note => note.Id)) + 1;
            var approaches = NestedTimings.Group(notes).Where(group => group.Beat < beat - .0001)
                .TakeLast(2).Reverse();
            foreach (var attack in approaches)
            {
                if (attack.Notes.Count == 2 && attack.Notes.Select(note => note.Hand).Distinct().Count() == 2)
                {
                    var removed = attack.Notes.Single(note => note.Hand == hand);
                    if (!protectedIds.Contains(removed.Id))
                        foreach (var option in WithExitCuts(notes.Where(note => note.Id != removed.Id), removed.Beat))
                            yield return option;
                }
                if (attack.Notes.Count != 1 || attack.Notes[0].Hand == hand)
                    continue;
                var single = attack.Notes[0];
                var authored = foundation.Where(note => note.Hand == hand && Math.Abs(note.Beat - single.Beat) <= .0001)
                    .Select(note => (Note?)note).FirstOrDefault();
                if (authored.HasValue)
                    foreach (var option in WithExitCuts(notes.Append(authored.Value with { Id = id, Beat = single.Beat })
                        .OrderBy(note => note.Beat).ThenBy(note => note.Hand), single.Beat))
                        yield return option;
                if (single.Direction is >= 4 and <= 7)
                {
                    // A parallel double can keep the original attack's phrasing.
                    // Placement validates each candidate cell against walls and bombs.
                    foreach (int x in Enumerable.Range(0, 4).Where(x => x != single.X)
                        .OrderBy(x => Math.Abs(x - single.X)))
                    {
                        var addition = single with { Id = id, Hand = hand, X = x, Y = 0 };
                        yield return new Option(notes.Append(addition).OrderBy(note => note.Beat)
                            .ThenBy(note => note.Hand).ToArray(), .25 * Math.Abs(x - single.X), 1);
                    }
                }
                if (!protectedIds.Contains(single.Id))
                {
                    foreach (int cut in new[] { 0, 1, (int)single.Direction }.Distinct())
                    foreach (double x in new[] { single.X, hand + 1d, 3 - single.X }.Distinct())
                    {
                        var swapped = single with { Hand = hand, Direction = cut, X = x, Angle = 0 };
                        foreach (var option in WithExitCuts(notes.Select(note => note.Id == single.Id ? swapped : note), single.Beat))
                            yield return option;
                    }
                }
                var adjustment = notes.Where(note => note.Hand == hand && note.Beat > single.Beat + .0001)
                    .Take(3).ToArray();
                foreach (int direction in new[] { 0, 1 })
                foreach (int changes in new[] { 0, 1, 2, 3, 6, 7 })
                {
                    var changed = adjustment.Where((note, index) => (changes & (1 << index)) != 0).ToArray();
                    if (changed.Any(note => protectedIds.Contains(note.Id) || note.Direction is < 0 or > 7))
                        continue;
                    var edits = changed.ToDictionary(note => note.Id, note => note with
                    {
                        Direction = note.Direction < 2 ? (int)note.Direction ^ 1 :
                            (direction + Array.IndexOf(adjustment, note) + 1) % 2,
                        Angle = 0,
                    });
                    foreach (int x in new[] { (int)single.X + (hand == 0 ? -1 : 1), hand + 1, hand == 0 ? 0 : 3 }.Distinct())
                    {
                        if (x < 0 || x > 3 || x == single.X)
                            continue;
                        var addition = single with { Id = id, Hand = hand, X = x, Direction = direction, Angle = 0 };
                        yield return new Option(notes.Select(note => edits.GetValueOrDefault(note.Id, note)).Append(addition)
                            .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray(),
                            changed.Length + .25 * Math.Abs(x - single.X), changed.Length + 1);
                    }
                }
            }

            IEnumerable<Option> WithExitCuts(IEnumerable<Note> edited, double attackBeat)
            {
                var candidate = edited.ToArray();
                yield return new Option(candidate, 0, 1);
                var exit = candidate.FirstOrDefault(note => note.Hand == hand && note.Beat > attackBeat + .0001);
                if (exit.Beat <= attackBeat || exit.Direction is < 0 or > 7 || protectedIds.Contains(exit.Id))
                    yield break;
                foreach (int cut in new[] { 0, 1 }.Where(cut => cut != exit.Direction))
                    yield return new Option(candidate.Select(note => note.Id == exit.Id
                        ? note with { Direction = cut, Angle = 0 } : note).ToArray(), 1, 2);
            }
        }

        internal static IEnumerable<Option> Around(PreparedTiming timing, IReadOnlyList<Note> placed,
            double beat, int hand, ISet<int> protectedIds, ISet<int> resetIds, bool splitStacks = false,
            int windowRadius = 4)
        {
            var level = timing.Level;
            var clock = level.Clock;
            var groups = NestedTimings.Group(placed);
            var authored = NestedTimings.Group(level.Gameplay.Notes).ToDictionary(group => group.Beat);
            int nextId = Math.Max(level.Gameplay.Notes.Max(note => note.Id), placed.Max(note => note.Id));
            var edits = new List<Edit>();
            var neighborhood = groups.Where(group => group.Beat < beat).TakeLast(windowRadius)
                .Concat(groups.Where(group => Math.Abs(group.Beat - beat) <= .0001))
                .Concat(groups.Where(group => group.Beat > beat).Take(windowRadius)).ToArray();
            for (int index = 0; index < neighborhood.Length; index++)
            {
                var group = neighborhood[index];
                double start = Math.Min(beat, group.Beat), end = Math.Max(beat, group.Beat);
                if (windowRadius > 4 && group.Notes.Count == 1 &&
                    level.Gameplay.Bombs.Any(bomb => Math.Abs(bomb.Beat - group.Notes[0].Beat) <= .0001))
                {
                    var singleWithBomb = group.Notes[0];
                    int handToAdd = 1 - singleWithBomb.Hand;
                    var setup = singleWithBomb with { Id = ++nextId, Hand = handToAdd,
                        X = handToAdd + 1, Y = 0, Direction = 0, Angle = 0 };
                    // Keep the original attack and let placement check the additional swing.
                    edits.Add(new Edit(group.Beat, null, setup, .75 + Math.Abs(clock.Gap(beat, group.Beat))));
                }
                if (group.Notes.Any(note => protectedIds.Contains(note.Id) || resetIds.Contains(note.Id)))
                    continue;
                double cost = neighborhood.Count(other =>
                    other.Beat > start && other.Beat <= end) * .20 +
                    Math.Min(4, Math.Abs(clock.Gap(beat, group.Beat))) * .10;
                if (splitStacks && group.Notes.Count == 2 && group.Notes[0].Hand == group.Notes[1].Hand)
                    foreach (var note in group.Notes)
                    {
                        edits.Add(new Edit(group.Beat, note.Id, null, .55 + cost));
                        var mirrored = note with { Id = ++nextId, Hand = 1 - note.Hand, X = 3 - note.X,
                            Angle = -note.Angle, Direction = note.Direction switch
                            { 2 => 3, 3 => 2, 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => note.Direction } };
                        edits.Add(new Edit(group.Beat, note.Id, mirrored, .8 + cost));
                    }
                if (group.Notes.Count == 2 && group.Notes[0].Hand != group.Notes[1].Hand)
                {
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
                edits.Add(complete);
                edits.Add(new Edit(group.Beat, single.Id, opposite, cost + 1.2 + sourceCost));
            }
            var ordered = windowRadius <= 4
                ? edits.OrderBy(edit => edit.Cost).ThenBy(edit => edit.Beat).Take(20).ToArray()
                : edits.GroupBy(edit => edit.Beat)
                    .SelectMany(group => group.OrderBy(edit => edit.Cost).Take(2)).ToArray();
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
