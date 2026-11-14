using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record VerifiedRepeat(RepeatWindow[] Occurrences, int[][] Links);

    public static class RepeatVerification
    {
        private const double Epsilon = 1e-6;
        private const double Tolerance = MeterRepeats.Tolerance;

        internal static void AddLink(IDictionary<int, List<int>> partners, int left, int right)
        {
            if (!partners.TryGetValue(left, out var peers))
                partners.Add(left, peers = new List<int> { left });
            peers.Add(right);
        }

        internal static VerifiedRepeat BuildRepeat(IEnumerable<RepeatWindow> members,
            IDictionary<int, List<int>> partners) =>
            new VerifiedRepeat(members.OrderBy(window => window.Start).ThenBy(window => window.End).ToArray(),
                partners.Values.Select(peers => peers.ToArray()).ToArray());

        private static bool Overlaps(RepeatWindow a, RepeatWindow b) =>
            a.Start < b.End - Epsilon && b.Start < a.End - Epsilon;

        public static RepeatWindow[] BookmarkWindows(IEnumerable<double> bookmarks, double end)
        {
            var distinct = new List<double>();
            foreach (double beat in bookmarks.Where(beat => double.IsFinite(beat) && beat >= 0 && beat < end)
                .OrderBy(beat => beat))
            {
                if (distinct.Count == 0 || beat - distinct[^1] > Epsilon)
                    distinct.Add(beat);
            }
            if (distinct.Count < 2)
                return Array.Empty<RepeatWindow>();
            return distinct.Zip(distinct.Skip(1).Append(end), (start, stop) => new RepeatWindow(start, stop))
                .Where(window => window.End - window.Start is >= 8 - Epsilon and <= 128).ToArray();
        }

        public static (int Left, int Right)[] CompareWindows(
            IReadOnlyList<double> beats, IReadOnlyList<int> counts, RepeatWindow first, RepeatWindow second,
            bool local, bool allowExtraBeat)
        {
            double leftSpan = first.End - first.Start, rightSpan = second.End - second.Start;
            double difference = Math.Abs(leftSpan - rightSpan);
            double shorterSpan = Math.Min(leftSpan, rightSpan);
            if (Overlaps(first, second))
                return Array.Empty<(int, int)>();
            if (allowExtraBeat && shorterSpan >= 6 && Math.Abs(difference - 1) <= .12)
            {
                first = first with { End = first.Start + shorterSpan };
                second = second with { End = second.Start + shorterSpan };
            }
            else if (difference > Math.Max(.25, shorterSpan * .05))
                return Array.Empty<(int, int)>();

            int a = MeterRepeats.Bound(beats, first.Start - Epsilon);
            int b = MeterRepeats.Bound(beats, first.End - Epsilon);
            int c = MeterRepeats.Bound(beats, second.Start - Epsilon);
            int d = MeterRepeats.Bound(beats, second.End - Epsilon);
            int split = b - a;
            int size = Math.Max(split, d - c);
            int minimum = local ? 4 : 6;
            int smallerCount = Math.Min(split, d - c);
            if (smallerCount < minimum || smallerCount < .84 * size)
                return Array.Empty<(int, int)>();

            var combined = new double[split + d - c];
            for (int index = a; index < b; index++)
                combined[index - a] = beats[index];
            for (int index = c; index < d; index++)
                combined[split + index - c] = beats[index];
            var pairs = MatchingMarkers(combined, second.Start - first.Start)
                .Where(pair => pair.Left < split && pair.Right >= split)
                .Select(pair => (Left: a + pair.Left, Right: c + pair.Right - split)).ToArray();
            var aligned = pairs.Where(pair => counts[pair.Left] == counts[pair.Right]).ToArray();
            if (pairs.Length < .88 * size || aligned.Length < Math.Max(minimum, .80 * size))
                return Array.Empty<(int, int)>();

            bool variedGaps = pairs.Zip(pairs.Skip(1), (left, right) =>
                Math.Round((beats[right.Left] - beats[left.Left]) * 8)).Distinct().Take(2).Count() == 2;
            bool variedCounts = aligned.Select(pair => counts[pair.Left]).Distinct().Take(2).Count() == 2;
            return variedGaps || !local && variedCounts ? aligned : Array.Empty<(int, int)>();
        }

        internal static IEnumerable<(int Left, int Right)> MatchingMarkers(double[] beats, double offset, int[] counts = null)
        {
            int right = 0;
            for (int left = 0; left < beats.Length; left++)
            {
                double target = beats[left] + offset;
                right = MeterRepeats.Bound(beats, target - Tolerance, first: right);
                if (right == beats.Length)
                    yield break;
                int end = MeterRepeats.Bound(beats, target + Tolerance, inclusive: true, first: right);
                if (right == end)
                    continue;

                int partner = -1;
                for (int index = right; index < end; index++)
                    if ((counts == null || counts[index] == counts[left]) &&
                        (partner < 0 || Math.Abs(beats[index] - target) < Math.Abs(beats[partner] - target)))
                        partner = index;
                if (partner < 0)
                    continue;

                double expected = beats[partner] - offset;
                int first = MeterRepeats.Bound(beats, expected - Tolerance);
                int last = MeterRepeats.Bound(beats, expected + Tolerance, inclusive: true, first: first);
                bool hasCloserHit = false;
                for (int index = first; index < last && !hasCloserHit; index++)
                    hasCloserHit = (counts == null || counts[index] == counts[left]) &&
                        Math.Abs(beats[index] - expected) < Math.Abs(beats[left] - expected);
                if (hasCloserHit)
                    continue;
                yield return (left, partner);
                right = partner + 1;
            }
        }

        public static IEnumerable<VerifiedRepeat> VerifyGroups(
            IReadOnlyList<double> beats, IReadOnlyList<int> counts,
            IEnumerable<(RepeatWindow First, RepeatWindow Second)> candidates,
            bool local = false, bool allowExtraBeat = true,
            Func<RepeatWindow, double> boundaryEvidence = null, int minimumOccurrences = 2)
        {
            var matches = new Dictionary<(RepeatWindow, RepeatWindow), (int Left, int Right)[]>();
            var neighbors = new Dictionary<RepeatWindow, HashSet<RepeatWindow>>();
            foreach (var (first, second) in candidates.Distinct())
            {
                var pairs = CompareWindows(beats, counts, first, second, local, allowExtraBeat);
                if (pairs.Length == 0)
                    continue;
                matches.Add((first, second), pairs);
                AddNeighbor(first, second);
                AddNeighbor(second, first);
            }

            var occupied = new List<RepeatWindow>();
            foreach (var match in matches.OrderByDescending(pair => pair.Value.Length * (boundaryEvidence == null ? 1 :
                1 + boundaryEvidence(pair.Key.Item1) + boundaryEvidence(pair.Key.Item2)))
                .ThenBy(pair => pair.Key.Item1.Start).ThenBy(pair => pair.Key.Item1.End)
                .ThenBy(pair => pair.Key.Item2.Start).ThenBy(pair => pair.Key.Item2.End))
            {
                var (first, second) = match.Key;
                if (occupied.Any(window => Overlaps(first, window) || Overlaps(second, window)))
                    continue;
                var members = new List<RepeatWindow> { first, second };
                foreach (var candidate in neighbors[first].Intersect(neighbors[second])
                    .OrderBy(window => window.Start).ThenBy(window => window.End))
                {
                    if (members.All(member => neighbors[member].Contains(candidate)) &&
                        !occupied.Any(window => Overlaps(candidate, window)))
                        members.Add(candidate);
                }

                if (members.Count < minimumOccurrences)
                    continue;
                var partners = new SortedDictionary<int, List<int>>();
                foreach (var member in members.Skip(1))
                {
                    bool forward = matches.TryGetValue((first, member), out var pairs);
                    pairs ??= matches[(member, first)];
                    foreach (var pair in pairs)
                    {
                        int left = forward ? pair.Left : pair.Right;
                        int right = forward ? pair.Right : pair.Left;
                        AddLink(partners, left, right);
                    }
                }
                occupied.AddRange(members);
                yield return BuildRepeat(members, partners);
            }

            void AddNeighbor(RepeatWindow from, RepeatWindow to)
            {
                if (!neighbors.TryGetValue(from, out var peers))
                    neighbors.Add(from, peers = new HashSet<RepeatWindow>());
                peers.Add(to);
            }
        }
    }
}

