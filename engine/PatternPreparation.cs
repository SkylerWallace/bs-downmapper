using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record PreparedPattern(
        IReadOnlyList<PatternSeed> Seeds, ISet<int> Anchors,
        IReadOnlyList<ChartEndpoint> Endpoints, ISet<ChartLinkId> ReleasedLinks);

    public static class PatternPreparation
    {
        public static PreparedPattern Prepare(StandardLevel foundation, PreparedTiming rhythm,
            IReadOnlyList<Note> approved)
        {
            var level = rhythm.Level;
            if (approved.Count == 0)
                throw new InvalidDataException("Pattern generation requires approved hit timings.");
            if (Math.Abs(foundation.Bpm - level.Bpm) > .0001)
                throw new InvalidDataException("The rhythm and pattern source must have the same BPM.");
            if (!SameAudio(foundation.AudioPath, level.AudioPath))
                throw new InvalidDataException("The rhythm and pattern source must reference the same audio.");
            double lastBeat = Math.Max(foundation.Gameplay.Notes[^1].Beat, approved[^1].Beat);
            if (foundation.Clock.Regions.Concat(level.Clock.Regions).Select(region => region.Beat).Append(lastBeat)
                .Distinct().Any(beat => Math.Abs(foundation.Clock.Seconds(beat) - level.Clock.Seconds(beat)) > .001))
                throw new InvalidDataException("The rhythm and pattern source must use the same tempo map.");

            var byId = approved.ToDictionary(note => note.Id);
            var source = level.Gameplay.Notes.ToDictionary(note => note.Id);
            var released = rhythm.Links.Released.ToHashSet();
            var retained = new List<ChartLink>();
            foreach (var link in level.Gameplay.ReadLinks())
            {
                if (released.Contains(link.Id))
                    continue;
                bool attachmentsSurvive = link.AnchorIds.All(id =>
                    byId.TryGetValue(id, out var note) && note.Beat == source[id].Beat);
                if (!attachmentsSurvive)
                {
                    if (!rhythm.Links.Optional.Contains(link.Id))
                        throw new InvalidDataException("The selected rhythm removed a protected arc or chain attachment.");
                    released.Add(link.Id);
                    continue;
                }
                retained.Add(link);
            }

            var anchors = retained.SelectMany(link => link.AnchorIds).ToHashSet();
            var endpoints = retained.SelectMany(link => link.Endpoints).ToArray();
            var sourceAnchors = foundation.Gameplay.ReadLinks().SelectMany(link => link.AnchorIds).ToHashSet();
            var seeds = PatternFoundation.Seed(foundation.Gameplay.Notes, approved, sourceAnchors, anchors)
                .Select(seed => anchors.Contains(seed.Note.Id) ? seed with { Note = source[seed.Note.Id] } : seed).ToArray();
            return new PreparedPattern(seeds, anchors, endpoints, released);
        }

        private static bool SameAudio(string first, string second)
        {
            if (string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase))
                return true;
            using var a = File.OpenRead(first);
            using var b = File.OpenRead(second);
            return a.Length == b.Length && EngineFiles.Hash(a) == EngineFiles.Hash(b);
        }
    }
}
