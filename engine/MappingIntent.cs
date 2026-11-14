using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal sealed record PairedGesture(int Pair, Note First, Note Second);
    internal sealed record BombReturn(Note First, Note Second, IReadOnlyList<int> Cues);
    internal sealed record ResetExit(Note Note, int Direction, double? X = null, double? Y = null);
    internal sealed record SourceGestures(
        IReadOnlyList<PairedGesture> Pairs, IReadOnlyList<BombReturn> Returns, IReadOnlyList<ResetExit> Exits)
    {
        internal HashSet<int> ResetNotes() => Returns.SelectMany(reset => new[] { reset.First.Id, reset.Second.Id })
            .Concat(Exits.Select(exit => exit.Note.Id)).ToHashSet();
    }

    internal static class MappingIntent
    {
        private static readonly int[] Angles = { 90, 270, 180, 0, 135, 45, 225, 315 };
        private static readonly int[] Opposite = { 1, 0, 3, 2, 7, 6, 5, 4 };

        internal static SourceGestures Detect(IReadOnlyList<Note> notes, IReadOnlyList<Bomb> bombs,
            ChartClock clock, ISet<int> linked)
        {
            var pairs = PairedCalls(notes, clock, linked).ToArray();
            var claimed = pairs.SelectMany(pair => new[] { pair.First.Id, pair.Second.Id }).ToHashSet();
            var returns = new List<BombReturn>();
            var hands = new[] { 0, 1 }.Select(hand =>
                NestedTimings.Group(notes.Where(note => note.Hand == hand))).ToArray();
            foreach (var swings in hands)
            for (int index = 1; index < swings.Count; index++)
            {
                if (swings[index - 1].Notes.Count != 1 || swings[index].Notes.Count != 1)
                    continue;
                var first = swings[index - 1].Notes[0];
                var second = swings[index].Notes[0];
                double gap = clock.Gap(first.Beat, second.Beat);
                if (first.Direction != second.Direction || first.Angle != second.Angle || first.Direction == 8 ||
                    first.X == second.X && first.Y == second.Y || gap < .18 || gap > 1 ||
                    linked.Contains(first.Id) || linked.Contains(second.Id))
                    continue;
                var cues = Enumerable.Range(0, bombs.Count).Where(i =>
                    first.Beat < bombs[i].Beat && bombs[i].Beat < second.Beat &&
                    new SwingClearance.Point(bombs[i].X - first.X, bombs[i].Y - first.Y).Length <= .51).ToArray();
                if (cues.Length == 0)
                    continue;
                returns.Add(new BombReturn(first, second, cues));
                claimed.Add(first.Id);
                claimed.Add(second.Id);
            }

            var exits = new List<ResetExit>();
            foreach (var reset in returns)
            {
                var swings = hands[reset.Second.Hand];
                int index = swings.FindIndex(group => group.Notes.Any(note => note.Id == reset.Second.Id));
                if (index + 1 >= swings.Count || swings[index + 1].Notes.Count != 1)
                    continue;
                var release = swings[index + 1].Notes[0];
                var end = reset.Second;
                if (linked.Contains(release.Id) || claimed.Contains(release.Id) || release.Direction != end.Direction ||
                    bombs.Any(bomb => end.Beat < bomb.Beat && bomb.Beat < release.Beat))
                    continue;
                int target = Opposite[(int)end.Direction];
                double dx = end.X - reset.First.X, dy = end.Y - reset.First.Y;
                exits.Add(new ResetExit(release, target, Math.Clamp(reset.First.X - dx, 0, 3),
                    Math.Clamp(dy != 0 ? reset.First.Y - dy : Up(target) ? 1 : 0, 0, 1)));
                claimed.Add(release.Id);
                if (index + 2 >= swings.Count || swings[index + 2].Notes.Count != 1)
                    continue;
                var tail = swings[index + 2].Notes[0];
                if (!linked.Contains(tail.Id) && !claimed.Contains(tail.Id) &&
                    clock.Gap(release.Beat, tail.Beat) <= .5 && Up(tail.Direction) == Up(target))
                {
                    exits.Add(new ResetExit(tail, Up(target) ? 1 : 0));
                    claimed.Add(tail.Id);
                }
            }
            return new SourceGestures(pairs, returns, exits);
        }

        private static IEnumerable<PairedGesture> PairedCalls(
            IReadOnlyList<Note> notes, ChartClock clock, ISet<int> linked)
        {
            var groups = NestedTimings.Group(notes);
            int pairId = 0;
            for (int first = 0; first < groups.Count - 3;)
            {
                var pairs = new List<(Note First, Note Second)>();
                int end = first;
                double? interval = null;
                while (end + 1 < groups.Count && groups[end].Notes.Count == 1 && groups[end + 1].Notes.Count == 1)
                {
                    var a = groups[end].Notes[0];
                    var b = groups[end + 1].Notes[0];
                    double gap = b.Beat - a.Beat;
                    if (linked.Contains(a.Id) || linked.Contains(b.Id) || a.Hand != b.Hand ||
                        !Vertical(a.Direction) || !Vertical(b.Direction) || Rotation(a, b) > 45 ||
                        gap <= 0 || gap > .75 || clock.Gap(a.Beat, b.Beat) > .5)
                        break;
                    interval ??= gap;
                    if (Math.Abs(gap - interval.Value) > .0001 || pairs.Count > 0 &&
                        (pairs[^1].First.Hand == a.Hand || Math.Abs(a.Beat - pairs[^1].Second.Beat - interval.Value) > .0001))
                        break;
                    pairs.Add((a, b));
                    end += 2;
                }
                if (pairs.Count < 2)
                {
                    first++;
                    continue;
                }
                foreach (var pair in pairs)
                    yield return new PairedGesture(pairId++, pair.First, pair.Second);
                first = end;
            }
        }

        private static double Rotation(Note a, Note b)
        {
            double difference = Angles[(int)b.Direction] + b.Angle - Angles[(int)a.Direction] - a.Angle;
            return Math.Abs((difference % 360 + 360) % 360 - 180);
        }

        private static bool Vertical(double direction) => direction is 0 or 1 or 4 or 5 or 6 or 7;
        private static bool Up(double direction) => direction is 0 or 4 or 5;
    }
}
