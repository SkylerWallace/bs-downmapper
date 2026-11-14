using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    internal static class BookmarkFiles
    {
        private const string DirectoryName = "Bookmarks";
        private const string FileSuffix = ".bookmarks.dat";

        internal static double[] Read(string song, string difficulty, JsonObject chart)
        {
            var embedded = new[] { chart, chart["customData"], chart["_customData"] }.OfType<JsonObject>()
                .SelectMany(data => Beats(data["bookmarks"]).Concat(Beats(data["_bookmarks"])));
            var sidecars = Files(song).Select(ReadSidecar).Where(data => data != null &&
                Is(data["characteristic"], "Standard") && Is(data["difficulty"], difficulty))
                .SelectMany(data => Beats(data["bookmarks"]));
            return embedded.Concat(sidecars).Where(beat => double.IsFinite(beat) && beat >= 0)
                .Distinct().OrderBy(beat => beat).ToArray();
        }

        internal static void Copy(string source, string destination)
        {
            foreach (var path in Files(source))
            {
                string folder = Path.Combine(destination, DirectoryName);
                Directory.CreateDirectory(folder);
                File.Copy(path, Path.Combine(folder, Path.GetFileName(path)));
            }
        }

        private static IEnumerable<string> Files(string song)
        {
            if (string.IsNullOrEmpty(song)) return Array.Empty<string>();
            string folder = Path.Combine(song, DirectoryName);
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Where(path => path.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
                : Array.Empty<string>();
        }

        private static JsonObject ReadSidecar(string path)
        {
            try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
#if CHROMAPPER
            catch (Newtonsoft.Json.JsonException) { return null; }
#else
            catch (System.Text.Json.JsonException) { return null; }
#endif
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool Is(JsonNode node, string expected) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) &&
            string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<double> Beats(JsonNode node) =>
            node is JsonArray rows ? rows.OfType<JsonObject>().Select(row =>
                StandardSong.Number(row["beat"] ?? row["b"] ?? row["_time"])) : Enumerable.Empty<double>();
    }
}
