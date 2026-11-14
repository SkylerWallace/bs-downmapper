using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record RepeatTimingContext(
        IReadOnlyList<Note> Notes, ISet<int> ProtectedIds,
        ISet<double> RequiredBeats, IReadOnlyList<RepeatWindow> SparseWindows);

    public static class RepeatPriorities
    {
        private static readonly (double Gap, double Pulse)[] Thresholds =
        {
            (.33, .74), (.17, .44), (.105, .30), (.07, .24)
        };

        public static int[] Match(
            IReadOnlyList<Note> reference, IReadOnlyList<int> initialOrder,
            IReadOnlyList<TimingPriority> tiers, IReadOnlyDictionary<string, int> budgets,
            IReadOnlyDictionary<string, RepeatTimingContext> contexts, ISet<int> mandatory,
            IEnumerable<VerifiedRepeat> repeats, Func<double, int> phraseAt, Func<double, double> seconds)
        {
            var order = initialOrder.ToArray();
            var groups = NestedTimings.Group(reference);
            var referenceBeats = groups.Select(group => group.Beat).ToArray();
            var noteBeats = groups.SelectMany(group => group.Notes.Select(note => (note.Id, group.Beat)))
                .ToDictionary(item => item.Id, item => item.Beat);
            var positions = order.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
            var links = repeats.ToArray();
            int lower = 0;

            for (int tier = 0; tier < tiers.Count; tier++)
            {
                var priority = tiers[tier];
                int cutoff = budgets[priority.Difficulty];
                int upper = tier + 1 < tiers.Count ? budgets[tiers[tier + 1].Difficulty] : order.Length;
                if (lower < cutoff && cutoff < upper)
                {
                    var context = contexts[priority.Difficulty];
                    foreach (var move in TradeCandidates(context, order.Take(cutoff), referenceBeats,
                        priority.Scores, mandatory, links, phraseAt, seconds, Thresholds[tier], CanSwap))
                    {
                        int old = positions[move.Donor.Id], next = positions[move.Incoming.Id];
                        (order[old], order[next]) = (order[next], order[old]);
                        positions[move.Donor.Id] = next;
                        positions[move.Incoming.Id] = old;
                    }
                }
                lower = cutoff;

                bool CanSwap(Note donor, Note incoming)
                {
                    int old = positions[donor.Id], next = positions[incoming.Id];
                    return lower <= old && old < cutoff && cutoff <= next && next < upper &&
                        priority.Scores[noteBeats[donor.Id]] - priority.Scores[noteBeats[incoming.Id]] <= .05;
                }
            }
            return order;
        }

        private sealed record Evidence(int Occurrence, double Support, double Score, double[] Peers);

        private static IEnumerable<(Note Donor, Note Incoming)> TradeCandidates(
            RepeatTimingContext context, IEnumerable<int> selection, double[] reference,
            IReadOnlyDictionary<double, double> scores, ISet<int> mandatory,
            IReadOnlyList<VerifiedRepeat> repeats, Func<double, int> phraseAt, Func<double, double> seconds,
            (double Gap, double Pulse) thresholds, Func<Note, Note, bool> canSwap)
        {
            var source = NestedTimings.Group(context.Notes).ToDictionary(group => group.Beat, group => group.Notes);
            var available = source.Keys.OrderBy(beat => beat).ToArray();
            var selected = selection.ToHashSet();
            var kept = source.Select(pair => (pair.Key, Notes: pair.Value.Where(note => selected.Contains(note.Id)).ToList()))
                .Where(pair => pair.Notes.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Notes);
            var original = kept.Keys.ToHashSet();
            var times = available.ToDictionary(beat => beat, seconds);
            var phrases = available.ToDictionary(beat => beat, phraseAt);
            var fixedBeats = original.GroupBy(beat => phrases[beat])
                .SelectMany(group => new[] { group.Min(), group.Max() }).ToHashSet();
            foreach (double required in context.RequiredBeats)
            {
                int match = NestedTimings.Nearest(required, available);
                if (match >= 0)
                    fixedBeats.Add(available[match]);
            }

            var evidence = new Dictionary<double, Evidence>();
            int occurrence = 0;
            foreach (var repeat in repeats)
            {
                var areas = new Dictionary<int, int>();
                foreach (var window in repeat.Occurrences)
                {
                    int first = MeterRepeats.Bound(reference, window.Start - 1e-5);
                    int end = MeterRepeats.Bound(reference, window.End);
                    for (int index = first; index < end; index++)
                        areas[index] = occurrence;
                    occurrence++;
                }
                foreach (var link in repeat.Links)
                {
                    var peers = new Dictionary<double, int>();
                    foreach (int index in link)
                    {
                        int match = NestedTimings.Nearest(reference[index], available);
                        if (match >= 0)
                            peers[available[match]] = index;
                    }
                    if (peers.Count < 2)
                        continue;
                    int votes = peers.Keys.Count(original.Contains);
                    foreach (var (beat, index) in peers)
                        if (areas.TryGetValue(index, out int area))
                            evidence[beat] = new Evidence(area, (double)(votes - (original.Contains(beat) ? 1 : 0)) /
                                (peers.Count - 1), scores[reference[index]], peers.Keys.Where(peer => peer != beat).ToArray());
                }
            }

            var donors = original.Where(beat => evidence.ContainsKey(beat) && !fixedBeats.Contains(beat) &&
                kept[beat].Count == 1 && !mandatory.Contains(kept[beat][0].Id) &&
                !context.ProtectedIds.Contains(kept[beat][0].Id))
                .ToLookup(beat => (evidence[beat].Occurrence, phrases[beat]));
            var moves = new List<(double Gain, double Beat, double Old)>();
            foreach (var (beat, next) in evidence)
            {
                if (original.Contains(beat) || next.Support < .6 ||
                    context.SparseWindows.Any(window => window.Start <= beat && beat <= window.End))
                    continue;
                foreach (double old in donors[(next.Occurrence, phrases[beat])])
                {
                    var previous = evidence[old];
                    double agreement = next.Support - previous.Support;
                    double preference = next.Score - previous.Score;
                    double gain = preference + .18 * agreement;
                    if (agreement >= .5 && preference >= -.12 && gain >= .06 &&
                        Math.Abs(times[beat] - times[old]) <= thresholds.Pulse)
                        moves.Add((gain, beat, old));
                }
            }

            var ordered = original.OrderBy(beat => beat).ToList();
            var edited = new HashSet<double>();
            var perOccurrence = new Dictionary<int, int>();
            foreach (var (_, beat, old) in moves.OrderByDescending(move => move.Gain)
                .ThenBy(move => move.Beat).ThenBy(move => move.Old))
            {
                var next = evidence[beat];
                var previous = evidence[old];
                if (edited.Contains(beat) || edited.Contains(old) || perOccurrence.GetValueOrDefault(next.Occurrence) >= 2)
                    continue;
                double liveSupport = (double)next.Peers.Count(kept.ContainsKey) / next.Peers.Length;
                double liveCost = (double)previous.Peers.Count(kept.ContainsKey) / previous.Peers.Length;
                double liveGain = liveSupport - liveCost;
                if (liveSupport < .6 || liveGain < .5 || next.Score - previous.Score + .18 * liveGain < .06)
                    continue;

                var donor = kept[old][0];
                var incoming = source[beat].FindIndex(note => note.Hand == donor.Hand);
                if (incoming < 0)
                    continue;
                var note = source[beat][incoming];
                if (phraseAt(note.Beat) != phraseAt(donor.Beat) || !canSwap(donor, note))
                    continue;
                int index = ordered.BinarySearch(old);
                if (index <= 0 || index >= ordered.Count - 1)
                    continue;
                double oldGap = Math.Max(times[old] - times[ordered[index - 1]], times[ordered[index + 1]] - times[old]);
                double bridgedGap = times[ordered[index + 1]] - times[ordered[index - 1]];
                var neighbors = ordered.ToList();
                neighbors.RemoveAt(index);
                int insertion = ~neighbors.BinarySearch(beat);
                if (insertion <= 0 || insertion >= neighbors.Count)
                    continue;
                double before = times[beat] - times[neighbors[insertion - 1]];
                double after = times[neighbors[insertion]] - times[beat];
                if (Math.Min(before, after) < thresholds.Gap - 1e-8)
                    continue;
                double newGap = insertion == index ? Math.Max(before, after) : bridgedGap;
                if (newGap > Math.Max(2 * thresholds.Pulse, 1.15 * oldGap) + 1e-8)
                    continue;

                kept.Add(beat, new List<Note> { note });
                kept.Remove(old);
                neighbors.Insert(insertion, beat);
                ordered = neighbors;
                edited.Add(beat);
                edited.Add(old);
                perOccurrence[next.Occurrence] = perOccurrence.GetValueOrDefault(next.Occurrence) + 1;
                yield return (donor, note);
            }
        }
    }
}
