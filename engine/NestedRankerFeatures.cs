using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BsDownmap.Engine
{
    public sealed record TimingFeatures(double[] Beats, double[][] Rows);

    public static class NestedRankerFeatures
    {
        private const int BaseColumns = 31;
        private const int MotifColumns = 9;
        private const int PhaseColumns = 5;
        private const int Mix = 25, MixRelative = 26, Baseline = 27, TwoHands = 18, Count = 17, SecondsPerBeat = 7;
        private static readonly int[] Recurrences = { -16, -8, -4, 4, 8, 16 };
        private static readonly int[] PeerColumns = { Count, TwoHands, Mix, MixRelative, Baseline };
        private static readonly double[] Phases = { .5, 1.0 / 3, 2.0 / 3, .25 };

        public static TimingFeatures Build(
            IReadOnlyList<Note> notes, IReadOnlyList<Note> baseline, ISet<int> protectedIds,
            IReadOnlyDictionary<double, double> mix, Func<double, double> seconds, Func<double, double> toGrid,
            Func<double, double> gridSeconds, double pulse, CancellationToken cancellation = default)
        {
            var groups = NestedTimings.Group(notes);
            var raw = groups.Select(group => group.Beat).ToArray();
            if (raw.Length == 0)
                return new TimingFeatures(raw, Array.Empty<double[]>());
            var times = raw.Select(seconds).ToArray();
            var grid = raw.Select(toGrid).ToArray();
            var hands = groups.Select(group => group.Notes.Aggregate(0, (mask, note) => mask | 1 << note.Hand)).ToArray();
            var counts = groups.Select(group => group.Notes.Count).ToArray();
            var starts = Enumerable.Range(1, raw.Length - 1).Where(i =>
                raw[i] - raw[i - 1] >= 2.5 && times[i] - times[i - 1] >= 1.25 * pulse)
                .Prepend(0).Append(raw.Length).ToArray();
            var cues = RhythmAccents.CueWeights(raw, hands, starts);
            var accents = RhythmAccents.AccentGrid(grid, times, hands, counts, starts, pulse);
            var baselineBeats = NestedTimings.DistinctBeats(baseline).ToHashSet();
            var kept = grid.Where((_, index) => baselineBeats.Contains(raw[index])).ToArray();
            var rows = new double[raw.Length][];
            for (int phrase = 0; phrase + 1 < starts.Length; phrase++)
            {
                int start = starts[phrase], end = starts[phrase + 1];
                double duration = Math.Max(.001, times[end - 1] - times[start]);
                for (int i = start; i < end; i++)
                {
                    double beat = grid[i];
                    double before = i > start ? Math.Min(4, beat - grid[i - 1]) : 4;
                    double after = i + 1 < end ? Math.Min(4, grid[i + 1] - beat) : 4;
                    int left = Bound(grid, beat - 2, start, i);
                    int right = Bound(grid, beat + 2, i, end, inclusive: true);
                    int leftContext = Bound(grid, beat - 8, start, i);
                    int rightContext = Bound(grid, beat + 8, i, end, inclusive: true);
                    int repeats = 0;
                    foreach (int offset in Recurrences)
                    {
                        double expected = beat + offset;
                        int j = end - start > 2 ? Bound(grid, expected - .025, start + 1, end - 1) : end;
                        if (j < end - 1 && grid[j] <= expected + .025 &&
                            Math.Abs(before - Math.Min(4, grid[j] - grid[j - 1])) <= .125 &&
                            Math.Abs(after - Math.Min(4, grid[j + 1] - grid[j])) <= .125 &&
                            (hands[i] == 3) == (hands[j] == 3))
                            repeats++;
                    }
                    int previous = MeterRepeats.Bound(kept, beat);
                    int following = MeterRepeats.Bound(kept, beat, inclusive: true);
                    int rank = RhythmAccents.PulseRank(beat);
                    int phase = RhythmAccents.Mod((int)Math.Round(beat), 4);
                    bool whole = Math.Abs(beat - Math.Round(beat)) <= .04;
                    double mixScore = mix[raw[i]];
                    var localMix = raw[leftContext..rightContext].Select(b => mix[b]).OrderBy(value => value).ToArray();
                    int middle = localMix.Length / 2;
                    double median = localMix.Length % 2 == 0 ? (localMix[middle - 1] + localMix[middle]) / 2 : localMix[middle];
                    rows[i] = new double[]
                    {
                        Bit(rank == 0), Bit(rank == 1), Bit(rank == 2), Bit(rank == 3),
                        Bit(whole && phase == 1), Bit(whole && phase == 2), Bit(whole && phase == 3),
                        gridSeconds(beat + 1) - gridSeconds(beat), before, after,
                        i > start ? Math.Min(3, times[i] - times[i - 1]) : 3,
                        i + 1 < end ? Math.Min(3, times[i + 1] - times[i]) : 3,
                        i - left, right - i - 1, rightContext - leftContext - 1,
                        (times[i] - times[start]) / duration,
                        (times[i] - times[0]) / Math.Max(.001, times[^1] - times[0]),
                        counts[i], Bit(hands[i] == 3),
                        Bit(i > start && hands[i] != 3 && hands[i - 1] != 3 && hands[i] != hands[i - 1]),
                        Bit(i + 1 < end && hands[i] == 3 && hands[i + 1] == 3 && before >= 1.2 && after <= 1.0001),
                        Bit(groups[i].Notes.Any(note => protectedIds.Contains(note.Id))), cues[i], accents[i], repeats,
                        mixScore, mixScore - median, Bit(baselineBeats.Contains(raw[i])),
                        previous > 0 ? Math.Min(4, beat - kept[previous - 1]) : 4,
                        following < kept.Length ? Math.Min(4, kept[following] - beat) : 4,
                        MeterRepeats.Bound(kept, beat + 4, inclusive: true) - MeterRepeats.Bound(kept, beat - 4),
                    };
                }
            }
            return new TimingFeatures(raw, AddContext(grid, rows, cancellation));

            static int Bit(bool value) => value ? 1 : 0;
        }

        public static IReadOnlyDictionary<double, double> Score(TreeEnsemble ranker, TimingFeatures features, double fraction)
        {
            if (!(fraction > 0 && fraction <= 1))
                throw new ArgumentOutOfRangeException(nameof(fraction));
            var scores = new Dictionary<double, double>(features.Beats.Length);
            var row = new double[BaseColumns + MotifColumns + PhaseColumns + 1];
            row[^1] = fraction;
            for (int i = 0; i < features.Beats.Length; i++)
            {
                features.Rows[i].CopyTo(row, 0);
                scores.Add(features.Beats[i], ranker.Probability(row));
            }
            return scores;
        }

        private static double[][] AddContext(double[] grid, double[][] rows, CancellationToken cancellation)
        {
            const int width = 25, peers = 8;
            var slots = new uint[grid.Length];
            var chords = new uint[grid.Length];
            var energy = new double[grid.Length, width];
            var sizes = new int[grid.Length];
            var phases = grid.Select(beat => beat - Math.Floor(beat + .0001)).ToArray();
            for (int i = 0; i < grid.Length; i++)
            {
                int start = MeterRepeats.Bound(grid, grid[i] - 1.01);
                int end = MeterRepeats.Bound(grid, grid[i] + 1.01);
                for (int j = start; j < end; j++)
                {
                    int slot = Math.Clamp((int)Math.Round((grid[j] - grid[i]) * 12) + width / 2, 0, width - 1);
                    uint mask = 1u << slot;
                    slots[i] |= mask;
                    chords[i] = rows[j][TwoHands] != 0 ? chords[i] | mask : chords[i] & ~mask;
                    energy[i, slot] = rows[j][Mix];
                }
                sizes[i] = EngineMath.PopCount(slots[i]);
            }
            var output = new double[grid.Length][];
            for (int i = 0; i < grid.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var best = new List<(double Distance, int Index)>(peers);
                for (int j = 0; j < grid.Length; j++)
                {
                    double gap = grid[i] - grid[j];
                    double phase = Math.Abs(gap / 2 - Math.Round(gap / 2));
                    if (Math.Abs(gap) < 2 || phase > .045 || Math.Abs(rows[i][SecondsPerBeat] - rows[j][SecondsPerBeat]) > .10)
                        continue;
                    int shared = EngineMath.PopCount(slots[i] & slots[j]);
                    int size = sizes[i] + sizes[j];
                    double distance = 1 - (double)shared / Math.Max(1, size - shared);
                    distance += .16 * EngineMath.PopCount(chords[i] ^ chords[j]) / Math.Max(1, size);
                    double flux = 0;
                    for (int slot = 0; slot < width; slot++)
                        flux += Math.Abs(energy[i, slot] - energy[j, slot]);
                    distance += .08 * flux / Math.Max(1, size) + .2 * Math.Abs(rows[i][Mix] - rows[j][Mix]);
                    if (best.Count == peers && best[^1].Distance <= distance)
                        continue;
                    int index = best.FindIndex(peer => peer.Distance > distance);
                    best.Insert(index < 0 ? best.Count : index, (distance, j));
                    if (best.Count > peers)
                        best.RemoveAt(peers);
                }

                var row = new double[BaseColumns + MotifColumns + PhaseColumns];
                rows[i].CopyTo(row, 0);
                double support = 0;
                for (int j = 0; j < 3; j++)
                    row[BaseColumns + 1 + j] = j < best.Count ? Math.Min(1, best[j].Distance) : 1;
                foreach (var (distance, index) in best)
                {
                    double weight = Math.Max(0, 1 - Math.Min(1, distance) / .35);
                    support += weight;
                    for (int j = 0; j < PeerColumns.Length; j++)
                        row[BaseColumns + 4 + j] += weight * rows[index][PeerColumns[j]];
                }
                row[BaseColumns] = support;
                for (int j = 0; j < PeerColumns.Length; j++)
                    row[BaseColumns + 4 + j] /= Math.Max(support, 1e-8);
                int first = MeterRepeats.Bound(grid, grid[i] - 8);
                int last = MeterRepeats.Bound(grid, grid[i] + 8);
                for (int j = first; j < last; j++)
                {
                    double delta = Math.Abs(phases[j] - phases[i]);
                    if (Math.Min(delta, 1 - delta) < .055)
                        row[BaseColumns + MotifColumns]++;
                    for (int p = 0; p < Phases.Length; p++)
                        if (Math.Abs(phases[j] - Phases[p]) < .055)
                            row[BaseColumns + MotifColumns + 1 + p]++;
                }
                for (int p = 0; p < PhaseColumns; p++)
                    row[BaseColumns + MotifColumns + p] /= last - first;
                output[i] = row;
            }
            return output;
        }

        private static int Bound(double[] sorted, double value, int start, int end, bool inclusive = false) =>
            Math.Min(end, MeterRepeats.Bound(sorted, value, inclusive, start));
    }
}
