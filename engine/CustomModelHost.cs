using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    public static class CustomModelHost
    {
        public static async Task<JsonNode> Invoke(
            string package, JsonObject manifest, JsonObject context, JsonObject overrides,
            string pythonExecutable, string pythonAdapter, CancellationToken cancellation)
        {
            string folder = Path.GetFullPath(package);
            string id = manifest["id"].GetValue<string>();
            string kind = manifest["kind"].GetValue<string>();
            int version = manifest["apiVersion"].GetValue<int>();
            string entry = Path.GetFullPath(Path.Combine(folder, manifest["entry"].GetValue<string>()));
            string relative = Path.GetRelativePath(folder, entry);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                || relative == "." || !File.Exists(entry))
                throw new InvalidDataException($"{id} must declare an entry inside its model package.");
            if (kind is not ("timing" or "pattern"))
                throw new InvalidDataException($"Unsupported custom model kind {kind}.");

            var start = new ProcessStartInfo
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
#if !CHROMAPPER
            if (version == 1 && Path.GetExtension(entry).Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(pythonExecutable) || string.IsNullOrEmpty(pythonAdapter))
                    throw new InvalidOperationException($"{id} requires the Python model compatibility runtime.");
                start.FileName = pythonExecutable;
                ProcessRuntime.AddArguments(start, "-B", pythonAdapter, entry, kind);
                start.Environment["PYTHONUTF8"] = "1";
            }
            else
#endif
            if (version == 2 && Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = entry;
            }
            else
            {
                throw new InvalidDataException($"Unsupported model entry for {id}.");
            }

            var payload = new JsonObject
            {
                ["kind"] = kind, ["context"] = context.DeepClone(), ["settings"] = Settings(manifest, overrides),
            };
            using var child = Process.Start(start) ?? throw new IOException($"Could not start model {id}.");
            using var registration = cancellation.Register(() =>
            {
                try { if (!child.HasExited) ProcessRuntime.Stop(child); }
                catch (InvalidOperationException) { }
            });
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                await child.StandardInput.WriteAsync(payload.ToJsonString()).ConfigureAwait(false);
            }
            finally
            {
                child.StandardInput.Close();
            }
            await ProcessRuntime.Wait(child, cancellation).ConfigureAwait(false);
            if (child.ExitCode != 0)
                throw new InvalidDataException($"{id}: {(await errors.ConfigureAwait(false)).Trim()}");
            return JsonNode.Parse(await output.ConfigureAwait(false)) ??
                throw new InvalidDataException($"{id} returned no result.");
        }

        public static IReadOnlyList<Note> ApplyTimingResult(
            JsonNode result, IReadOnlyList<Note> eligible, IEnumerable<int> protectedIds, IEnumerable<double> requiredBeats)
        {
            if (result is not JsonArray ids || ids.Count == 0)
                throw new InvalidDataException("A timing model must return a nonempty list of candidate note IDs.");
            var selected = new HashSet<int>();
            foreach (var item in ids)
                if (item is not JsonValue value || !value.TryGetValue<int>(out int id) || !selected.Add(id))
                    throw new InvalidDataException("A timing model returned an invalid or duplicate note ID.");
            return NestedTimings.CompleteModelSelection(eligible, selected, protectedIds, requiredBeats);
        }

        public static IReadOnlyList<Note> ApplyPatternResult(
            JsonNode result, IReadOnlyList<Note> seeds, ISet<int> protectedIds)
        {
            if (result is not JsonObject proposals)
                throw new InvalidDataException("A pattern model must return a dictionary of seed overrides.");
            var byId = seeds.ToDictionary(note => note.Id);
            foreach (var proposal in proposals)
            {
                if (!int.TryParse(proposal.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                    !byId.TryGetValue(id, out var note) || proposal.Value is not JsonObject changes)
                    throw new InvalidDataException($"Invalid pattern seed for note {proposal.Key}.");
                foreach (var change in changes)
                {
                    if (change.Value is not JsonValue value || !value.TryGetValue<int>(out int position))
                        throw new InvalidDataException($"Invalid pattern seed field {change.Key}.");
                    note = change.Key switch
                    {
                        "x" when position is >= 0 and <= 3 => note with { X = position },
                        "y" when position is >= 0 and <= 2 => note with { Y = position },
                        "d" when position is >= 0 and <= 8 => note with { Direction = position },
                        "a" when position is >= -180 and <= 180 => note with { Angle = position },
                        _ => throw new InvalidDataException($"Invalid pattern seed field {change.Key}."),
                    };
                }
                if (!protectedIds.Contains(id)) byId[id] = note;
            }
            return seeds.Select(seed => byId[seed.Id]).ToArray();
        }

        private static JsonObject Settings(JsonObject manifest, JsonObject overrides)
        {
            var definitions = manifest["settings"] as JsonObject ?? new JsonObject();
            if (overrides.Any(item => !definitions.ContainsKey(item.Key)))
                throw new InvalidDataException("Unknown model settings.");
            var result = new JsonObject();
            foreach (var item in definitions)
            {
                var definition = item.Value;
                var value = overrides.ContainsKey(item.Key) ? overrides[item.Key] : definition["default"];
                bool valid = value is JsonValue scalar && definition["type"].GetValue<string>() switch
                {
                    "boolean" => scalar.TryGetValue<bool>(out _),
                    "number" => scalar.TryGetValue<double>(out double number) && double.IsFinite(number) &&
                        number >= definition["min"].GetValue<double>() && number <= definition["max"].GetValue<double>(),
                    "choice" => scalar.TryGetValue<string>(out string choice) &&
                        ((JsonArray)definition["options"]).Any(option => option.GetValue<string>() == choice),
                    _ => false,
                };
                if (!valid) throw new InvalidDataException($"Invalid value for model setting {item.Key}.");
                result[item.Key] = value.DeepClone();
            }
            return result;
        }
    }
}
