using System;
using System.IO;
using System.Text.Json.Nodes;
using BsDownmap.Engine;

internal static class ClockChecks
{
    private static void Equal(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 1e-10)
            throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid tempo data to be rejected.");
    }

    private static JsonObject Json(string text) => JsonNode.Parse(text).AsObject();

    private static void Main()
    {
        var events = ChartClock.Read(120, Json("""
            {"bpmEvents":[{"b":4,"m":60},{"b":4,"m":120},{"b":8,"m":60}]}
            """));
        var regions = new[] { new BeatRegion(0, 0, .5), new BeatRegion(4, 2, .5), new BeatRegion(8, 4, 1) };
        var preview = ChartClock.FromRegions(regions);
        regions[0] = new BeatRegion(0, 0, 100); // Preview owns a snapshot, not caller storage.
        foreach (double beat in new[] { -2.0, 0, 3.99, 4, 7.99, 8, 9, 20 })
        {
            Equal(events.Seconds(beat), preview.Seconds(beat));
            Equal(beat, preview.Beat(preview.Seconds(beat)));
            Equal(events.SecondsPerBeat(beat), preview.SecondsPerBeat(beat));
        }
        Equal(1.5, preview.Gap(7, 9));
        Reject(() => ChartClock.FromRegions(Array.Empty<BeatRegion>()));
        Reject(() => ChartClock.FromRegions(new[] { new BeatRegion(0, 0, 0) }));
        Reject(() => ChartClock.FromRegions(new[] { new BeatRegion(double.NaN, 0, 1) }));
        Reject(() => ChartClock.FromRegions(new[] { new BeatRegion(0, double.PositiveInfinity, 1) }));
        Reject(() => ChartClock.FromRegions(new[] { new BeatRegion(0, 0, 1), new BeatRegion(0, 2, 1) }));
        Reject(() => ChartClock.FromRegions(new[] { new BeatRegion(0, 0, 1), new BeatRegion(2, 0, 1) }));
        Reject(() => ChartClock.Read(120, Json("{\"bpmEvents\":[{\"b\":4,\"m\":0}]}")));

        var audio = Json("""
            {"songFrequency":1000,"songSampleCount":3500,"bpmData":[
                {"sb":0,"eb":4,"si":0,"ei":2000},
                {"sb":4,"eb":8,"si":2008,"ei":4008},
                {"sb":8,"eb":7,"si":4009,"ei":3500}]}
            """);
        var clock = ChartClock.Read(120, null, audio);
        Equal(2, clock.Regions.Count);
        Equal(2.008, clock.Seconds(4));
        Equal(1.002, clock.Gap(3, 5));
        audio["bpmData"][2]["ei"] = 3400;
        Reject(() => ChartClock.Read(120, null, audio));
        audio["bpmData"].AsArray().RemoveAt(2);
        audio["bpmData"][1]["si"] = 2020;
        Reject(() => ChartClock.Read(120, null, audio));
        Console.WriteLine("Passed clock boundaries, round trips, snapshot isolation, invalid regions and audio trim checks.");
    }
}
