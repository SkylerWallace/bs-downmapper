using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    public static class RepeatDetection
    {
        private const double Epsilon = 1e-6;
        private const double Tolerance = MeterRepeats.Tolerance;

        public static VerifiedRepeat[] Find(
            IReadOnlyList<Note> notes, IEnumerable<double> bookmarks, double endBeat,
            Func<double, double> toGrid, Func<double, double> fromGrid, bool longRepeats = false,
            bool preferPhraseBoundaries = false)
        {
            var groups = NestedTimings.Group(notes);
            var beats = groups.Select(group => toGrid(group.Beat)).ToArray();
            var counts = groups.Select(group => group.Notes.Count).ToArray();
            if (beats.Length == 0)
                return Array.Empty<VerifiedRepeat>();

            var hints = RepeatVerification.BookmarkWindows(bookmarks.Select(toGrid), toGrid(endBeat));
            var hintPairs = hints.SelectMany((window, index) => hints.Skip(index + 1).Select(other => (window, other)));
            var verifiedHints = RepeatVerification.VerifyGroups(beats, counts, hintPairs).ToArray();
            var reserved = verifiedHints.SelectMany(group => group.Occurrences).ToArray();
            var exactOffsets = MeterRepeats.LikelyOffsets(beats).ToArray();
            var variantOffsets = MeterRepeats.ShortAnchorOffsets(beats).Concat(exactOffsets).Distinct().ToArray();
            var inferred = new List<VerifiedRepeat>();
            if (preferPhraseBoundaries)
            {
                var phrasePairs = PhrasePairs(beats[0], toGrid(endBeat), variantOffsets).ToArray();
                var bookmarkReservations = reserved;
                var anchors = Verify(phrasePairs.Where(pair => pair.First.End - pair.First.Start >= 24), minimumOccurrences: 3);
                Reserve(anchors);

                // Keep a repeated continuation together before independent windows
                // can claim fragments of it. Require support in at least three occurrences.
                var recurringAnchors = new List<VerifiedRepeat>();
                foreach (var anchor in anchors)
                {
                    var ends = anchor.Occurrences.Select(window => window.End).ToHashSet();
                    double maximum = anchor.Occurrences.Min(window => window.End - window.Start) / 2;
                    var continuations = Verify(phrasePairs.Where(pair => pair.First.End - pair.First.Start <= maximum &&
                        ends.Contains(pair.First.Start) && ends.Contains(pair.Second.Start)), minimumOccurrences: 3);
                    if (continuations.Length == 0)
                        continue;
                    recurringAnchors.Add(anchor);
                    Reserve(continuations);
                }

                // Unconfirmed anchors go back into the ordinary candidate pool.
                inferred.RemoveAll(group => anchors.Contains(group) && !recurringAnchors.Contains(group));
                reserved = bookmarkReservations.Concat(inferred.SelectMany(group => group.Occurrences)).ToArray();

                // A short verified lead-in gets its own link instead of being
                // absorbed by a longer window that ends at the same anchor.
                foreach (var anchor in recurringAnchors)
                {
                    var starts = anchor.Occurrences.Select(window => window.Start).ToHashSet();
                    Reserve(Verify(phrasePairs.Where(pair => pair.First.End - pair.First.Start <= 8 &&
                        starts.Contains(pair.First.End) && starts.Contains(pair.Second.End))));
                }
                Reserve(Verify(phrasePairs));

                VerifiedRepeat[] Verify(IEnumerable<(RepeatWindow First, RepeatWindow Second)> pairs, int minimumOccurrences = 2) =>
                    RepeatVerification.VerifyGroups(beats, counts, pairs.Where(pair =>
                        reserved.All(window => !Overlaps(window, pair.First) && !Overlaps(window, pair.Second))),
                        boundaryEvidence: window => .65 * (DensityChange(window.Start) + DensityChange(window.End)),
                        minimumOccurrences: minimumOccurrences).ToArray();

                void Reserve(VerifiedRepeat[] groups)
                {
                    inferred.AddRange(groups);
                    reserved = reserved.Concat(groups.SelectMany(group => group.Occurrences)).ToArray();
                }
            }
            var fallback = Discover(beats, counts, exactOffsets, reserved, variations: false, longForm: false).ToList();
            var occupied = reserved.Concat(fallback.SelectMany(group => group.Occurrences)).ToArray();
            fallback.AddRange(Discover(beats, counts, variantOffsets, occupied, variations: true, longForm: false));
            if (longRepeats)
            {
                var extended = Discover(beats, counts, variantOffsets.Where(offset => offset >= 40 - Tolerance).ToArray(),
                    reserved, variations: true, longForm: true);
                fallback = Extend(fallback, extended);
            }

            var downbeats = MeterRepeats.LocalDownbeats(beats, counts);
            var candidates = MeterRepeats.RepeatWindows(beats, downbeats).Where(pair =>
                reserved.All(window => !Overlaps(window, pair.First) && !Overlaps(window, pair.Second)));
            fallback = Extend(fallback, RepeatVerification.VerifyGroups(beats, counts, candidates));
            return verifiedHints.Concat(inferred).Concat(fallback).Select(group => group with
            {
                Occurrences = group.Occurrences.Select(window => new RepeatWindow(fromGrid(window.Start), fromGrid(window.End)))
                    .ToArray()
            }).ToArray();

            double DensityChange(double boundary)
            {
                int middle = MeterRepeats.Bound(beats, boundary - Epsilon);
                int first = MeterRepeats.Bound(beats, boundary - 8 - Epsilon);
                int end = MeterRepeats.Bound(beats, boundary + 8 - Epsilon);
                int before = 0, after = 0;
                for (int index = first; index < middle; index++) before += counts[index];
                for (int index = middle; index < end; index++) after += counts[index];
                return Math.Abs(before - after) / (double)Math.Max(1, before + after);
            }
        }

        private static IEnumerable<(RepeatWindow First, RepeatWindow Second)> PhrasePairs(
            double origin, double end, IReadOnlyList<double> offsets)
        {
            // Anchor provisional two-bar windows to the observed opening phase.
            // The verifier still requires matching marker rhythms and note counts.
            foreach (int phrase in new[] { 8, 6 })
            foreach (double offset in offsets)
            {
                double aligned = Math.Round(offset / phrase) * phrase;
                if (aligned < phrase || Math.Abs(offset - aligned) > Tolerance)
                    continue;
                for (double start = origin; start + aligned + phrase <= end + Epsilon; start += phrase)
                    foreach (int length in new[] { phrase * 4, phrase * 2, phrase })
                        if (length <= aligned && start + aligned + length <= end + Epsilon)
                            yield return (new RepeatWindow(start, start + length),
                                new RepeatWindow(start + aligned, start + aligned + length));
            }
        }

        private static bool Overlaps(RepeatWindow a, RepeatWindow b) =>
            a.Start < b.End - Epsilon && b.Start < a.End - Epsilon;

        internal static List<VerifiedRepeat> Extend(IReadOnlyList<VerifiedRepeat> previous, IEnumerable<VerifiedRepeat> incoming)
        {
            var replaced = new HashSet<int>();
            var extensions = new List<VerifiedRepeat>();
            foreach (var extension in incoming)
            {
                var occupied = Enumerable.Range(0, previous.Count).Where(index => previous[index].Occurrences
                    .Any(old => extension.Occurrences.Any(window => Overlaps(old, window)))).ToArray();
                var peers = extension.Links.SelectMany(link => link.Select(index => (index, link)))
                    .ToLookup(item => item.index, item => item.link);
                if (occupied.Any(index =>
                    previous[index].Occurrences.Any(old => !extension.Occurrences.Any(window =>
                        window.Start <= old.Start + Epsilon && old.End <= window.End + Epsilon)) ||
                    previous[index].Links.Any(link => !peers[link[0]].Any(peer => link.All(peer.Contains)))))
                    continue;
                int oldPairs = occupied.Sum(index => PairCount(previous[index]));
                if (PairCount(extension) <= oldPairs)
                    continue;
                replaced.UnionWith(occupied);
                extensions.Add(extension);
            }
            return previous.Where((_, index) => !replaced.Contains(index)).Concat(extensions).ToList();

            static int PairCount(VerifiedRepeat group) => group.Links.Sum(link => link.Length * (link.Length - 1));
        }

        private sealed record Alignment((int Left, int Right)[] Pairs, double[] LeftIndices);
        private readonly record struct Candidate(int Matches, double Duration, int First, int End, double Offset, int LongSpan);

        private static IEnumerable<VerifiedRepeat> Discover(
            double[] beats, int[] counts, double[] offsets, IReadOnlyList<RepeatWindow> reserved, bool variations, bool longForm)
        {
            var occupied = reserved.ToList();
            var comparisons = new Dictionary<double, Alignment>();
            var spans = longForm ? new[] { 96, 80, 64, 56, 48, 40 } :
                variations ? new[] { 32, 24, 16, 12 } : new[] { 32, 24, 16, 12, 8 };
            var candidates = new List<Candidate>();
            foreach (double offset in offsets)
            {
                var alignment = Comparison(offset);
                foreach (var (left, _) in alignment.Pairs)
                {
                    if (variations && occupied.Any(window => window.Start <= beats[left] && beats[left] < window.End))
                        continue;
                    foreach (int span in spans)
                    {
                        if (span > offset + Tolerance)
                            continue;
                        int end = MeterRepeats.Bound(beats, beats[left] + span - Tolerance);
                        if (longForm && (!Free(new(beats[left], beats[end - 1] + Epsilon)) ||
                            !Free(new(beats[left] + offset, beats[end - 1] + offset + Epsilon))))
                            continue;
                        int longSpan = longForm ? span : 0;
                        var matches = RangeMatch(beats, counts, alignment, left, end, offset, variations, longSpan);
                        if (matches.Length == 0)
                            continue;
                        candidates.Add(new Candidate(matches.Length, beats[matches[^1].Left] - beats[left],
                            left, end, offset, longSpan));
                        break;
                    }
                }
            }

            foreach (var candidate in candidates.OrderByDescending(item => item.Matches)
                .ThenByDescending(item => item.Duration).ThenBy(item => item.First))
            {
                int end = candidate.End;
                var first = new RepeatWindow(beats[candidate.First], Math.Min(beats[end - 1] + .125,
                    end < beats.Length ? beats[end] : Math.Ceiling(beats[^1] + .125)));
                if (!Free(first))
                    continue;
                var members = new List<RepeatWindow> { first };
                var partners = new Dictionary<int, List<int>>();
                var probes = offsets.Where(offset => offset != candidate.Offset).Prepend(candidate.Offset)
                    .Concat(offsets.Select(offset => -offset));
                foreach (double offset in probes)
                {
                    if (Math.Abs(offset) < first.End - first.Start - Tolerance)
                        continue;
                    var translated = new RepeatWindow(first.Start + offset, first.End + offset);
                    if (!Free(translated) || members.Any(window => Overlaps(window, translated)))
                        continue;
                    var matches = RangeMatch(beats, counts, Comparison(offset), candidate.First, end, offset,
                        variations, candidate.LongSpan);
                    if (matches.Length == 0 || matches[0].Left != candidate.First)
                        continue;
                    translated = new RepeatWindow(beats[matches[0].Right],
                        Math.Max(translated.End, beats[matches[^1].Right] + Epsilon));
                    if (!Free(translated) || members.Any(window => Overlaps(window, translated)))
                        continue;
                    members.Add(translated);
                    foreach (var (left, right) in matches)
                    {
                        if (counts[left] != counts[right])
                            continue;
                        RepeatVerification.AddLink(partners, left, right);
                    }
                }
                if (members.Count < 2)
                    continue;
                occupied.AddRange(members);
                yield return RepeatVerification.BuildRepeat(members, partners);
            }

            bool Free(RepeatWindow window) => !occupied.Any(used => Overlaps(used, window));

            Alignment Comparison(double offset)
            {
                if (!comparisons.TryGetValue(offset, out var alignment))
                {
                    var pairs = RepeatVerification.MatchingMarkers(beats, offset, variations ? null : counts).ToArray();
                    comparisons.Add(offset, alignment = new Alignment(pairs, pairs.Select(pair => (double)pair.Left).ToArray()));
                }
                return alignment;
            }
        }

        private static (int Left, int Right)[] RangeMatch(
            double[] beats, int[] counts, Alignment alignment, int start, int end, double offset, bool variations, int longSpan)
        {
            int first = MeterRepeats.Bound(alignment.LeftIndices, start);
            int last = MeterRepeats.Bound(alignment.LeftIndices, end, first: first);
            int matched = last - first;
            if (matched < 8)
                return Array.Empty<(int, int)>();
            var pairs = alignment.Pairs;
            double extent = beats[pairs[last - 1].Left] - beats[pairs[first].Left];
            if (extent < Math.Max(6, .80 * longSpan) - Tolerance)
                return Array.Empty<(int, int)>();
            int rightCount = Math.Max(pairs[last - 1].Right + 1,
                MeterRepeats.Bound(beats, beats[end - 1] + offset + Tolerance, inclusive: true)) -
                Math.Min(pairs[first].Right, MeterRepeats.Bound(beats, beats[start] + offset - Tolerance));
            int leftCount = end - start;
            int largest = Math.Max(leftCount, rightCount);
            double similarity = longSpan > 0 ? .91 : variations ? .84 : .88;
            double coverage = longSpan > 0 ? .87 : variations ? .78 : .82;
            if (2.0 * matched / (leftCount + rightCount) < similarity || (double)matched / largest < coverage)
                return Array.Empty<(int, int)>();
            for (int index = first + 1; index < last; index++)
            {
                var previous = pairs[index - 1];
                var current = pairs[index];
                if (current.Left - previous.Left > 6 || current.Right - previous.Right > 6 ||
                    beats[current.Left] - beats[previous.Left] > 6)
                    return Array.Empty<(int, int)>();
            }
            var matches = pairs[first..last];
            if (variations)
            {
                double required = longSpan > 0 ? .88 * largest : .70 * matched;
                if (matches.Count(pair => counts[pair.Left] == counts[pair.Right]) < Math.Max(8, required) ||
                    !SeparatedAnchors(beats, matches, longSpan > 0 ? .50 * longSpan : 3))
                    return Array.Empty<(int, int)>();
            }
            return matches;
        }

        private static bool SeparatedAnchors(double[] beats, (int Left, int Right)[] pairs, double distance)
        {
            double? anchor = null;
            for (int index = 0; index + 2 < pairs.Length; index++)
            {
                var (left, right) = pairs[index];
                if (pairs[index + 1] != (left + 1, right + 1) || pairs[index + 2] != (left + 2, right + 2))
                    continue;
                var signature = Signature(left);
                if (signature == default || signature != Signature(right))
                    continue;
                if (anchor.HasValue && beats[left] - anchor.Value >= distance)
                    return true;
                anchor ??= beats[left];
            }
            return false;

            (double, double) Signature(int index)
            {
                double first = Math.Round((beats[index + 1] - beats[index]) * 8);
                double second = Math.Round((beats[index + 2] - beats[index + 1]) * 8);
                return Math.Min(first, second) > 0 && first != second && Math.Max(first, second) <= 32 ?
                    (first, second) : default;
            }
        }
    }
}
