using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal static class RankedEasy
    {
        internal static void Refine(
            NestedTimings.BeatGroup[] groups, double[] beats, double[] times, int[] starts,
            Dictionary<int, List<Note>> kept, ISet<int> anchors, ISet<int> fixedBeats,
            double[] accents, double[] rank, double[] mix, double minimumGap, double pulse)
        {
            var accentBeats = beats.Where((_, i) => accents[i] > 0).ToArray();
            var near = Enumerable.Range(0, beats.Length).Where(i =>
            {
                int first = MeterRepeats.Bound(accentBeats, beats[i] - 2.0001);
                return first < accentBeats.Length && accentBeats[first] <= beats[i] + 2.0001;
            }).ToHashSet();
            var locked = anchors.Concat(near).ToHashSet();
            var phrases = Phrases(beats, starts).ToArray();
            Allocate(groups, times, kept, locked, rank, minimumGap, pulse, phrases);
            Sequences(groups, beats, times, kept, anchors, fixedBeats, near, rank, mix, minimumGap, pulse, phrases);
            Doubles(groups, beats, times, kept, locked, rank, mix, minimumGap, pulse, phrases);
            Figures(groups, beats, times, starts, kept, locked, rank, minimumGap);
        }

        private static IEnumerable<(int First, int End)> Phrases(double[] beats, int[] starts)
        {
            const int width = 32;
            for (int phrase = 0; phrase + 1 < starts.Length; phrase++)
            {
                int first = starts[phrase], end = starts[phrase + 1];
                if (end - first < 3)
                    continue;
                int edge = first;
                for (double bar = (Math.Floor((beats[first] + .002) / width) + 1) * width;
                    bar <= beats[end - 1] - .30 * width; bar += width)
                {
                    int split = MeterRepeats.Bound(beats, bar - .002, first: edge + 1);
                    if (split < end && beats[split] - beats[edge] >= .45 * width)
                    {
                        yield return (edge, split);
                        edge = split;
                    }
                }
                yield return (edge, end);
            }
        }

        private static void Allocate(NestedTimings.BeatGroup[] groups, double[] times, Dictionary<int, List<Note>> kept,
            HashSet<int> locked, double[] rank, double minimumGap, double pulse, (int First, int End)[] phrases)
        {
            var budgets = phrases.Select(phrase => Enumerable.Range(phrase.First, phrase.End - phrase.First)
                .Sum(i => kept.TryGetValue(i, out var group) ? group.Count : 0)).ToArray();
            var demand = phrases.Select(phrase => Enumerable.Range(phrase.First, phrase.End - phrase.First)
                .GroupBy(i => Math.Round((times[i] - times[phrase.First]) / (.7 * pulse)))
                .Sum(slot => slot.Max(i => rank[i]))).ToArray();
            double totalDemand = demand.Sum();
            if (totalDemand <= 1e-8)
                return;
            int totalBudget = budgets.Sum();
            var targets = demand.Select(value => value * totalBudget / totalDemand).ToArray();
            var initial = (int[])budgets.Clone();
            var caps = budgets.Select(count => Math.Max(1, (int)Math.Ceiling(.2 * count))).ToArray();

            for (int iteration = 0; iteration < Math.Ceiling(.04 * totalBudget); iteration++)
            {
                var donors = new List<(double Cost, int Phrase, int Note)>();
                var receivers = new List<(double Value, int Phrase, int Note, int Left)>();
                for (int phrase = 0; phrase < phrases.Length; phrase++)
                {
                    var (first, end) = phrases[phrase];
                    var local = Enumerable.Range(first, end - first).Where(kept.ContainsKey).ToArray();
                    double excess = budgets[phrase] - targets[phrase];
                    if (excess >= .5 && initial[phrase] - budgets[phrase] < caps[phrase])
                        foreach (int i in local.Skip(1).SkipLast(1))
                            if (!locked.Contains(i) && kept[i].Count == 2)
                                donors.Add((.35 * rank[i] - .04 * Math.Min(3, excess), phrase, i));
                    if (excess > -.5 || budgets[phrase] - initial[phrase] >= caps[phrase])
                        continue;
                    for (int j = 1; j < local.Length; j++)
                    {
                        int left = local[j - 1], right = local[j];
                        for (int i = left + 1; i < right; i++)
                        {
                            double before = times[i] - times[left], after = times[right] - times[i];
                            double gap = Math.Min(before, after);
                            if (locked.Contains(i) || gap < minimumGap || rank[i] < .32 ||
                                gap < .5 * pulse && rank[i] < .75 * (rank[left] + rank[right]) / 2)
                                continue;
                            double spacing = Square(before / pulse - 1) + Square(after / pulse - 1) -
                                Square((before + after) / pulse - 1);
                            receivers.Add((rank[i] - .10 * spacing - .04 * Math.Max(-3, excess), phrase, i, left));
                        }
                    }
                }
                if (donors.Count == 0 || receivers.Count == 0)
                    break;
                var drop = donors.Min();
                var add = receivers.Max();
                if (add.Value - drop.Cost < .10)
                    break;
                kept.Add(add.Note, new List<Note> { Preferred(groups[add.Note].Notes, 1 - kept[add.Left][^1].Hand) });
                kept[drop.Note] = new List<Note> { Preferred(kept[drop.Note]) };
                budgets[drop.Phrase]--;
                budgets[add.Phrase]++;
            }
        }

        private static void Sequences(NestedTimings.BeatGroup[] groups, double[] beats, double[] times,
            Dictionary<int, List<Note>> kept, ISet<int> anchors, ISet<int> fixedBeats, HashSet<int> near,
            double[] rank, double[] mix, double minimumGap, double pulse, (int First, int End)[] phrases)
        {
            foreach (var (first, end) in phrases)
            {
                var selected = Enumerable.Range(first, end - first).Where(kept.ContainsKey).ToArray();
                if (selected.Length < 3)
                    continue;
                var locked = selected.Where(i => i == selected[0] || i == selected[^1] || anchors.Contains(i) ||
                    near.Contains(i) || kept[i].Count != 1 || rank[i] >= .8 ||
                    fixedBeats.Contains(i) && rank[i] >= .45).ToArray();
                for (int span = 1; span < locked.Length; span++)
                {
                    int left = locked[span - 1], right = locked[span];
                    var interior = selected.Where(i => left < i && i < right).ToArray();
                    if (interior.Length == 0)
                        continue;
                    var original = interior.ToHashSet();
                    var paths = new Dictionary<int, (double Score, int[] Hits)> { [left] = (0, Array.Empty<int>()) };
                    int previous = left;
                    foreach (int current in interior.Append(right))
                    {
                        double oldGap = times[current] - times[previous];
                        int lo = Math.Min(right, MeterRepeats.Bound(beats, beats[current] - 3, first: left + 1));
                        int hi = Math.Min(right, MeterRepeats.Bound(beats, beats[current] + 3.0001, first: lo));
                        var choices = current == right ? new[] { right } :
                            Enumerable.Range(lo, hi - lo).Where(i => !near.Contains(i) || i == current);
                        var next = new Dictionary<int, (double Score, int[] Hits)>();
                        foreach (int i in choices)
                        {
                            double value = i == right ? 0 : 2 * rank[i] - .08 * RhythmAccents.PulseRank(beats[i]) +
                                .20 * mix[i] + (groups[i].Notes.Count > 1 ? .25 : 0) + (original.Contains(i) ? .10 : 0);
                            foreach (var (j, path) in paths)
                            {
                                double gap = times[i] - times[j];
                                if (gap < Math.Min(minimumGap, oldGap) - 1e-8 || gap > Math.Max(oldGap, 2 * pulse) + 1e-8)
                                    continue;
                                double score = path.Score + value - Square(gap / pulse - 1);
                                if (!next.TryGetValue(i, out var old) || score > old.Score ||
                                    score == old.Score && Later(path.Hits, old.Hits))
                                    next[i] = (score, path.Hits.Append(i).ToArray());
                            }
                        }
                        paths = next;
                        previous = current;
                    }
                    if (!paths.TryGetValue(right, out var result) ||
                        result.Hits.SkipLast(1).Sum(i => rank[i]) - interior.Sum(i => rank[i]) < .12)
                        continue;
                    var removed = interior.Select(i => kept[i][0]).ToArray();
                    foreach (int i in interior)
                        kept.Remove(i);
                    for (int i = 0; i < removed.Length; i++)
                        kept.Add(result.Hits[i], new List<Note> { Preferred(groups[result.Hits[i]].Notes, removed[i].Hand) });
                }
            }
        }

        private static void Doubles(NestedTimings.BeatGroup[] groups, double[] beats, double[] times,
            Dictionary<int, List<Note>> kept, HashSet<int> locked, double[] rank, double[] mix,
            double minimumGap, double pulse, (int First, int End)[] phrases)
        {
            foreach (var (first, end) in phrases)
            {
                var hits = Enumerable.Range(first, end - first).Where(kept.ContainsKey).ToArray();
                for (int h = 1; h + 1 < hits.Length; h++)
                {
                    int left = hits[h - 1], current = hits[h], right = hits[h + 1];
                    if (locked.Contains(current) || kept[current].Count != 2 || rank[current] > .60)
                        continue;
                    double spacing = Math.Max(minimumGap, Math.Min(.70 * pulse,
                        Math.Min(times[current] - times[left], times[right] - times[current])));
                    var choices = new List<(double Gain, int Index)>();
                    for (int i = left + 1; i < right; i++)
                    {
                        double distance = Math.Abs(beats[i] - beats[current]);
                        var group = groups[i].Notes;
                        if (i == current || locked.Contains(i) || distance > .75 || group.Count != 2 ||
                            group[0].Hand == group[1].Hand || !EasyPulse.IsEasyDouble(group) ||
                            Math.Min(times[i] - times[left], times[right] - times[i]) < spacing)
                            continue;
                        double gain = rank[i] - rank[current] + .12 * (mix[i] - mix[current]) - .035 * distance;
                        if (gain >= .20)
                            choices.Add((gain, i));
                    }
                    if (choices.Count == 0)
                        continue;
                    int next = choices.Max().Index;
                    kept.Remove(current);
                    kept.Add(next, groups[next].Notes.ToList());
                    hits[h] = next;
                }
            }
        }

        private static void Figures(NestedTimings.BeatGroup[] groups, double[] beats, double[] times, int[] starts,
            Dictionary<int, List<Note>> kept, HashSet<int> locked, double[] rank, double minimumGap)
        {
            int moved = 0;
            for (int phrase = 0; phrase + 1 < starts.Length; phrase++)
            {
                var blocks = Enumerable.Range(starts[phrase], starts[phrase + 1] - starts[phrase])
                    .ToLookup(i => 4 * Math.Floor((beats[i] + .001) / 4));
                var figures = new Dictionary<double, (int[] Shape, int[] Notes)>();
                foreach (var block in blocks)
                {
                    var hits = block.ToArray();
                    if (hits.Length < 5 || Math.Abs(beats[hits[0]] - block.Key) > .02)
                        continue;
                    var figure = new List<int> { hits[0] };
                    foreach (int i in hits.Skip(1))
                    {
                        if (times[i] - times[figure[^1]] < .25)
                            break;
                        figure.Add(i);
                    }
                    var shape = figure.Select(i => (int)Math.Round((beats[i] - block.Key) * 48)).ToArray();
                    if (figure.Count == 4 && shape.Any(slot => slot % 24 != 0))
                        figures.Add(block.Key, (shape, figure.ToArray()));
                }
                foreach (var (bar, figure) in figures)
                {
                    int repeats = new[] { -12, -8, -4, 4, 8, 12 }.Count(shift =>
                        figures.TryGetValue(bar + shift, out var other) && figure.Shape.SequenceEqual(other.Shape));
                    if (repeats < 2)
                        continue;
                    var block = blocks[bar].ToArray();
                    var old = block.Where(kept.ContainsKey).ToArray();
                    var shared = old.Intersect(figure.Notes).ToHashSet();
                    if (shared.Count < 2 || old.Length > figure.Notes.Length)
                        continue;
                    shared.UnionWith(old.Where(i => locked.Contains(i) || kept[i].Count != 1));
                    int previous = kept.Keys.Where(i => i < block[0]).DefaultIfEmpty(-1).Max();
                    int following = kept.Keys.Where(i => i > block[^1]).DefaultIfEmpty(-1).Min();
                    int donor = -1;
                    if (old.Length == 3 && moved < Math.Ceiling(.03 * kept.Count))
                        donor = kept.Keys.Where(i => kept[i].Count == 2 && !locked.Contains(i) && !block.Contains(i) &&
                            Math.Abs(beats[i] - bar) <= 128).OrderBy(i => rank[i]).ThenBy(i => Math.Abs(beats[i] - bar))
                            .ThenBy(i => i).DefaultIfEmpty(-1).First();
                    int[] chosen = null;
                    double best = double.NegativeInfinity;
                    for (int mask = 1; mask < 16; mask++)
                    {
                        var path = figure.Notes.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
                        if (path.Length != old.Length + (donor >= 0 ? 1 : 0) || !shared.IsSubsetOf(path) ||
                            previous >= 0 && times[path[0]] - times[previous] < Math.Min(minimumGap, times[old[0]] - times[previous]) ||
                            following >= 0 && times[following] - times[path[^1]] < Math.Min(minimumGap, times[following] - times[old[^1]]))
                            continue;
                        double value = path.Sum(i => rank[i]);
                        if (value > best || value == best && Later(path, chosen))
                        {
                            best = value;
                            chosen = path;
                        }
                    }
                    if (chosen == null)
                        continue;
                    var displaced = new List<Note>();
                    foreach (int i in old.Except(chosen))
                    {
                        displaced.Add(kept[i][0]);
                        kept.Remove(i);
                    }
                    if (donor >= 0)
                    {
                        var ordered = kept[donor].OrderBy(note => note.Y).ThenBy(note => Math.Abs(note.X - note.Hand - 1)).ToArray();
                        kept[donor] = new List<Note> { ordered[0] };
                        displaced.Add(ordered[1]);
                        moved++;
                    }
                    foreach (var (index, note) in chosen.Except(old).Zip(displaced, (index, note) => (index, note)))
                        kept.Add(index, new List<Note> { Preferred(groups[index].Notes, note.Hand) });
                }
            }
        }

        private static bool Later(int[] candidate, int[] incumbent)
        {
            for (int i = 0; i < candidate.Length; i++)
                if (candidate[i] != incumbent[i])
                    return candidate[i] > incumbent[i];
            return false;
        }

        private static double Square(double x) => x * x;

        private static Note Preferred(IEnumerable<Note> notes, int hand = -1) => notes
            .OrderBy(note => note.Hand != hand).ThenBy(note => note.Y).ThenBy(note => Math.Abs(note.X - note.Hand - 1)).First();
    }
}
