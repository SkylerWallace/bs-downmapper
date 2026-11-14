using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    internal readonly record struct ParityReentry(int Hand, double Beat);
    internal readonly record struct ProjectedReset(int Hand, double Start, double End, double Direction);

    internal static class PlacementParity
    {
        internal static async Task<JsonObject> Audit(DirectionRuntime runtime, IReadOnlyList<Note> notes,
            IReadOnlyList<Bomb> bombs, IReadOnlyList<ParityReentry> reentries, double bpm,
            Func<double, double> seconds, bool variableTempo, CancellationToken cancellation,
            IReadOnlyDictionary<(double Beat, int Hand), int[]> domains = null)
        {
            var nativeByProxy = new Dictionary<double, double>();
            var proxyByNative = new Dictionary<double, double>();

            double Project(double native) => variableTempo ? seconds(native) * bpm / 60 : native;

            double NoteBeat(double native)
            {
                double proxy = Project(native);
                if (variableTempo)
                {
                    nativeByProxy[proxy] = native;
                    proxyByNative[native] = proxy;
                }
                return proxy;
            }

            var chart = new JsonObject
            {
                ["version"] = "3.0.0",
                ["colorNotes"] = new JsonArray(notes.Select(note => (JsonNode)new JsonObject
                {
                    ["b"] = NoteBeat(note.Beat), ["x"] = note.X, ["y"] = note.Y,
                    ["c"] = note.Hand, ["d"] = note.Direction, ["a"] = note.Angle,
                }).ToArray()),
                ["bombNotes"] = new JsonArray(bombs.Select(bomb => (JsonNode)new JsonObject
                {
                    ["b"] = Project(bomb.Beat), ["x"] = bomb.X, ["y"] = bomb.Y,
                }).ToArray()),
            };
            var cues = new JsonArray(reentries.Select(cue => (JsonNode)new JsonObject
            {
                ["hand"] = cue.Hand,
                ["beat"] = proxyByNative.GetValueOrDefault(cue.Beat, cue.Beat),
            }).ToArray());
            var repairDomains = domains == null ? null : new JsonArray(domains.Select(pair => (JsonNode)new JsonArray(
                JsonValue.Create(Project(pair.Key.Beat)), JsonValue.Create(pair.Key.Hand),
                pair.Value == null ? null :
                    new JsonArray(pair.Value.Select(direction => (JsonNode)JsonValue.Create(direction)).ToArray()))).ToArray());
            var result = await runtime.Audit(chart, bpm, cues, cancellation, repairDomains).ConfigureAwait(false);
            if (nativeByProxy.Count == 0)
                return result;
            double[] sortedProjections = null;
            foreach (var item in result["issues"].AsArray().Concat(result["trace"].AsArray()).Concat(result["edits"].AsArray()))
            {
                double proxy = item["beat"].GetValue<double>();
                if (!nativeByProxy.TryGetValue(proxy, out double native))
                {
                    sortedProjections ??= nativeByProxy.Keys.OrderBy(beat => beat).ToArray();
                    if (!TryResolveProjection(sortedProjections, proxy, out double projection))
                        throw new InvalidDataException("A parity finding could not be mapped to the native beat grid.");
                    native = nativeByProxy[projection];
                }
                item["beat"] = native;
            }
            result["timeBasis"] = "Elapsed seconds, reported in native beats";
            return result;
        }

        internal static bool TryResolveProjection(double[] sorted, double reported, out double projection)
        {
            projection = default;
            if (!double.IsFinite(reported))
                return false;

            // JSON can round-trip a projected beat a few ULPs apart between
            // QuickJS and Unity. Accept a unique nearby note, never a grid snap.
            int index = Array.BinarySearch(sorted, reported);
            if (index >= 0)
            {
                projection = sorted[index];
                return true;
            }

            int right = ~index;
            double tolerance = Math.Max(1, Math.Abs(reported)) * 4e-15;
            bool hasLeft = right > 0 && reported - sorted[right - 1] <= tolerance;
            bool hasRight = right < sorted.Length && sorted[right] - reported <= tolerance;
            if (hasLeft == hasRight)
                return false;

            projection = sorted[hasLeft ? right - 1 : right];
            return true;
        }

        internal static void Validate(IReadOnlyList<Note> candidate, IReadOnlyList<Bomb> bombs,
            ISet<ProjectedReset> projected, IReadOnlyDictionary<int, double> gaps, ISet<int> sparse,
            double drumGap, double sparseDrumGap, JsonObject directionReport, JsonObject source, JsonObject result, double adherence)
        {
            var inherited = source["issues"].AsArray().Select(issue => (
                Math.Round(Number(issue["beat"]), 4), issue["hand"].GetValue<int>(),
                issue["status"].GetValue<string>())).ToHashSet();
            var planned = (directionReport["drummingRepeats"] as JsonArray ?? new JsonArray())
                .Select(repeat => (repeat["hand"].GetValue<int>(), Number(repeat["fromBeat"]),
                    Number(repeat["beat"]))).ToHashSet();
            var verifiedResets = new Dictionary<(double Beat, int Hand), double>();
            var verifiedRepeats = new HashSet<(double Beat, int Hand)>();

            for (int hand = 0; hand < 2; hand++)
            {
                var attacks = NestedTimings.Group(candidate.Where(note => note.Hand == hand));
                for (int index = 1; index < attacks.Count; index++)
                {
                    var before = attacks[index - 1].Notes;
                    var after = attacks[index].Notes;
                    double start = before[0].Beat, end = after[0].Beat;
                    var key = (Math.Round(end, 4), hand);
                    if (before.Count == 1 && after.Count == 1 && before[0].Direction == after[0].Direction &&
                        projected.Contains(new ProjectedReset(hand, start, end, after[0].Direction)))
                        verifiedResets[key] = after[0].Direction;
                    double minimum = after.All(note => sparse.Contains(note.Id)) ? sparseDrumGap : drumGap;
                    if (planned.Contains((hand, start, end)) && before.Concat(after).All(note => note.Direction == 1) &&
                        after.Min(note => gaps[note.Id]) >= minimum &&
                        !bombs.Any(bomb => start < bomb.Beat && bomb.Beat <= end))
                        verifiedRepeats.Add(key);
                }
            }

            foreach (var issue in result["issues"].AsArray())
            {
                double beat = Math.Round(Number(issue["beat"]), 4);
                int hand = issue["hand"].GetValue<int>();
                string status = issue["status"].GetValue<string>();
                var directions = issue["directions"].AsArray().Select(Number).ToArray();
                bool expected = inherited.Contains((beat, hand, status)) ||
                    status == "warning" && inherited.Contains((beat, hand, "error")) ||
                    adherence < 1 && verifiedRepeats.Contains((beat, hand)) && directions.All(direction => direction == 1) ||
                    directions.Length == 1 && verifiedResets.TryGetValue((beat, hand), out double reset) && directions[0] == reset;
                if (expected || status == "warning")
                    continue;
                var attacks = NestedTimings.Group(candidate.Where(note => note.Hand == hand && note.Beat <= beat + .0001));
                var affected = attacks.TakeLast(3).Reverse().SelectMany(attack => attack.Notes).ToArray();
                throw new PlacementDeadEnd(beat, affected, $"Unexpected parity {status} for {(hand == 0 ? "left" : "right")} hand");
            }

            if (verifiedResets.Count != projected.Count)
                throw new InvalidDataException("Projected source reset changed during placement.");
        }

        private static double Number(JsonNode node) =>
            node is JsonValue value && value.TryGetValue<int>(out int integer) ? integer : node.GetValue<double>();
    }
}
