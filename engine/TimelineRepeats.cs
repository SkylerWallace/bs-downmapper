using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record ScopedRepeats(IReadOnlyList<VerifiedRepeat> Sections,
        IReadOnlyList<(RepeatWindow Scope, IReadOnlyList<VerifiedRepeat> Groups)> Subsections);

    public static class TimelineRepeats
    {
        public static ScopedRepeats Find(StandardLevel level, IReadOnlyList<Note> notes)
        {
            var bookmarks = level.Bookmarks();
            var global = RepeatDetection.Find(notes, bookmarks, level.EndBeat, level.ToGrid, level.FromGrid,
                preferPhraseBoundaries: true);
            var groups = NestedTimings.Group(notes);
            var beats = groups.Select(group => level.ToGrid(group.Beat)).ToArray();
            var counts = groups.Select(group => group.Notes.Count).ToArray();
            var downbeats = MeterRepeats.LocalDownbeats(beats, counts);
            var scopes = RepeatVerification.BookmarkWindows(bookmarks.Select(level.ToGrid), level.ToGrid(level.EndBeat)).ToList();
            foreach (var window in global.SelectMany(group => group.Occurrences).Select(window =>
                new RepeatWindow(level.ToGrid(window.Start), level.ToGrid(window.End))))
                if (scopes.All(scope => scope.End <= window.Start + 1e-6 || window.End <= scope.Start + 1e-6))
                    scopes.Add(window);
            var local = new List<(RepeatWindow, IReadOnlyList<VerifiedRepeat>)>();
            foreach (var scope in scopes.OrderBy(scope => scope.Start).ThenBy(scope => scope.End))
            {
                var windows = PhraseWindows(scope).ToArray();
                var baseline = RepeatVerification.VerifyGroups(beats, counts, Pairs(windows), local: true,
                    allowExtraBeat: false).ToArray();
                var meter = MeterWindows(scope, downbeats).ToArray();
                var linked = meter.Length == 0 ? baseline :
                    RepeatDetection.Extend(baseline, RepeatVerification.VerifyGroups(beats, counts,
                        Pairs(windows.Concat(meter)), local: true)).ToArray();
                if (linked.Length > 0)
                    local.Add((Convert(scope), linked.Select(group => group with
                    {
                        Occurrences = group.Occurrences.Select(Convert).ToArray(),
                    }).ToArray()));
            }
            return new ScopedRepeats(global, local);

            RepeatWindow Convert(RepeatWindow window) => new(level.FromGrid(window.Start), level.FromGrid(window.End));
        }

        private static IEnumerable<(RepeatWindow, RepeatWindow)> Pairs(IEnumerable<RepeatWindow> source)
        {
            var windows = source.Distinct().OrderBy(window => window.Start).ThenBy(window => window.End).ToArray();
            for (int i = 0; i < windows.Length; i++)
                for (int j = i + 1; j < windows.Length; j++)
                    yield return (windows[i], windows[j]);
        }

        private static IEnumerable<RepeatWindow> PhraseWindows(RepeatWindow section)
        {
            double duration = section.End - section.Start;
            var lengths = new HashSet<double> { 4, 6, 8, 12, 16, 24, 32 };
            foreach (int divisor in new[] { 2, 3, 4 })
            {
                double length = duration / divisor;
                if (length >= 4 - 1e-6 && length <= 32 + 1e-6)
                    lengths.Add(Math.Round(length, 5));
            }
            foreach (double length in lengths)
                for (int i = 0; i < Math.Floor((duration + length * .045 + 1e-6) / length); i++)
                    yield return new RepeatWindow(section.Start + i * length,
                        Math.Min(section.End, section.Start + (i + 1) * length));
        }

        private static IEnumerable<RepeatWindow> MeterWindows(RepeatWindow section,
            IReadOnlyDictionary<double, HashSet<int>> downbeats)
        {
            foreach (var (start, periods) in downbeats)
            {
                if (start < section.Start || start >= section.End)
                    continue;
                foreach (int length in periods.SelectMany(period => new[] { period, 2 * period, 4 * period, 8 * period }).Distinct())
                    if (length >= 3 && length <= 32 && start + length <= section.End + MeterRepeats.Tolerance)
                        yield return new RepeatWindow(start, Math.Min(section.End, start + length));
            }
        }
    }
}
