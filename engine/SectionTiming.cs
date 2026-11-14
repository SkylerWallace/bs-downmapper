using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public static class SectionTiming
    {
        public static (IReadOnlyList<RepeatWindow> Windows, ISet<double> Accents) EasySparseCues(
            IReadOnlyList<Note> notes, IReadOnlyList<Wall> walls, IReadOnlyList<Bomb> bombs, Func<double, double> seconds)
        {
            var beats = NestedTimings.DistinctBeats(notes);
            var runs = new List<List<RepeatWindow>>();
            foreach (var wall in walls.Where(wall => wall.Duration >= 3 &&
                seconds(wall.Beat + wall.Duration) - seconds(wall.Beat) >= 1.5)
                .OrderBy(wall => wall.Beat).ThenBy(wall => wall.Duration))
            {
                if (runs.Count == 0 || wall.Beat > runs[^1][^1].End + 2)
                    runs.Add(new List<RepeatWindow>());
                var run = runs[^1];
                if (run.Count == 0 || wall.Beat > run[^1].Start + .0001)
                    run.Add(new RepeatWindow(wall.Beat, wall.Beat + wall.Duration));
            }
            var windows = new List<RepeatWindow>();
            var accents = new HashSet<double>();
            foreach (var run in runs.Where(run => run.Count >= 3))
            {
                var entries = run.Select(wall => MeterRepeats.Bound(beats, wall.Start - .0001)).ToArray();
                if (entries.Where((index, wall) => index >= beats.Length || beats[index] > run[wall].Start + 2.0001).Any())
                    continue;
                accents.UnionWith(entries.Select(index => beats[index]));
                windows.Add(new RepeatWindow(run[0].Start, run[^1].End + 2));
            }

            var clusters = new List<List<double>>();
            foreach (double beat in bombs.Select(bomb => Math.Round(bomb.Beat, 4)).Distinct().OrderBy(beat => beat))
            {
                if (clusters.Count == 0 || beat - clusters[^1][^1] > .26)
                    clusters.Add(new List<double>());
                clusters[^1].Add(beat);
            }
            var fills = clusters.Where(cluster => cluster.Count >= 3 &&
                cluster[0] - Math.Floor(cluster[0]) <= .5 && cluster[^1] - cluster[0] <= .75)
                .Select(cluster =>
                {
                    double start = Math.Floor(cluster[0]);
                    string pattern = string.Join(",", cluster.Select(beat => (int)Math.Round((beat - start) * 1000)));
                    return (Start: start, End: cluster[^1], Pattern: pattern);
                }).ToArray();
            var counts = fills.ToLookup(fill => fill.Pattern);
            foreach (var fill in fills)
            {
                if (counts[fill.Pattern].Count() < 2)
                    continue;
                int before = NestedTimings.Nearest(fill.Start - 2, beats);
                int after = NestedTimings.Nearest(fill.Start + 2, beats);
                if (before < 0 || after < 0)
                    continue;
                accents.Add(beats[before]);
                accents.Add(beats[after]);
                windows.Add(new RepeatWindow(fill.Start - 1, fill.End + .5));
            }
            return (windows, accents);
        }

        public static IReadOnlyList<Note> Prune(
            IReadOnlyList<Note> notes, IReadOnlyList<DensitySection> sections, IReadOnlyList<AlternatingStream> streams,
            ISet<int> fixedIds, double minimumRatio, double maximumRatio, Func<double, double> seconds,
            IReadOnlyDictionary<double, double> scores, ISet<double> linkedAccents,
            IReadOnlyList<Note> budgetReference = null)
        {
            var protectedIds = new HashSet<int>(fixedIds);
            foreach (var stream in streams)
                protectedIds.UnionWith(minimumRatio >= .8 ? stream.NoteIds : stream.NoteIds.Take(2));
            double target = (minimumRatio + maximumRatio) / 2;
            var referenceBeats = budgetReference?.Select(note => note.Beat).OrderBy(beat => beat).ToArray();
            var retained = new List<Note>();
            foreach (var section in sections)
            {
                var groups = NestedTimings.Group(notes.Where(note => section.Start <= note.Beat && note.Beat < section.End));
                if (groups.Count == 0)
                    continue;
                int count = groups.Sum(group => group.Notes.Count);
                int referenceCount = referenceBeats == null ? count :
                    MeterRepeats.Bound(referenceBeats, section.End) - MeterRepeats.Bound(referenceBeats, section.Start);
                int budget = Math.Min(count, Math.Max(1, Math.Max((int)Math.Ceiling(referenceCount * minimumRatio),
                    (int)Math.Round(referenceCount * target))));
                var removed = new HashSet<int>();
                var interior = Enumerable.Range(1, Math.Max(0, groups.Count - 2)).ToArray();
                foreach (int index in interior.OrderBy(Salience))
                {
                    var group = groups[index].Notes;
                    if (count - group.Count < budget || group.Any(note => protectedIds.Contains(note.Id)))
                        continue;
                    if (minimumRatio >= .8 && (group.Count == 1 || Whole(group[0].Beat)))
                        continue;
                    removed.UnionWith(group.Select(note => note.Id));
                    count -= group.Count;
                }
                foreach (int index in interior)
                {
                    if (count <= budget)
                        break;
                    var group = groups[index].Notes;
                    if (group.Count < 2 || group.Any(note => removed.Contains(note.Id)))
                        continue;
                    var hands = group.ToLookup(note => note.Hand);
                    int remaining = group.Count;
                    foreach (var note in group.OrderByDescending(note => hands[note.Hand].Count())
                        .ThenBy(note => note.Hand == index % 2).ThenBy(note => note.Y))
                    {
                        if (count <= budget || remaining <= 1)
                            break;
                        if (!protectedIds.Contains(note.Id))
                        {
                            removed.Add(note.Id);
                            remaining--;
                            count--;
                        }
                    }
                }
                foreach (var group in groups)
                {
                    var kept = group.Notes.Where(note => !removed.Contains(note.Id)).ToArray();
                    if (maximumRatio > .5 || kept.Length <= 1)
                    {
                        retained.AddRange(kept);
                        continue;
                    }
                    foreach (var hand in kept.GroupBy(note => note.Hand).OrderBy(group => group.Key))
                    {
                        var fixedNotes = hand.Where(note => fixedIds.Contains(note.Id)).ToArray();
                        if (fixedNotes.Length > 0)
                            retained.AddRange(fixedNotes);
                        else
                            retained.Add(hand.OrderBy(note => note.Y).ThenBy(note => Math.Abs(note.X - note.Hand - 1)).First());
                    }
                }

                double Salience(int index)
                {
                    var group = groups[index].Notes;
                    double beat = group[0].Beat, time = seconds(beat);
                    double before = time - seconds(groups[index - 1].Notes[0].Beat);
                    double after = seconds(groups[index + 1].Notes[0].Beat) - time;
                    double key = Math.Round(beat, 5);
                    return (Whole(beat) ? 3 : 0) + (Math.Abs(beat * 2 - Math.Round(beat * 2)) < .0002 ? 1 : 0) +
                        group.Count - 1 + Math.Min(1, Math.Max(before, after)) +
                        .75 * scores.GetValueOrDefault(key) + (linkedAccents.Contains(key) ? .35 : 0);
                }
            }
            return retained;

            static bool Whole(double beat) => Math.Abs(beat - Math.Round(beat)) < .0001;
        }
    }
}
