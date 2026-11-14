using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    public static class StageChart
    {
        public static string Write(string stagingFolder, StandardLevel rhythm, StandardLevel foundation, string target,
            JsonObject generated, double njs, double offset, IReadOnlyList<Note> approved)
        {
            int rank = Array.IndexOf(TimingPreparation.Standard.ToArray(), target);
            if (rank <= 0 || !double.IsFinite(njs) || njs <= 0 || !double.IsFinite(offset))
                throw new InvalidDataException("A generated chart needs a lower Standard difficulty and valid jump settings.");
            var notes = ChartGameplay.Read(generated).Notes;
            if (!Signature(notes).SequenceEqual(Signature(approved)))
                throw new InvalidDataException("Export changed the approved beats, hands, or note counts.");
            string infoPath = Path.Combine(stagingFolder, "Info.dat");
            var info = JsonNode.Parse(File.ReadAllText(infoPath)).AsObject();
            bool modern = info["difficultyBeatmaps"] is JsonArray;
            string key = modern ? "beatmapDataFilename" : "_beatmapFilename";
            string nameKey = modern ? "difficulty" : "_difficulty";
            var entries = modern ? (JsonArray)info["difficultyBeatmaps"] : ((JsonArray)info["_difficultyBeatmapSets"])
                .OfType<JsonObject>().Single(set => Text(set, "_beatmapCharacteristicName") == "Standard")["_difficultyBeatmaps"].AsArray();
            var all = modern ? entries.OfType<JsonObject>() : ((JsonArray)info["_difficultyBeatmapSets"])
                .OfType<JsonObject>().SelectMany(set => set["_difficultyBeatmaps"].AsArray().OfType<JsonObject>());
            var old = entries.OfType<JsonObject>().FirstOrDefault(entry =>
                Text(entry, nameKey) == target && (!modern || Text(entry, "characteristic") == "Standard"));
            var reserved = all.Where(entry => entry != old).Select(entry => Text(entry, key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            reserved.Add("Info.dat");
            if (modern)
            {
                foreach (var entry in all)
                    reserved.Add(Text(entry, "lightshowDataFilename"));
                reserved.Add(Text(info, "coverImageFilename"));
                reserved.Add(Text(info, "songPreviewFilename"));
                reserved.Add(Text(info["audio"], "songFilename"));
                reserved.Add(Text(info["audio"], "audioDataFilename"));
            }
            else
            {
                reserved.Add(Text(info, "_songFilename"));
                reserved.Add(Text(info, "_coverImageFilename"));
            }

            string existing = Text(old, key);
            string filename = existing;
            if (filename == null || reserved.Contains(filename))
            {
                string stem = modern ? target + ".beatmap" : target + "Standard";
                filename = stem + ".dat";
                while (reserved.Contains(filename) || EngineFiles.Exists(Path.Combine(stagingFolder, filename)))
                    filename = $"{stem}.downmap-{Guid.NewGuid():N}.dat";
            }
            if (filename != Path.GetFileName(filename) || filename is "." or ".." ||
                filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Generated difficulty filenames must be flat local filenames.");

            var metadata = (JsonObject)rhythm.Metadata.DeepClone();
            metadata[nameKey] = target;
            metadata[key] = filename;
            metadata[modern ? "noteJumpMovementSpeed" : "_noteJumpMovementSpeed"] = njs;
            metadata[modern ? "noteJumpStartBeatOffset" : "_noteJumpStartBeatOffset"] = offset;
            if (modern)
            {
                metadata["characteristic"] = "Standard";
                metadata["beatmapAuthors"] = new JsonObject
                {
                    ["mappers"] = foundation.Metadata["beatmapAuthors"]?["mappers"]?.DeepClone() ?? new JsonArray(),
                    ["lighters"] = metadata["beatmapAuthors"]?["lighters"]?.DeepClone() ?? new JsonArray(),
                };
            }
            else
                metadata["_difficultyRank"] = 9 - 2 * rank;
            if (old == null)
                entries.Add(metadata);
            else
                entries[entries.IndexOf(old)] = metadata;

            var output = (JsonObject)generated.DeepClone();
            if (output.ContainsKey("njsEvents")) output["njsEvents"] = new JsonArray();
            if (output.ContainsKey("njsEventData")) output["njsEventData"] = new JsonArray();
            string path = Path.Combine(stagingFolder, filename);
            File.WriteAllText(path, output.ToJsonString());
            File.WriteAllText(infoPath, info.ToJsonString());
            return path;
        }

        private static IEnumerable<(double Beat, int Hand)> Signature(IEnumerable<Note> notes) =>
            notes.Select(note => (note.Beat, note.Hand)).OrderBy(note => note.Beat).ThenBy(note => note.Hand);

        private static string Text(JsonNode node, string key) => node?[key]?.GetValue<string>();
    }
}
