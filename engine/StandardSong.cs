using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    public sealed record StandardLevel(
        string Folder, string Difficulty, string Filename, string AudioPath, JsonObject Info, JsonObject Metadata,
        JsonObject Chart, JsonObject AudioData, ChartGameplay Gameplay, ChartClock Clock, ChartClock LegacyGrid,
        double Bpm, double SongOffset)
    {
        public double ToGrid(double beat) => LegacyGrid == null ? beat : LegacyGrid.Beat(Clock.Seconds(beat));
        public double FromGrid(double beat) => LegacyGrid == null ? beat : Clock.Beat(LegacyGrid.Seconds(beat));
        public double GridSeconds(double beat) => (LegacyGrid ?? Clock).Seconds(beat);

        public double EndBeat
        {
            get
            {
                double end = Gameplay.Notes.Max(note => note.Beat);
                if (AudioData?["bpmData"] is JsonArray regions && regions.Count > 0)
                    end = Math.Max(end, StandardSong.Number(regions[^1]["eb"]));
                return Math.Max(1, Math.Ceiling(end));
            }
        }

        public IReadOnlyList<double> Bookmarks()
        {
            return BookmarkFiles.Read(Folder, Difficulty, Chart);
        }
    }

    public sealed class StandardSong
    {
        private readonly string folder;
        private readonly JsonObject info;
        private readonly JsonObject audioData;
        private readonly string audioPath;
        private readonly double bpm;
        private readonly double offset;
        private readonly Dictionary<string, JsonObject> charts = new(StringComparer.Ordinal);
        private static readonly string[] Ranked = { "ExpertPlus", "Expert", "Hard", "Normal", "Easy" };

        public StandardSong(string folder)
        {
            this.folder = Path.GetFullPath(folder);
            info = ReadObject(LocalPath(this.folder, "Info.dat"));
            if (info["audio"] is JsonObject audio)
            {
                bpm = Number(audio["bpm"]);
                audioData = ReadObject(LocalPath(this.folder, audio["audioDataFilename"]?.GetValue<string>()));
                audioPath = LocalPath(this.folder, audio["songFilename"]?.GetValue<string>());
            }
            else
            {
                bpm = Number(info["_beatsPerMinute"]);
                audioPath = LocalPath(this.folder, info["_songFilename"]?.GetValue<string>());
            }
            offset = Number(info["_songTimeOffset"]);
            if (!double.IsFinite(offset))
                offset = 0;
            if (!double.IsFinite(bpm) || bpm <= 0)
                throw new InvalidDataException("Positive BPM required.");

            var entries = info["difficultyBeatmaps"] is JsonArray modern
                ? modern.OfType<JsonObject>().Where(row => row["characteristic"]?.GetValue<string>() == "Standard")
                : Rows(info, "_difficultyBeatmapSets").Where(row => row["_beatmapCharacteristicName"]?.GetValue<string>() == "Standard")
                    .SelectMany(set => Rows(set, "_difficultyBeatmaps"));
            foreach (var entry in entries)
            {
                string difficulty = (entry["difficulty"] ?? entry["_difficulty"])?.GetValue<string>();
                if (difficulty == "Expert+") difficulty = "ExpertPlus";
                if (Ranked.Contains(difficulty))
                    charts[difficulty] = entry;
            }
        }

        public IReadOnlyList<string> Difficulties => Ranked.Where(charts.ContainsKey).ToArray();

        public IReadOnlyDictionary<string, double[]> SavedTimings()
        {
            var saved = new Dictionary<string, double[]>();
            foreach (string rank in Difficulties)
            {
                var entry = charts[rank];
                string filename = (entry["beatmapDataFilename"] ?? entry["_beatmapFilename"])?.GetValue<string>();
                string path = LocalPath(folder, filename);
                if (!File.Exists(path))
                    continue;
                var beats = SpreadPublisher.ReadTimings(path);
                if (beats.Length > 0)
                    saved.Add(rank, beats);
            }
            return saved;
        }

        public StandardLevel Read(string difficulty)
        {
            if (!charts.TryGetValue(difficulty, out var meta))
                throw new InvalidDataException($"Missing Standard {difficulty} chart.");
            if (meta["_customData"]?["_requirements"] is JsonArray legacy && legacy.Count != 0 ||
                meta["customData"]?["requirements"] is JsonArray modern && modern.Count != 0)
                throw new InvalidDataException("Required gameplay mods are unsupported.");
            string filename = (meta["beatmapDataFilename"] ?? meta["_beatmapFilename"])?.GetValue<string>();
            var chart = ReadObject(LocalPath(folder, filename));
            var gameplay = ChartGameplay.Read(chart);
            var clock = ChartClock.Read(bpm, chart, audioData);
            var version = (chart["version"] ?? chart["_version"])?.GetValue<string>();
            var legacyGrid = version?.StartsWith("2.", StringComparison.Ordinal) == true ? ChartClock.ReadLegacyGrid(bpm, chart) : null;
            return new StandardLevel(folder, difficulty, filename, audioPath, (JsonObject)info.DeepClone(),
                (JsonObject)meta.DeepClone(), chart, (JsonObject)audioData?.DeepClone(), gameplay, clock, legacyGrid, bpm, offset);
        }

        private static string LocalPath(string folder, string filename)
        {
            if (string.IsNullOrWhiteSpace(filename) || filename != Path.GetFileName(filename) ||
                filename is "." or ".." || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Package filenames must be flat local filenames.");
            return Path.Combine(folder, filename);
        }

        private static JsonObject ReadObject(string path) =>
            JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"Expected a JSON object in {path}.");

        private static IEnumerable<JsonObject> Rows(JsonObject container, string key) =>
            container[key] is JsonArray rows ? rows.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

        internal static double Number(JsonNode node) => node is JsonValue value && value.TryGetValue<double>(out double number) ?
            number : node is JsonValue integer && integer.TryGetValue<int>(out int whole) ? whole : double.NaN;
    }
}
