using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    public readonly record struct BeatRegion(double Beat, double Seconds, double SecondsPerBeat);

    public sealed class ChartClock
    {
        private readonly BeatRegion[] regions;
        public IReadOnlyList<BeatRegion> Regions { get; }

        private ChartClock(List<BeatRegion> regions)
        {
            this.regions = regions.ToArray();
            Regions = Array.AsReadOnly(this.regions);
        }

        public static ChartClock FromRegions(IEnumerable<BeatRegion> regions)
        {
            var copy = regions.ToList();
            if (copy.Count == 0)
                throw new InvalidDataException("At least one tempo region is required.");
            for (int i = 0; i < copy.Count; i++)
            {
                var region = copy[i];
                if (!double.IsFinite(region.Beat) || !double.IsFinite(region.Seconds) ||
                    !double.IsFinite(region.SecondsPerBeat) || region.SecondsPerBeat <= 0 ||
                    i > 0 && (region.Beat <= copy[i - 1].Beat || region.Seconds <= copy[i - 1].Seconds))
                    throw new InvalidDataException("Tempo regions must be finite, positive and ordered.");
            }
            return new ChartClock(copy);
        }

        public double SecondsPerBeat(double beat) => Region(beat, false).SecondsPerBeat;

        public static ChartClock Read(double bpm, JsonObject chart, JsonObject audioData = null)
        {
            if (!double.IsFinite(bpm) || bpm <= 0)
                throw new InvalidDataException("Positive BPM required.");
            if (audioData != null)
                return ReadAudio(audioData);
            return WithEvents(bpm, Rows(chart, "bpmEvents").Select(row => (Number(row, "b"), Number(row, "m"))));
        }

        public static ChartClock ReadLegacyGrid(double bpm, JsonObject chart)
        {
            var changes = new Dictionary<double, double>();
            foreach (var container in new[] { chart, chart["_customData"] as JsonObject })
            foreach (string key in new[] { "_BPMChanges", "_bpmChanges" })
            foreach (var entry in Rows(container, key))
            {
                double beat = Number(entry, "_time"), tempo = Number(entry, "_BPM");
                if (beat < 0 || tempo <= 0)
                    throw new InvalidDataException("Invalid legacy BPM change.");
                changes.TryAdd(beat, tempo);
            }
            if (changes.Count == 0)
                return null;
            var events = new List<(double Beat, double Tempo)>();
            double grid = 0, previous = 0, previousTempo = bpm;
            foreach (var (beat, tempo) in changes.OrderBy(entry => entry.Key))
            {
                grid += (beat - previous) * previousTempo / bpm;
                events.Add((grid, tempo));
                previous = beat;
                previousTempo = tempo;
            }
            return WithEvents(bpm, events);
        }

        private static ChartClock WithEvents(double bpm, IEnumerable<(double Beat, double Tempo)> events)
        {
            var regions = new List<BeatRegion> { new(0, 0, 60 / bpm) };
            foreach (var (beat, tempo) in events.OrderBy(entry => entry.Beat))
            {
                if (beat < 0 || tempo <= 0 || !double.IsFinite(beat) || !double.IsFinite(tempo))
                    throw new InvalidDataException("Invalid tempo event.");
                var last = regions[^1];
                if (beat == last.Beat)
                    regions[^1] = last with { SecondsPerBeat = 60 / tempo };
                else
                    regions.Add(new BeatRegion(beat, last.Seconds + (beat - last.Beat) * last.SecondsPerBeat, 60 / tempo));
            }
            return new ChartClock(regions);
        }

        private static ChartClock ReadAudio(JsonObject audio)
        {
            var input = Rows(audio, "bpmData").ToArray();
            double frequency = Number(audio, "songFrequency");
            if (frequency <= 0 || input.Length == 0)
                throw new InvalidDataException("AudioData needs positive sample rate and tempo regions.");
            var regions = new List<BeatRegion>();
            for (int i = 0; i < input.Length; i++)
            {
                var row = input[i];
                double start = Number(row, "sb", 0), end = Number(row, "eb");
                double first = Number(row, "si", 0), last = Number(row, "ei");
                if (i > 0)
                {
                    var previous = regions[^1];
                    double previousEnd = Number(input[i - 1], "eb");
                    double previousLast = Number(input[i - 1], "ei");
                    bool contiguous = Math.Abs(start - previousEnd) <= .0001 &&
                        Math.Abs(first - previousLast) <= Math.Max(2, frequency * .01);
                    if (i == input.Length - 1 && contiguous && end < start && last < first &&
                        audio["songSampleCount"] != null && last == Number(audio, "songSampleCount") &&
                        end >= regions[0].Beat && end <= previousEnd && last / frequency >= regions[0].Seconds &&
                        last <= previousLast)
                        break;
                    if (!contiguous || start <= previous.Beat || first / frequency <= previous.Seconds)
                        throw new InvalidDataException("AudioData tempo regions must be positive and contiguous.");
                    regions[^1] = previous with { SecondsPerBeat = (first / frequency - previous.Seconds) / (start - previous.Beat) };
                }
                if (end <= start || last <= first)
                    throw new InvalidDataException("AudioData tempo regions must be positive and contiguous.");
                regions.Add(new BeatRegion(start, first / frequency, (last - first) / frequency / (end - start)));
            }
            return new ChartClock(regions);
        }

        public double Seconds(double beat)
        {
            var region = Region(beat, false);
            return region.Seconds + (beat - region.Beat) * region.SecondsPerBeat;
        }

        public double Beat(double seconds)
        {
            var region = Region(seconds, true);
            return region.Beat + (seconds - region.Seconds) / region.SecondsPerBeat;
        }

        public double Gap(double first, double second) => regions.Length == 1 ?
            (second - first) * regions[0].SecondsPerBeat : Seconds(second) - Seconds(first);

        private BeatRegion Region(double value, bool seconds)
        {
            int low = 0, high = regions.Length - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if ((seconds ? regions[middle].Seconds : regions[middle].Beat) <= value)
                    low = middle;
                else
                    high = middle - 1;
            }
            return regions[low];
        }

        private static IEnumerable<JsonObject> Rows(JsonObject container, string key) =>
            container?[key] is JsonArray list ? list.Cast<JsonObject>() : Enumerable.Empty<JsonObject>();

        private static double Number(JsonObject row, string key, double? fallback = null)
        {
            if (row[key] is JsonValue value)
            {
                if (value.TryGetValue<double>(out double number) && double.IsFinite(number)) return number;
                if (value.TryGetValue<int>(out int integer)) return integer;
                if (value.TryGetValue<long>(out long wide)) return wide;
            }
            if (row[key] == null && fallback.HasValue) return fallback.Value;
            throw new InvalidDataException($"Invalid tempo field {key}.");
        }
    }
}
