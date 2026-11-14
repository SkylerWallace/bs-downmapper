using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    public static class SpreadPublisher
    {
        private const string InfoFilename = "Info.dat";

        public static void Publish(
            string song, string staged, string reports, string expectedInfo, IReadOnlyDictionary<string, string> expectedTargets)
        {
            song = Path.GetFullPath(song);
            string lockPath = Path.Combine(song, ".bs-downmap.lock");
            string token = Path.Combine(song, ".bs-downmap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(token);
            try
            {
                Directory.Move(token, lockPath);
            }
            catch (IOException error)
            {
                Directory.Delete(token);
                if (Directory.Exists(lockPath) || File.Exists(lockPath))
                    throw new IOException("This song is locked by another BS Downmapper update.", error);
                throw;
            }

            try
            {
                var original = ReadInfo(song);
                var generated = ReadInfo(staged);
                VerifySnapshot(song, original, expectedInfo, expectedTargets);
                var saved = new Dictionary<string, double[]>();
                foreach (var item in original)
                {
                    string file = Path.Combine(song, item.Value);
                    if (!File.Exists(file)) continue;
                    var beats = ReadTimings(file);
                    if (beats.Length > 0) saved[item.Key] = beats;
                }
                var files = new List<string>();
                var newFiles = new List<string>();
                foreach (string rank in expectedTargets.Keys)
                {
                    if (!generated.TryGetValue(rank, out string name))
                        throw new InvalidDataException($"The staged spread is missing Standard {rank}.");
                    if (string.Equals(name, InfoFilename, StringComparison.OrdinalIgnoreCase) ||
                        files.Contains(name, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("Staged difficulties must have distinct chart filenames.");
                    if (!original.TryGetValue(rank, out string previous) ||
                        !string.Equals(previous, name, StringComparison.OrdinalIgnoreCase))
                        newFiles.Add(name);
                    files.Add(name);
                    saved[rank] = ReadTimings(Path.Combine(staged, name));
                    if (saved[rank].Length == 0)
                        throw new InvalidDataException($"The staged Standard {rank} has no hit timings.");
                }
                foreach (string rank in expectedTargets.Keys)
                {
                    var conflicts = NestedTimings.Conflicts(saved, rank, saved[rank]);
                    if (conflicts.Count > 0)
                    {
                        string details = string.Join("; ", conflicts.Select(conflict =>
                            $"{conflict.Difficulty} ({string.Join(", ", conflict.Beats.Take(3))})"));
                        throw new InvalidDataException($"Standard {rank} has timing conflicts with {details}.");
                    }
                }
                files.Add(InfoFilename);
                Commit(song, staged, reports, files, () =>
                {
                    VerifySnapshot(song, original, expectedInfo, expectedTargets);
                    if (newFiles.Any(name => EngineFiles.Exists(Path.Combine(song, name))))
                        throw new IOException("A file now uses a generated difficulty filename.");
                });
            }
            finally
            {
                Directory.Delete(lockPath);
            }
        }

        private static void Commit(
            string song, string staged, string reports, List<string> files, Action verify)
        {
            verify();
            string backup = Path.Combine(reports, "previous");
            string ready = Path.Combine(song, ".bs-downmap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(ready);
            try
            {
                foreach (string name in files)
                {
                    string current = Path.Combine(song, name);
                    if (File.Exists(current)) File.Copy(current, Path.Combine(backup, name));
                    File.Copy(Path.Combine(staged, name), Path.Combine(ready, name));
                }
                verify();
                var committed = new Stack<string>();
                try
                {
                    foreach (string name in files)
                    {
                        EngineFiles.Replace(Path.Combine(ready, name), Path.Combine(song, name));
                        committed.Push(name);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    while (committed.TryPop(out string name))
                    {
                        string saved = Path.Combine(backup, name), destination = Path.Combine(song, name);
                        if (File.Exists(saved))
                        {
                            string restore = Path.Combine(ready, name);
                            File.Copy(saved, restore);
                            EngineFiles.Replace(restore, destination);
                        }
                        else File.Delete(destination);
                    }
                    throw;
                }
            }
            finally
            {
                foreach (string file in Directory.EnumerateFiles(ready)) File.Delete(file);
                Directory.Delete(ready);
            }
        }

        internal static void CheckSnapshot(string song, string expectedInfo, IReadOnlyDictionary<string, string> expected) =>
            VerifySnapshot(song, ReadInfo(song), expectedInfo, expected);

        private static void VerifySnapshot(
            string song, Dictionary<string, string> entries, string expectedInfo, IReadOnlyDictionary<string, string> expected)
        {
            if (!string.Equals(Fingerprint(Path.Combine(song, InfoFilename)), expectedInfo, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Song Info changed. Reopen BS Downmapper before generating.");
            foreach (var item in expected)
            {
                string actual = entries.TryGetValue(item.Key, out string name) && File.Exists(Path.Combine(song, name))
                    ? Fingerprint(Path.Combine(song, name))
                    : "missing";
                if (!string.Equals(actual, item.Value, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Standard {item.Key} changed. Reopen BS Downmapper before generating.");
            }
        }

        private static string Fingerprint(string path)
        {
            using var file = File.OpenRead(path);
            return EngineFiles.Hash(file);
        }

        private static Dictionary<string, string> ReadInfo(string song)
        {
            var info = JsonNode.Parse(File.ReadAllText(Path.Combine(song, InfoFilename)));
            bool modern = info["difficultyBeatmaps"] is JsonArray;
            var entries = modern
                ? ((JsonArray)info["difficultyBeatmaps"]).Where(item => item["characteristic"]?.GetValue<string>() == "Standard")
                : ((JsonArray)info["_difficultyBeatmapSets"])
                    .Where(set => set["_beatmapCharacteristicName"]?.GetValue<string>() == "Standard")
                    .SelectMany(set => (JsonArray)set["_difficultyBeatmaps"]);
            var result = new Dictionary<string, string>();
            foreach (var entry in entries)
            {
                string rank = entry[modern ? "difficulty" : "_difficulty"].GetValue<string>();
                string name = entry[modern ? "beatmapDataFilename" : "_beatmapFilename"].GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                    throw new InvalidDataException($"Standard {rank} has an invalid filename.");
                result[rank == "Expert+" ? "ExpertPlus" : rank] = name;
            }
            return result;
        }

        internal static double[] ReadTimings(string filename)
        {
            var chart = JsonNode.Parse(File.ReadAllText(filename));
            var notes = chart["colorNotes"] is JsonArray modern
                ? modern.Select(item => item["b"]?.GetValue<double>() ?? 0)
                : (chart["_notes"] as JsonArray ?? new JsonArray()).Where(item => item["_type"].GetValue<int>() is 0 or 1)
                    .Select(item => item["_time"].GetValue<double>());
            return NestedTimings.DistinctBeats(notes.Select((beat, id) => new Note(id, beat, 0, 0, 0, 0, 0)));
        }
    }
}
