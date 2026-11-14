using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record EasyPulseSeed(IReadOnlyList<Note> Notes, ISet<double> RequiredBeats);

    public static class EasyPulse
    {
        private const double Tolerance = .0001;

        public static EasyPulseSeed Select(
            IReadOnlyList<Note> notes, IReadOnlyList<Bomb> bombs, IReadOnlyList<Wall> walls,
            ISet<int> protectedIds, ISet<double> linkedAccents,
            Func<double, double> seconds, double minimumGap, double pulse, bool phraseAccents,
            Func<double, double> toGrid = null, bool phraseSwaps = false, bool phraseBudget = false,
            IReadOnlyDictionary<double, double> rankScores = null, IReadOnlyDictionary<double, double> mixScores = null)
        {
            if (!double.IsFinite(pulse) || pulse <= 0 || !double.IsFinite(minimumGap) || minimumGap <= 0)
                throw new ArgumentException("Easy pulse and spacing must be positive and finite.");
            var (windows, accents) = SectionTiming.EasySparseCues(notes, walls, bombs, seconds);
            var groups = NestedTimings.Group(notes).Where(group => accents.Contains(group.Beat) ||
                group.Notes.Any(note => protectedIds.Contains(note.Id)) ||
                !windows.Any(window => window.Start - Tolerance <= group.Beat && group.Beat <= window.End + Tolerance)).ToArray();
            if (groups.Length == 0)
                return new EasyPulseSeed(notes.Take(1).ToArray(), new HashSet<double>());

            var beats = groups.Select(group => group.Beat).ToArray();
            var times = beats.Select(seconds).ToArray();
            var hands = groups.Select(group => group.Notes.Aggregate(0, (mask, note) => mask | 1 << note.Hand)).ToArray();
            var starts = Enumerable.Range(1, groups.Length - 1).Where(i =>
                beats[i] - beats[i - 1] >= 2.5 && times[i] - times[i - 1] >= 1.25 * pulse)
                .Prepend(0).Append(groups.Length).ToArray();
            var required = Enumerable.Range(0, groups.Length).Where(i => accents.Contains(beats[i]) ||
                groups[i].Notes.Any(note => protectedIds.Contains(note.Id))).ToHashSet();
            foreach (int i in Motifs(groups, hands).Distinct().OrderBy(i => i))
                if (required.All(j => Math.Abs(times[i] - times[j]) >= minimumGap))
                    required.Add(i);

            var cues = phraseAccents ? RhythmAccents.CueWeights(beats, hands, starts) : new double[groups.Length];
            for (int i = 0; i < beats.Length; i++)
                if (linkedAccents.Contains(beats[i]))
                    cues[i] += .25;

            var selected = new HashSet<int>(required);
            for (int section = 0; section + 1 < starts.Length; section++)
            {
                int first = starts[section], end = starts[section + 1];
                var anchors = required.Where(i => first <= i && i < end).OrderBy(i => i).ToList();
                if (anchors.Count == 0 || times[anchors[0]] - times[first] >= minimumGap)
                    anchors.Insert(0, first);
                if (times[end - 1] - times[anchors[^1]] >= minimumGap)
                    anchors.Add(end - 1);
                required.UnionWith(anchors);
                selected.UnionWith(anchors);
                for (int index = 0; index + 1 < anchors.Count; index++)
                    SelectBetween(anchors[index], anchors[index + 1]);
            }

            var result = new List<Note>();
            var indices = selected.OrderBy(i => i).ToArray();
            int lastHand = -1;
            for (int index = 0; index < indices.Length; index++)
            {
                int i = indices[index];
                var group = groups[i].Notes;
                var locked = group.Where(note => protectedIds.Contains(note.Id)).ToArray();
                double left = index > 0 ? times[i] - times[indices[index - 1]] : double.PositiveInfinity;
                double right = index + 1 < indices.Length ? times[indices[index + 1]] - times[i] : double.PositiveInfinity;
                if (locked.Length > 0)
                    result.AddRange(locked);
                else if (group.Count == 2 && hands[i] == 3 && Math.Min(left, right) >= .8 * pulse && IsEasyDouble(group))
                    result.AddRange(group);
                else
                    result.Add(group.OrderBy(note => note.Hand == lastHand).ThenBy(note => note.Y)
                        .ThenBy(note => Math.Abs(note.X - note.Hand - 1)).First());
                lastHand = result[^1].Hand;
            }
            if (phraseSwaps)
            {
                var grid = toGrid == null ? beats : beats.Select(toGrid).ToArray();
                var kept = result.ToLookup(note => note.Id);
                var selection = new Dictionary<int, List<Note>>();
                foreach (int i in indices)
                    selection.Add(i, groups[i].Notes.Where(note => kept.Contains(note.Id)).ToList());
                var costs = EasyPhraseSearch.Costs(grid, starts, cues);
                var accentGrid = RhythmAccents.AccentGrid(grid, times, hands,
                    groups.Select(group => group.Notes.Count).ToArray(), starts, pulse);
                bool ranked = rankScores != null && mixScores != null;
                double[] rank = ranked ? beats.Select(beat => rankScores[beat]).ToArray() : null;
                double[] mix = ranked ? beats.Select(beat => mixScores[beat]).ToArray() : null;
                if (ranked)
                    WeightRankedCosts();
                var fixedBeats = EasyPhraseSearch.Swap(groups, grid, times, starts, selection, required, costs,
                    accentGrid, minimumGap, pulse, ranked ? 1.5 : 1);
                if (phraseBudget)
                    EasyPhraseSearch.Redistribute(groups, times, starts, selection, required,
                        costs.Select((cost, i) => cost - accentGrid[i]).ToArray(), minimumGap, pulse);
                if (ranked)
                    RankedEasy.Refine(groups, grid, times, starts, selection, required, fixedBeats,
                        accentGrid, rank, mix, minimumGap, pulse);
                result = selection.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.OrderBy(note => note.Hand)).ToList();

                void WeightRankedCosts()
                {
                    var accentedBeats = grid.Where((_, index) => accentGrid[index] > 0).ToArray();
                    for (int section = 0; section + 1 < starts.Length; section++)
                    for (int i = starts[section] + 1; i + 1 < starts[section + 1]; i++)
                    {
                        int firstAccent = MeterRepeats.Bound(accentedBeats, grid[i] - 2.0001);
                        bool near = firstAccent < accentedBeats.Length && accentedBeats[firstAccent] <= grid[i] + 2.0001;
                        costs[i] -= (near ? .25 : 1) * mix[i];
                        costs[i] -= (near ? .5 : 1) * 4.5 * (rank[i] - .5);
                        if (near)
                            continue;
                        double before = grid[i] - grid[i - 1], after = grid[i + 1] - grid[i];
                        int count = groups[i].Notes.Count;
                        costs[i] -= 1.2 * Math.Min(1, Math.Max(0, after - before));
                        costs[i] -= count >= 3 ? .9 : count == 2 ? .6 : 0;
                        if (before >= 1.2 && after <= 1.0001 && hands[i] == 3 && hands[i + 1] == 3)
                            costs[i] -= 1.8 * Math.Min(1, before - after);
                    }
                }
            }
            return new EasyPulseSeed(result, required.Select(i => beats[i]).ToHashSet());

            void SelectBetween(int a, int b)
            {
                var costs = Enumerable.Repeat(double.PositiveInfinity, b - a + 1).ToArray();
                var previous = new int[costs.Length];
                costs[0] = 0;
                for (int i = a + 1; i <= b; i++)
                {
                    double beat = beats[i];
                    double grid = Math.Abs(beat / 2 - Math.Round(beat / 2)) < Tolerance ? 0 :
                        Math.Abs(beat - Math.Round(beat)) < Tolerance ? .20 :
                        Math.Abs(beat * 2 - Math.Round(beat * 2)) < .0002 ? .65 : 1.5;
                    grid -= Math.Min(.35, .65 * grid) * cues[i];
                    for (int j = i - 1; j >= a; j--)
                    {
                        double gap = times[i] - times[j];
                        if (gap > 3 * pulse && j != a && i != b)
                            break;
                        if (gap < minimumGap && (j != a || i != b))
                            continue;
                        double error = gap / pulse - 1;
                        double cost = costs[j - a] + 3 * (error * error) + (i != b ? grid : 0);
                        if (cost < costs[i - a])
                        {
                            costs[i - a] = cost;
                            previous[i - a] = j;
                        }
                    }
                }
                for (int i = b; i != a; i = previous[i - a])
                    selected.Add(i);
            }
        }

        private static IEnumerable<int> Motifs(NestedTimings.BeatGroup[] groups, int[] hands)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                if (groups[i].Notes.Count == 1 && i >= 7 && hands[i] != hands[i - 7])
                {
                    int start = i - 7;
                    if (Enumerable.Range(start, 7).All(j => groups[j].Notes.Count == 1 && hands[j] == hands[start] &&
                        Math.Abs(groups[j].Beat - groups[start].Beat - (j - start) * .5) < Tolerance) &&
                        Math.Abs(groups[i].Beat - groups[start].Beat - 3.5) < Tolerance)
                        foreach (int hit in new[] { i - 7, i - 5, i - 3, i })
                            yield return hit;
                }
                if (groups[i].Notes.Count != 2 || hands[i] != 3 ||
                    Math.Abs(groups[i].Beat - Math.Floor(groups[i].Beat) - .5) >= Tolerance)
                    continue;
                int end = i + 1;
                while (end < groups.Length && groups[end].Notes.Count == 2 && hands[end] == 3 &&
                    Math.Abs(groups[end].Beat - groups[i].Beat - (end - i)) <= Tolerance)
                    end++;
                if (end - i >= 3)
                    for (int hit = i; hit < end; hit++)
                        yield return hit;
            }
        }

        internal static bool IsEasyDouble(IReadOnlyList<Note> group)
        {
            var (left, right) = group[0].Hand == 0 ? (group[0], group[1]) : (group[1], group[0]);
            bool coordinated = Up(left.Direction) && Up(right.Direction) || Down(left.Direction) && Down(right.Direction) ||
                left.Direction == 2 && right.Direction == 3;
            return coordinated && !SwingClearance.GroupRules(left, right).Any();

            static bool Up(double direction) => direction is 0 or 4 or 5;
            static bool Down(double direction) => direction is 1 or 6 or 7;
        }
    }
}

