using System;
using System.IO;
using System.Linq;
using SimpleJSON;
using BsDownmap.Engine;
using EngineJson = System.Text.Json.Nodes.JsonNode;

namespace BsDownmap.ChroMapper
{
    internal static class StandardChart
    {
        internal static JSONNode Entry(JSONNode info, string difficulty)
        {
            bool modern = info.HasKey("difficultyBeatmaps");
            var entries = modern
                ? info["difficultyBeatmaps"].Children.Where(chart => chart["characteristic"].Value == "Standard")
                : info["_difficultyBeatmapSets"].Children
                    .Where(set => set["_beatmapCharacteristicName"].Value == "Standard")
                    .SelectMany(set => set["_difficultyBeatmaps"].Children);
            return entries.LastOrDefault(chart =>
            {
                var rank = chart[modern ? "difficulty" : "_difficulty"].Value;
                return (rank == "Expert+" ? "ExpertPlus" : rank) == difficulty;
            });
        }

        internal static double Bpm(JSONNode info) =>
            (info.HasKey("audio") ? info["audio"]["bpm"] : info["_beatsPerMinute"]).AsDouble;

        internal static string ReadAssetText(string folder, string name)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name))
                throw new FormatException("Package filenames must be flat local filenames.");
            return File.ReadAllText(Path.Combine(folder, name));
        }
    }

    internal sealed class BeatClock
    {
        private readonly ChartClock clock;

        internal BeatClock(TempoRegion[] regions) => clock = ChartClock.FromRegions(
            regions.Select(region => new BeatRegion(region.beat, region.seconds, region.secondsPerBeat)));

        internal static TempoRegion[] ReadRegions(string folder, string difficulty)
        {
            var info = JSON.Parse(StandardChart.ReadAssetText(folder, "Info.dat"));
            double bpm = StandardChart.Bpm(info);
            ChartClock clock;
            try
            {
                if (info.HasKey("audio"))
                {
                    var audio = StandardChart.ReadAssetText(folder, info["audio"]["audioDataFilename"].Value);
                    clock = ChartClock.Read(bpm, null, EngineJson.Parse(audio).AsObject());
                }
                else
                {
                    var entry = StandardChart.Entry(info, difficulty);
                    if (entry == null) throw new FormatException($"Missing Standard {difficulty}.");
                    string filename = (entry.HasKey("beatmapDataFilename")
                        ? entry["beatmapDataFilename"] : entry["_beatmapFilename"]).Value;
                    var chart = StandardChart.ReadAssetText(folder, filename);
                    clock = ChartClock.Read(bpm, EngineJson.Parse(chart).AsObject());
                }
            }
            catch (InvalidDataException error)
            {
                throw new FormatException(error.Message, error);
            }
            return clock.Regions.Select(region => new TempoRegion
            {
                beat = region.Beat, seconds = region.Seconds, secondsPerBeat = region.SecondsPerBeat
            }).ToArray();
        }

        internal double SecondsPerBeat(double beat) => clock.SecondsPerBeat(beat);
        internal double Seconds(double beat) => clock.Seconds(beat);
        internal double Beat(double seconds) => clock.Beat(seconds);
    }
}
