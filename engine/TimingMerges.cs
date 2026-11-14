using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BsDownmap.Engine
{
    public readonly record struct TimingMerge(int NoteId, int TargetId);

    public static class TimingMerges
    {
        private const double Tolerance = .0001;

        public static IReadOnlyList<Note> ApplyEditorEdits(
            IReadOnlyList<Note> original, IReadOnlyList<double> removedBeats, IReadOnlyList<int> removedNoteIds,
            IReadOnlyList<TimingMerge> merges, ISet<int> linkedAnchors, double endBeat)
        {
            var groups = NestedTimings.Group(original);
            var beats = groups.Select(group => group.Beat).ToArray();
            var originalIds = original.Select(note => note.Id).ToHashSet();
            var removed = new HashSet<int>();
            foreach (double beat in removedBeats)
            {
                if (!double.IsFinite(beat) || beat < 0 || beat > endBeat)
                    throw new InvalidDataException("Timing edits must be on the song timeline.");
                int index = NestedTimings.Nearest(beat, beats);
                if (index < 0)
                    throw new InvalidDataException("A removed timing no longer exists in the saved chart. Reopen the timing editor.");
                removed.UnionWith(groups[index].Notes.Select(note => note.Id));
            }
            foreach (int id in removedNoteIds)
            {
                if (!originalIds.Contains(id))
                    throw new InvalidDataException("A removed note no longer exists in the saved chart. Reopen the timing editor.");
                removed.Add(id);
            }
            if (linkedAnchors.Overlaps(removed))
                throw new InvalidDataException("A timing attached to a retained arc or chain cannot be deleted.");
            if (removed.Count == original.Count)
                throw new InvalidDataException("Keep at least one timing in the song.");
            var retained = original.Where(note => !removed.Contains(note.Id)).ToArray();
            return Apply(original, retained, merges, linkedAnchors)
                .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();
        }

        public static IReadOnlyList<TimingMerge> Propose(
            IReadOnlyList<Note> original, IReadOnlyList<Note> retained, IReadOnlyList<DensitySection> sections,
            IReadOnlyList<AlternatingStream> streams, ISet<int> protectedIds,
            IReadOnlyList<Bomb> bombs, IReadOnlyList<Wall> walls, IReadOnlyList<double> linkedEndpointBeats,
            Func<double, double> seconds)
        {
            var kept = retained.Select(note => note.Id).ToHashSet();
            var locked = protectedIds.Concat(streams.SelectMany(stream => stream.NoteIds)).ToHashSet();
            var groups = NestedTimings.Group(original);
            var merges = new List<TimingMerge>();
            if (sections.Count == 0)
                return merges;
            int section = 0;
            for (int index = 0; index + 1 < groups.Count; index++)
            {
                var first = groups[index].Notes;
                var second = groups[index + 1].Notes;
                if (first.Count != 1 || second.Count != 1)
                    continue;
                var a = first[0];
                var b = second[0];
                if (a.Hand == b.Hand || !Editable(a) || !Editable(b) ||
                    Math.Abs(b.Beat - a.Beat - .25) > Tolerance || seconds(b.Beat) - seconds(a.Beat) > .14)
                    continue;
                if (Math.Abs(a.Beat * 4 - Math.Round(a.Beat * 4)) > .0004 ||
                    index > 0 && a.Beat - groups[index - 1].Notes[0].Beat < .5 - Tolerance ||
                    index + 2 < groups.Count && groups[index + 2].Notes[0].Beat - b.Beat < .5 - Tolerance)
                    continue;
                while (section + 1 < sections.Count && a.Beat >= sections[section].End)
                    section++;
                if (b.Beat >= sections[section].End || bombs.Any(bomb => Nearby(bomb.Beat)) ||
                    walls.Any(wall => wall.Beat <= b.Beat && wall.Beat + wall.Duration >= a.Beat) ||
                    linkedEndpointBeats.Any(Nearby))
                    continue;
                var (left, right) = a.Hand == 0 ? (a, b) : (b, a);
                if (left.X >= 2 || right.X < 2 || SwingClearance.GroupRules(a, b).Any())
                    continue;

                var (target, moved) = Math.Round(a.Beat * 4) % 2 == 0 ? (a, b) : (b, a);
                merges.Add(new TimingMerge(moved.Id, target.Id));

                bool Nearby(double beat) => a.Beat - .5 <= beat && beat <= b.Beat + .5;
            }
            return merges;

            bool Editable(Note note) => kept.Contains(note.Id) && !locked.Contains(note.Id);
        }

        public static IReadOnlyList<Note> Apply(
            IReadOnlyList<Note> original, IReadOnlyList<Note> retained, IReadOnlyList<TimingMerge> merges,
            ISet<int> linkedAnchors)
        {
            if (merges.Count == 0)
                return retained;
            var byId = retained.ToDictionary(note => note.Id);
            var singles = NestedTimings.Group(original).Where(group => group.Notes.Count == 1)
                .Select(group => group.Notes[0].Id).ToHashSet();
            var used = new HashSet<int>();
            var moved = new Dictionary<int, double>();
            foreach (var merge in merges)
            {
                if (!byId.TryGetValue(merge.NoteId, out var source) || !byId.TryGetValue(merge.TargetId, out var target) ||
                    !Editable(merge.NoteId) || !Editable(merge.TargetId) || merge.NoteId == merge.TargetId)
                    throw new InvalidDataException("A timing merge needs two available, unlinked original singles.");
                if (source.Hand == target.Hand || Math.Abs(Math.Abs(source.Beat - target.Beat) - .25) > Tolerance ||
                    Math.Abs(target.Beat * 2 - Math.Round(target.Beat * 2)) > .0002)
                    throw new InvalidDataException("A timing merge needs opposite hands and a nearby half-beat destination.");
                used.Add(source.Id);
                used.Add(target.Id);
                moved.Add(source.Id, target.Beat);
            }
            return retained.Select(note => moved.TryGetValue(note.Id, out double beat) ? note with { Beat = beat } : note)
                .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();

            bool Editable(int id) => singles.Contains(id) && !used.Contains(id) && !linkedAnchors.Contains(id);
        }
    }
}
