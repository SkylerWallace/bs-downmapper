using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public static class EasyDraftRefinement
    {
        public static IReadOnlyList<Note> Refine(
            IReadOnlyList<Note> original, IReadOnlyList<Note> retained, IReadOnlyList<TimingMerge> merges,
            ISet<int> protectedIds, IReadOnlyList<Bomb> bombs, IReadOnlyList<Wall> walls,
            IReadOnlyDictionary<double, double> rankScores, IReadOnlyDictionary<double, double> mixScores,
            Func<double, double> seconds, Func<double, double> toGrid, Func<double, double, double> gridGap,
            double minimumGap, double pulse)
        {
            var groups = NestedTimings.Group(original);
            var raw = groups.Select(group => group.Beat).ToArray();
            var times = raw.Select(seconds).ToArray();
            var beats = raw.Select(toGrid).ToArray();
            var beatSeconds = beats.Select(beat => gridGap(beat, beat + 1)).ToArray();
            var rank = raw.Select(beat => rankScores[beat]).ToArray();
            var mix = raw.Select(beat => mixScores[beat]).ToArray();
            var byId = groups.SelectMany((group, index) => group.Notes.Select(note => (note.Id, index)))
                .ToDictionary(row => row.Id, row => row.index);
            var locked = protectedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToHashSet();
            locked.UnionWith(merges.SelectMany(merge => new[] { byId[merge.NoteId], byId[merge.TargetId] }));
            var (windows, accents) = SectionTiming.EasySparseCues(original, walls, bombs, seconds);
            locked.UnionWith(Enumerable.Range(0, raw.Length).Where(i => accents.Contains(raw[i])));
            var suppressed = Enumerable.Range(0, raw.Length).Where(i => !accents.Contains(raw[i]) &&
                windows.Any(window => window.Start - .0001 <= raw[i] && raw[i] <= window.End + .0001)).ToHashSet();
            var destination = merges.ToDictionary(merge => merge.NoteId, merge => merge.TargetId);
            var kept = retained.GroupBy(note => byId[destination.GetValueOrDefault(note.Id, note.Id)])
                .ToDictionary(group => group.Key, group => group.ToList());
            var nearBeat = beats.Select(beat => Math.Abs(beat - Math.Round(beat)) <= .065).ToArray();

            ReplaceSingles(slow: false);
            foreach (var block in Enumerable.Range(0, beats.Length).GroupBy(i => Math.Floor((beats[i] + .001) / 8)))
            {
                var subdivisions = block.Where(i => Math.Abs(beats[i] - Math.Round(beats[i])) > .10).ToArray();
                var rates = block.Select(i => beatSeconds[i]).OrderBy(rate => rate).ToArray();
                double median = (rates[(rates.Length - 1) / 2] + rates[rates.Length / 2]) / 2;
                if (subdivisions.Length < 4 || median < .84 * pulse)
                    continue;
                var thirds = block.Where(i => PhaseError(beats[i], 3) < .015).ToHashSet();
                if (subdivisions.Count(thirds.Contains) < .30 * subdivisions.Length)
                    continue;
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    var order = kept.Keys.OrderBy(i => i).ToArray();
                    var choices = new List<(double Score, int Index)>();
                    foreach (int i in thirds.OrderBy(i => i))
                    {
                        if (kept.ContainsKey(i) || locked.Contains(i) || suppressed.Contains(i))
                            continue;
                        int position = ~Array.BinarySearch(order, i);
                        if (position <= 0 || position >= order.Length)
                            continue;
                        double left = times[i] - times[order[position - 1]], right = times[order[position]] - times[i];
                        if (Math.Min(left, right) >= minimumGap - 1e-8)
                            choices.Add((rank[i] + .65 * mix[i] + .30 * Math.Min(left, right) / (left + right), i));
                    }
                    if (choices.Count == 0)
                        break;
                    var choice = choices.Max();
                    int donor = kept.Keys.Where(i => kept[i].Count == 2 && !locked.Contains(i) &&
                        Math.Abs(times[choice.Index] - times[i]) < 60)
                        .OrderBy(i => rank[i] + .20 * mix[i]).DefaultIfEmpty(-1).First();
                    if (donor < 0 || choice.Score < .27 + .30 * rank[donor])
                        break;
                    var spare = kept[donor][1];
                    kept[donor].RemoveAt(1);
                    Place(choice.Index, spare);
                }
            }
            ReplaceSingles(slow: true);
            return kept.Values.SelectMany(group => group).OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();

            void Place(int index, Note note) => kept[index] = new List<Note>
            {
                groups[index].Notes.OrderBy(candidate => candidate.Hand != note.Hand).ThenBy(candidate => candidate.Y)
                    .ThenBy(candidate => Math.Abs(candidate.X - candidate.Hand - 1)).First()
            };

            void ReplaceSingles(bool slow)
            {
                foreach (int i in kept.Keys.OrderBy(i => i).Skip(1).SkipLast(1).ToArray())
                {
                    if (locked.Contains(i) || kept[i].Count != 1)
                        continue;
                    if (slow ? beatSeconds[i] < .90 * pulse || mix[i] > .45 :
                        nearBeat[i] || beatSeconds[i] < minimumGap + .01 || beatSeconds[i] > .88 * pulse)
                        continue;
                    double radius = slow ? .51 : .60;
                    var ordered = kept.Keys.OrderBy(j => j).ToArray();
                    int pos = Array.BinarySearch(ordered, i);
                    double before = times[ordered[pos - 1]], after = times[ordered[pos + 1]];
                    double spacing = Math.Min(minimumGap, Math.Min(times[i] - before, after - times[i]));
                    int first = Math.Min(i + 1, MeterRepeats.Bound(beats, beats[i] - radius, first: ordered[pos - 1] + 1));
                    int next = -1;
                    double best = double.NegativeInfinity;
                    for (int j = first; j < ordered[pos + 1] && beats[j] - beats[i] <= radius; j++)
                    {
                        if (kept.ContainsKey(j) || locked.Contains(j) || suppressed.Contains(j) ||
                            Math.Min(times[j] - before, after - times[j]) < spacing - 1e-8)
                            continue;
                        if (slow ? Math.Min(PhaseError(beats[j], 2), PhaseError(beats[j], 3)) > .015 ||
                            mix[j] < 1.4 * mix[i] : !nearBeat[j])
                            continue;
                        double value = Quality(j);
                        if (value > best)
                        {
                            best = value;
                            next = j;
                        }
                    }
                    if (next >= 0 && best - Quality(i) > (slow ? .30 : .025))
                    {
                        var displaced = kept[i][0];
                        kept.Remove(i);
                        Place(next, displaced);
                    }

                    double Quality(int j)
                    {
                        double left = times[j] - before, right = after - times[j];
                        if (slow)
                            return .35 * rank[j] + mix[j] + .25 * Math.Min(left, right) / (left + right);
                        double phase = nearBeat[j] ? .75 : Math.Abs(beats[j] - Math.Round(beats[j])) > .435 ? .1125 : 0;
                        return rank[j] + .25 * mix[j] + phase - .4 *
                            ((left - pulse) * (left - pulse) + (right - pulse) * (right - pulse));
                    }
                }
            }

            static double PhaseError(double beat, int subdivision) => Math.Abs(beat * subdivision - Math.Round(beat * subdivision));
        }
    }
}
