using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public readonly record struct RepeatWindow(double Start, double End);

    public static class MeterRepeats
    {
        private const int PhaseSteps = 24;
        private const int BlockLength = 16;
        internal const double Tolerance = .05;
        private static readonly int[] BarPeriods = { 2, 3, 4, 6 };
        private static readonly int[] WindowBars = { 2, 4, 8 };

        internal static int Bound(IReadOnlyList<double> beats, double beat, bool inclusive = false, int first = 0)
        {
            int last = beats.Count;
            while (first < last)
            {
                int middle = first + (last - first) / 2;
                if (beats[middle] < beat || inclusive && beats[middle] == beat)
                    first = middle + 1;
                else
                    last = middle;
            }
            return first;
        }

        public static IReadOnlyDictionary<double, HashSet<int>> LocalDownbeats(
            IReadOnlyList<double> beats, IReadOnlyList<int> counts)
        {
            if (beats.Count != counts.Count)
                throw new ArgumentException("Every beat must have a note count.", nameof(counts));

            var downbeats = new Dictionary<double, HashSet<int>>();
            if (beats.Count == 0)
                return downbeats;

            var accents = beats.Select((beat, index) => 1 + .55 * Math.Min(2, counts[index] - 1) +
                (index == 0 ? 0 : .65 * Math.Min(2, beat - beats[index - 1]))).ToArray();
            double firstBlock = Math.Floor(beats[0] / BlockLength) * BlockLength;
            for (double start = firstBlock; start <= Math.Floor(beats[^1]); start += BlockLength)
            {
                int first = Bound(beats, start - 8);
                int end = Bound(beats, start + BlockLength + 8);
                if (end - first < 8)
                    continue;

                double mean = accents[first..end].Average();
                foreach (int period in BarPeriods)
                {
                    int phaseCount = period * PhaseSteps;
                    var barsByPhase = new Dictionary<int, Dictionary<int, double>>();
                    for (int index = first; index < end; index++)
                    {
                        int step = (int)Math.Round(beats[index] * PhaseSteps);
                        int phase = (step % phaseCount + phaseCount) % phaseCount;
                        int bar = (step - phase) / phaseCount;
                        if (!barsByPhase.TryGetValue(phase, out var bars))
                            barsByPhase.Add(phase, bars = new Dictionary<int, double>());
                        bars[bar] = Math.Max(accents[index], bars.GetValueOrDefault(bar));
                    }

                    double minimumBars = Math.Max(3, (beats[end - 1] - beats[first]) / period * .50);
                    var ranked = barsByPhase.Where(pair => pair.Value.Count >= minimumBars)
                        .Select(pair => (Phase: pair.Key, Bars: pair.Value.Count, Strength: pair.Value.Values.Average()))
                        .Where(candidate => candidate.Strength >= mean + .06)
                        .OrderByDescending(candidate => candidate.Strength).ThenByDescending(candidate => candidate.Bars)
                        .ThenByDescending(candidate => candidate.Phase).Take(2).ToArray();
                    foreach (var candidate in ranked)
                    {
                        if (candidate.Strength < ranked[0].Strength - .20)
                            continue;
                        double origin = (double)candidate.Phase / PhaseSteps;
                        double firstBar = Math.Ceiling((start - origin) / period);
                        double endBar = Math.Ceiling((start + BlockLength - origin) / period);
                        for (double bar = firstBar; bar < endBar; bar++)
                        {
                            double beat = origin + bar * period;
                            if (beat < beats[0] - period || beat > beats[^1])
                                continue;
                            if (!downbeats.TryGetValue(beat, out var periods))
                                downbeats.Add(beat, periods = new HashSet<int>());
                            periods.Add(period);
                        }
                    }
                }
            }
            return downbeats;
        }

        public static IEnumerable<(RepeatWindow First, RepeatWindow Second)> RepeatWindows(
            IReadOnlyList<double> beats, IReadOnlyDictionary<double, HashSet<int>> downbeats)
        {
            var starts = downbeats.Keys.OrderBy(beat => beat).ToArray();
            var offsets = LikelyOffsets(beats).Concat(ShortAnchorOffsets(beats)).Distinct().ToArray();
            foreach (double first in starts)
            foreach (double offset in offsets)
            {
                double expected = first + offset;
                int right = Bound(starts, expected - Tolerance);
                int end = Bound(starts, expected + Tolerance, inclusive: true, first: right);
                for (int index = right; index < end; index++)
                {
                    double second = starts[index];
                    var lengths = downbeats[first].Intersect(downbeats[second])
                        .SelectMany(period => WindowBars.Select(bars => period * bars)).Distinct().OrderBy(length => length);
                    foreach (int length in lengths)
                    {
                        if (length > second - first + Tolerance || second + length > beats[^1] + 1.05)
                            continue;
                        double firstEnd = Math.Abs(second - first - length - 1) <= Tolerance ? second : first + length;
                        yield return (new RepeatWindow(first, firstEnd), new RepeatWindow(second, second + length));
                    }
                }
            }
        }

        internal static IEnumerable<double> LikelyOffsets(IReadOnlyList<double> beats)
        {
            var votes = new Dictionary<int, int>();
            var occurrences = new Dictionary<(int, int, int, int), List<double>>();
            var gaps = Gaps(beats);
            for (int index = 0; index + 3 < gaps.Length; index++)
            {
                int[] slice = gaps[index..(index + 4)];
                if (slice.Distinct().Count() < 2 || slice.Max() > 32)
                    continue;
                var signature = (slice[0], slice[1], slice[2], slice[3]);
                if (!occurrences.TryGetValue(signature, out var starts))
                    occurrences.Add(signature, starts = new List<double>());
                foreach (double previous in starts.TakeLast(48))
                {
                    double offset = beats[index] - previous;
                    if (offset < 6)
                        continue;
                    int key = (int)Math.Round(offset * PhaseSteps);
                    votes[key] = votes.GetValueOrDefault(key) + 1;
                }
                starts.Add(beats[index]);
            }
            return votes.OrderByDescending(pair => pair.Value).Take(72).Where(pair => pair.Value >= 3)
                .Select(pair => (double)pair.Key / PhaseSteps);
        }

        internal static IEnumerable<double> ShortAnchorOffsets(IReadOnlyList<double> beats)
        {
            var occurrences = new Dictionary<(int, int), List<double>>();
            var gaps = Gaps(beats);
            for (int index = 0; index + 1 < gaps.Length; index++)
            {
                int first = gaps[index], second = gaps[index + 1];
                if (Math.Min(first, second) <= 0 || first == second || Math.Max(first, second) > 32)
                    continue;
                var signature = (first, second);
                if (!occurrences.TryGetValue(signature, out var starts))
                    occurrences.Add(signature, starts = new List<double>());
                starts.Add(beats[index]);
            }
            var votes = new Dictionary<int, Dictionary<(int, int), int>>();
            foreach (var (signature, starts) in occurrences)
            for (int index = 0; index < starts.Count; index++)
            for (int earlier = Math.Max(0, index - 48); earlier < index; earlier++)
            {
                double offset = starts[index] - starts[earlier];
                if (offset < 12 - Tolerance)
                    continue;
                int key = (int)Math.Round(offset * PhaseSteps);
                if (!votes.TryGetValue(key, out var counts))
                    votes.Add(key, counts = new Dictionary<(int, int), int>());
                counts[signature] = counts.GetValueOrDefault(signature) + 1;
            }
            return votes.Where(pair => pair.Value.Count >= 2 && pair.Value.Values.Sum() >= 4)
                .OrderByDescending(pair => pair.Value.Values.Sum(count => Math.Min(count, 8)))
                .ThenByDescending(pair => pair.Value.Count).ThenByDescending(pair => pair.Key)
                .Take(72).Select(pair => (double)pair.Key / PhaseSteps);
        }

        private static int[] Gaps(IReadOnlyList<double> beats) => beats.Skip(1)
            .Select((beat, index) => (int)Math.Round((beat - beats[index]) * 8)).ToArray();
    }
}
