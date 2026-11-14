using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record PatternSeed(Note Note, int SourceId, double SourceBeat, string Provenance, bool Mirrored);
    public sealed record DensitySection(double Start, double End, int Notes, int TimingGroups, double NotesPerSecond)
    {
        public bool SimplifyGeometry => TimingGroups / (End - Start) >= 1;
    }
    public sealed record AlternatingStream(double Start, double End, double Gap, int[] NoteIds, bool SourceDownDownEntry);

    public static class PatternFoundation
    {
        public static IReadOnlyList<PatternSeed> Seed(
            IReadOnlyList<Note> source, IReadOnlyList<Note> rhythm,
            ISet<int> sourceAnchors, ISet<int> rhythmAnchors)
        {
            var groups = NestedTimings.Group(source);
            if (groups.Count == 0) throw new InvalidDataException("The pattern source has no colored notes.");
            var beats = groups.Select(group => group.Beat).ToArray();
            var anchored = rhythm.Where(note => rhythmAnchors.Contains(note.Id))
                .Select(note => (note.Beat, note.Hand)).ToHashSet();
            var uses = new Dictionary<(double, int), int>();
            var seeded = new List<PatternSeed>(rhythm.Count);
            foreach (var note in rhythm.OrderBy(note => note.Beat).ThenBy(note => note.Hand)
                .ThenBy(note => !rhythmAnchors.Contains(note.Id)).ThenBy(note => note.Id))
            {
                int index = NestedTimings.Nearest(note.Beat, beats);
                bool exact = index >= 0;
                if (!exact) index = NestedTimings.Nearest(note.Beat, beats, double.PositiveInfinity);
                var group = groups[index];
                var same = group.Notes.Where(member => member.Hand == note.Hand);
                var key = (note.Beat, note.Hand);
                if (anchored.Contains(key)) same = same.OrderBy(member => !sourceAnchors.Contains(member.Id));
                var sameHand = same.ToArray();
                var pool = sameHand.Concat(group.Notes.Where(member => member.Hand != note.Hand)).ToArray();
                int use = uses.GetValueOrDefault(key);
                uses[key] = use + 1;
                var seed = pool[use % pool.Length];
                bool mirrored = seed.Hand != note.Hand;
                var placed = note with
                {
                    X = mirrored ? 3 - seed.X : seed.X,
                    Y = seed.Y,
                    Direction = mirrored ? Mirror(seed.Direction) : seed.Direction,
                    Angle = mirrored ? -seed.Angle : seed.Angle,
                };
                string provenance = !exact ? "nearest-time extrapolation" : use < sameHand.Length
                    ? "same-time same-hand" : "same-time reassigned/emphasis seed";
                seeded.Add(new PatternSeed(placed, seed.Id, group.Beat, provenance, mirrored));
            }
            return seeded;
        }

        public static (IReadOnlyList<DensitySection> Sections, IReadOnlyList<AlternatingStream> Streams) Sections(
            IReadOnlyList<Note> notes, int window, int minimumStreamNotes, double maximumStreamGap,
            Func<double, double, double> gapSeconds)
        {
            if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
            var groups = NestedTimings.Group(notes);
            if (groups.Count == 0) throw new InvalidDataException("The supplied rhythm has no colored notes.");
            var beats = groups.Select(group => group.Beat).ToArray();
            double end = Math.Ceiling(beats[beats.Length - 1] + 1);
            var proposals = new List<(double Change, int Beat)>();
            for (int beat = window; beat < end - window; beat++)
            {
                double before = (LowerBound(beats, beat) - LowerBound(beats, beat - window)) / (double)window;
                double after = (LowerBound(beats, beat + window) - LowerBound(beats, beat)) / (double)window;
                double change = Math.Abs(after - before);
                if (change >= .75 && change / Math.Max(.25, Math.Max(before, after)) >= .5)
                    proposals.Add((change, beat));
            }
            var boundaries = new SortedSet<double> { 0, end };
            var accepted = new List<int>();
            foreach (var proposal in proposals.OrderByDescending(item => item.Change).ThenByDescending(item => item.Beat))
            {
                if (accepted.Any(other => Math.Abs(other - proposal.Beat) < window)) continue;
                accepted.Add(proposal.Beat);
                boundaries.Add(proposal.Beat);
            }
            for (int i = 1; i < beats.Length; i++)
            {
                if (beats[i] - beats[i - 1] < 2) continue;
                boundaries.Add(beats[i - 1] + 1);
                boundaries.Add(beats[i]);
            }
            var streams = Streams(groups, minimumStreamNotes, maximumStreamGap);
            foreach (var stream in streams)
            {
                boundaries.Add(stream.Start);
                boundaries.Add(stream.End);
            }
            var bounds = boundaries.ToArray();
            var sections = new List<DensitySection>(bounds.Length - 1);
            for (int i = 1; i < bounds.Length; i++)
            {
                double start = bounds[i - 1], stop = bounds[i];
                var contents = notes.Where(note => start <= note.Beat && note.Beat < stop).ToArray();
                sections.Add(new DensitySection(start, stop, contents.Length, NestedTimings.Group(contents).Count,
                    contents.Length / gapSeconds(start, stop)));
            }
            return (sections, streams);
        }

        private static List<AlternatingStream> Streams(
            List<NestedTimings.BeatGroup> groups, int minimumNotes, double maximumGap)
        {
            var streams = new List<AlternatingStream>();
            for (int i = 0; i + 1 < groups.Count;)
            {
                var first = groups[i].Notes;
                var second = groups[i + 1].Notes;
                double gap = second[0].Beat - first[0].Beat;
                if (first.Count != 1 || second.Count != 1 || gap <= 0 || gap > maximumGap + .0001)
                {
                    i++;
                    continue;
                }
                int end = i + 1;
                while (end < groups.Count && groups[end].Notes.Count == 1 &&
                    groups[end].Notes[0].Hand != groups[end - 1].Notes[0].Hand &&
                    Math.Abs(groups[end].Notes[0].Beat - groups[end - 1].Notes[0].Beat - gap) < .0001)
                    end++;
                if (end - i < minimumNotes)
                {
                    i++;
                    continue;
                }
                var ids = new int[end - i];
                for (int j = i; j < end; j++) ids[j - i] = groups[j].Notes[0].Id;
                bool entry = first[0].Hand == 1 && second[0].Hand == 0 &&
                    first[0].Direction == 1 && second[0].Direction == 1;
                streams.Add(new AlternatingStream(first[0].Beat, groups[end - 1].Notes[0].Beat + gap, gap, ids, entry));
                i = end;
            }
            return streams;
        }

        private static int LowerBound(double[] values, double value)
        {
            int index = Array.BinarySearch(values, value);
            return index < 0 ? ~index : index;
        }

        private static double Mirror(double direction) => direction switch
        {
            2 => 3, 3 => 2, 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => direction,
        };
    }
}
