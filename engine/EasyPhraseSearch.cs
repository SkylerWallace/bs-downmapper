using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal static class EasyPhraseSearch
    {
        private const double Tolerance = .0001;
        private static readonly int[] Recurrences = { -16, -8, -4, 4, 8, 16 };
        private static readonly int[] Pickups = { -8, -4, 4, 8 };
        private sealed record Path(double Cost, int[] Attacks);
        private sealed record Phrase(double Gap, double Rate, List<int> Indices, int Count);

        internal static void Redistribute(NestedTimings.BeatGroup[] groups, double[] times, int[] starts,
            Dictionary<int, List<Note>> kept, ISet<int> anchors, double[] costs, double minimumGap, double pulse)
        {
            var phrases = new List<Phrase>();
            for (int section = 0; section + 1 < starts.Length; section++)
            {
                int first = starts[section], end = starts[section + 1];
                double duration = times[end - 1] - times[first];
                if (duration < 4 * pulse)
                    continue;
                var gaps = Enumerable.Range(first, end - first - 1).Select(i => times[i + 1] - times[i]).OrderBy(gap => gap).ToArray();
                double gap = (gaps[(gaps.Length - 1) / 2] + gaps[gaps.Length / 2]) / 2;
                var indices = Enumerable.Range(first, end - first).Where(kept.ContainsKey).ToList();
                int count = indices.Sum(i => kept[i].Count);
                phrases.Add(new Phrase(gap, count / (duration + pulse), indices, count));
            }

            var added = new int[phrases.Count];
            var removed = new int[phrases.Count];
            while (phrases.Count >= 2)
            {
                var donors = new Dictionary<int, (double Cost, int Drop)>();
                var receivers = new Dictionary<int, (double Cost, int Insert, int Left)>();
                for (int area = 0; area < phrases.Count; area++)
                {
                    var phrase = phrases[area];
                    var hits = phrase.Indices;
                    if (added[area] == 0 && removed[area] < .25 * phrase.Count)
                        for (int index = 1; index + 1 < hits.Count; index++)
                        {
                            int i = hits[index];
                            if (anchors.Contains(i))
                                continue;
                            bool isDouble = kept[i].Count == 2;
                            if (!isDouble && (costs[i] < .25 || times[hits[index + 1]] - times[hits[index - 1]] > 2.5 * pulse))
                                continue;
                            var offer = (isDouble ? .25 + Math.Max(0, -costs[i]) : 1 - costs[i], i);
                            if (!donors.TryGetValue(area, out var old) || offer.CompareTo(old) < 0)
                                donors[area] = offer;
                        }
                    if (phrase.Gap <= .25 * pulse && removed[area] == 0 && added[area] < .35 * phrase.Count)
                        for (int index = 1; index < hits.Count; index++)
                        {
                            int left = hits[index - 1], right = hits[index];
                            if (times[right] - times[left] < 2 * minimumGap)
                                continue;
                            for (int i = left + 1; i < right; i++)
                            {
                                double spacing = Math.Min(times[i] - times[left], times[right] - times[i]);
                                if (spacing < minimumGap)
                                    continue;
                                var offer = (costs[i] - 2 * spacing / pulse, i, left);
                                if (!receivers.TryGetValue(area, out var old) || offer.CompareTo(old) < 0)
                                    receivers[area] = offer;
                            }
                        }
                }
                var choices = from receiver in receivers
                              from donor in donors
                              let target = phrases[receiver.Key]
                              let source = phrases[donor.Key]
                              where source.Gap >= 1.6 * target.Gap && source.Rate >= target.Rate
                              select (target.Gap, receiver.Value.Cost + donor.Value.Cost, Receive: receiver.Key,
                                  Donate: donor.Key, receiver.Value.Insert, donor.Value.Drop, receiver.Value.Left);
                if (!choices.Any())
                    break;
                var change = choices.Min();
                int previousHand = kept[change.Left][^1].Hand;
                var note = groups[change.Insert].Notes.OrderBy(note => note.Hand == previousHand).ThenBy(note => note.Y)
                    .ThenBy(note => Math.Abs(note.X - note.Hand - 1)).First();
                kept.Add(change.Insert, new List<Note> { note });
                var targetHits = phrases[change.Receive].Indices;
                targetHits.Insert(~targetHits.BinarySearch(change.Insert), change.Insert);
                if (kept[change.Drop].Count == 2)
                    kept[change.Drop] = kept[change.Drop].OrderBy(note => note.Y)
                        .ThenBy(note => Math.Abs(note.X - note.Hand - 1)).Take(1).ToList();
                else
                {
                    kept.Remove(change.Drop);
                    phrases[change.Donate].Indices.Remove(change.Drop);
                }
                added[change.Receive]++;
                removed[change.Donate]++;
            }
        }

        internal static double[] Costs(double[] beats, int[] starts, double[] cues)
        {
            var costs = new double[beats.Length];
            for (int section = 0; section + 1 < starts.Length; section++)
            {
                int first = starts[section], end = starts[section + 1];
                for (int i = first + 1; i < end - 1; i++)
                {
                    double beat = beats[i];
                    double before = Math.Min(2, beat - beats[i - 1]), after = Math.Min(2, beats[i + 1] - beat);
                    bool whole = Math.Abs(beat - Math.Round(beat)) < Tolerance;
                    double cost = whole ? 0 : Math.Abs(beat * 2 - Math.Round(beat * 2)) < .0002 ? .55 :
                        Math.Abs(beat * 4 - Math.Round(beat * 4)) < .0004 ? .85 : 1.2;
                    if (whole && Math.Round(beat) % 2 == 0)
                        cost -= .28;
                    if (Math.Abs(before - after) >= .24)
                    {
                        int support = Recurrences.Select(offset => NestedTimings.Nearest(beat + offset, beats))
                            .Count(j => first < j && j < end - 1 &&
                                Math.Abs(before - Math.Min(2, beats[j] - beats[j - 1])) <= .125 &&
                                Math.Abs(after - Math.Min(2, beats[j + 1] - beats[j])) <= .125);
                        cost -= Math.Min(.95, .24 * support + .3 * Math.Min(1, Math.Abs(after - before)));
                    }
                    costs[i] = cost - .45 * cues[i];
                }
            }
            return costs;
        }

        private static HashSet<(int First, int Last)> PickupPairs(double[] beats, int[] starts)
        {
            var pairs = new HashSet<(int, int)>();
            for (int section = 0; section + 1 < starts.Length; section++)
            {
                int first = starts[section], end = starts[section + 1];
                for (int i = first; i < end - 2; i++)
                {
                    double start = beats[i];
                    if (Math.Abs(start / 2 - Math.Round(start / 2)) > .00005)
                        continue;
                    for (int j = i + 2; j < Math.Min(i + 4, end); j++)
                    {
                        if (Math.Abs(beats[j] - start - 1.5) > Tolerance)
                            continue;
                        var gaps = Enumerable.Range(i, j - i).Select(k => beats[k + 1] - beats[k]).ToArray();
                        if (gaps.Max() - gaps.Min() < .24)
                            continue;
                        foreach (int offset in Pickups)
                        {
                            int k = MeterRepeats.Bound(beats, start + offset - Tolerance, first: first);
                            if (k + j - i < end && Enumerable.Range(0, j - i + 1)
                                .All(d => Math.Abs(beats[k + d] - beats[i + d] - offset) <= Tolerance))
                            {
                                pairs.Add((i, j));
                                break;
                            }
                        }
                    }
                }
            }
            return pairs;
        }

        internal static ISet<int> Swap(
            NestedTimings.BeatGroup[] groups, double[] beats, double[] times, int[] starts,
            Dictionary<int, List<Note>> kept, ISet<int> anchors, double[] costs, double[] accents,
            double minimumGap, double pulse, double radius)
        {
            var ranks = beats.Select(RhythmAccents.PulseRank).ToArray();
            var pickups = PickupPairs(beats, starts);
            var fixedBeats = kept.Where(pair => anchors.Contains(pair.Key) || pair.Value.Count != 1)
                .Select(pair => pair.Key).ToHashSet();
            var run = new List<int>();
            foreach (int i in kept.Keys.OrderBy(i => i))
            {
                if (run.Count > 0 && Math.Abs(beats[i] - beats[run[^1]] - 2) > Tolerance)
                    run.Clear();
                if (Math.Abs(beats[i] / 2 - Math.Round(beats[i] / 2)) > .00005)
                    continue;
                run.Add(i);
                if (run.Count == 4)
                    fixedBeats.UnionWith(run);
                else if (run.Count > 4)
                    fixedBeats.Add(i);
            }
            var accentBeats = beats.Where((_, i) => accents[i] > 0).ToArray();
            foreach (int i in fixedBeats.Where(i => !anchors.Contains(i) && kept[i].Count == 1).ToArray())
            {
                int neighbor = MeterRepeats.Bound(accentBeats, beats[i] - 1.0001);
                if (neighbor < accentBeats.Length && accentBeats[neighbor] <= beats[i] + 1.0001)
                    fixedBeats.Remove(i);
            }
            var boundaries = fixedBeats.OrderBy(i => i).ToArray();
            var replacements = new List<(int Old, int New, Note Note)>();
            for (int section = 1; section < boundaries.Length; section++)
            {
                int left = boundaries[section - 1], right = boundaries[section];
                var original = Enumerable.Range(left, right - left + 1).Where(kept.ContainsKey).ToArray();
                if (original.Length < 3)
                    continue;
                var paths = new Dictionary<int, Path> { [left] = new(0, Array.Empty<int>()) };
                for (int position = 1; position < original.Length; position++)
                {
                    int previous = original[position - 1], current = original[position];
                    double oldGap = times[current] - times[previous];
                    int lo = Math.Min(right, MeterRepeats.Bound(beats, beats[current] - 2.0001, first: left + 1));
                    int hi = Math.Min(right, MeterRepeats.Bound(beats, beats[current] + 2.0001, first: current));
                    var candidates = current == right ? new[] { right } :
                        Enumerable.Range(lo, Math.Max(0, hi - lo)).Where(i =>
                            accents[i] > 0 || Math.Abs(beats[i] - beats[current]) <= radius + Tolerance);
                    var next = new Dictionary<int, Path>();
                    foreach (int i in candidates)
                    {
                        double move = Math.Abs(groups[i].Beat - groups[current].Beat);
                        double attack = i == right ? 0 : costs[i] - accents[i];
                        if (move > 0)
                            attack += .15 + .15 * move;
                        if (ranks[current] < 2 && ranks[i] > ranks[current] &&
                            beats[i] - beats[i - 1] < 1 - Tolerance && accents[i] == 0)
                            attack += .7 * (ranks[i] - ranks[current]);
                        foreach (var (j, path) in paths)
                        {
                            double gap = times[i] - times[j];
                            if (gap < Math.Min(oldGap, minimumGap) - 1e-8 || gap > Math.Max(oldGap, 2 * pulse) + 1e-8)
                                continue;
                            double error = gap / pulse - 1;
                            double bonus = pickups.Contains((j, i)) && gap >= .7 * pulse && gap <= .9 * pulse ? 1 : 0;
                            double cost = path.Cost + attack + 1.5 * (error * error) - bonus;
                            if (!next.TryGetValue(i, out var incumbent) || cost < incumbent.Cost ||
                                cost == incumbent.Cost && Earlier(path.Attacks, incumbent.Attacks))
                                next[i] = new Path(cost, path.Attacks.Append(i).ToArray());
                        }
                    }
                    paths = next;
                }
                if (!paths.TryGetValue(right, out var selected))
                    continue;
                for (int i = 1; i + 1 < original.Length; i++)
                {
                    int old = original[i], next = selected.Attacks[i - 1];
                    if (old == next)
                        continue;
                    int hand = kept[old][0].Hand;
                    var note = groups[next].Notes.OrderBy(note => note.Hand != hand).ThenBy(note => note.Y)
                        .ThenBy(note => Math.Abs(note.X - note.Hand - 1)).First();
                    replacements.Add((old, next, note));
                }
            }
            foreach (var change in replacements)
                kept.Remove(change.Old);
            foreach (var change in replacements)
                kept.Add(change.New, new List<Note> { change.Note });
            return fixedBeats;
        }

        private static bool Earlier(int[] candidate, int[] current)
        {
            for (int i = 0; i < candidate.Length; i++)
                if (candidate[i] != current[i])
                    return candidate[i] < current[i];
            return false;
        }
    }
}
