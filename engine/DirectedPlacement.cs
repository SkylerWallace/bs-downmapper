using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    internal sealed record DirectedPlacementResult(
        IReadOnlyList<Note> Notes, JsonObject DirectionReport, JsonObject DirectionSettings, int Attempts);

    internal static class DirectedPlacement
    {
        private enum Mode { Standard, Horizontal, Warnings, Source }

        private sealed record SearchNote(Note Note, int Representative, bool Fixed, int[] Directions);
        private sealed record Attempt(Mode Mode, (int Id, int Direction)[] Exclusions, double GroupFrontier = double.NegativeInfinity);

        private static readonly int[] Limits = { 16, 8, 6, 10 };

        internal static async Task<DirectedPlacementResult> Place(
            DirectionRuntime runtime, JsonObject input, Func<IReadOnlyList<Note>, PlacementPlan> plan,
            Func<IReadOnlyList<Note>, JsonObject, CancellationToken, Task<IReadOnlyList<Note>>> validate,
            bool repairEmptyDomains, bool boundedRepair, CancellationToken cancellation,
            bool stopAfterParityRepair = false, bool tryHandsEarly = false)
        {
            var notes = ReadSearchNotes(input);
            var byId = notes.ToDictionary(note => note.Note.Id);
            var pending = Enum.GetValues(typeof(Mode)).Cast<Mode>().Reverse()
                .Select(mode => new Attempt(mode, Array.Empty<(int, int)>())).ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var attempts = new int[Limits.Length];
            DirectionPathFailure directionFailure = null;
            PlacementDeadEnd placementFailure = null;
            PlacementDeadEnd auditFailure = null;
            DirectedPlacementResult best = null;
            (double Cost, double Motion) bestRank = (double.PositiveInfinity, double.PositiveInfinity);
            int extraAttempts = 0;
            var repeatedGeometry = new Dictionary<(double Beat, string Reason), int>();

            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                int index = boundedRepair ? Enumerable.Range(0, pending.Count)
                    .OrderBy(i => pending[i].Mode).ThenByDescending(i => pending[i].GroupFrontier)
                    .ThenBy(i => pending[i].Exclusions.Length).First() : pending.Count - 1;
                var attempt = pending[index];
                pending.RemoveAt(index);
                var mode = attempt.Mode;
                if (attempts[(int)mode] >= Limits[(int)mode] ||
                    mode == Mode.Warnings && (!repairEmptyDomains || directionFailure == null && !boundedRepair) ||
                    mode == Mode.Source && (!repairEmptyDomains || auditFailure == null && !boundedRepair))
                    continue;
                string key = string.Join(";", attempt.Exclusions.Select(exclusion => $"{exclusion.Id}:{exclusion.Direction}"));
                if (!seen.Add($"{mode}/{key}"))
                    continue;
                if (best != null && extraAttempts++ >= 3)
                    break;
                attempts[(int)mode]++;

                var excluded = new JsonObject();
                foreach (var group in attempt.Exclusions.GroupBy(item => item.Id))
                    excluded.Add(group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        new JsonArray(group.Select(item => (JsonNode)JsonValue.Create(item.Direction)).ToArray()));
                var settings = (JsonObject)input.DeepClone();
                settings["excludedDirections"] = excluded;
                settings["allowHorizontalRepair"] = mode != Mode.Standard;
                settings["allowProvisionalParityWarning"] = mode is Mode.Warnings or Mode.Source;
                settings["preferSourceArrows"] = mode == Mode.Source;

                JsonObject report;
                try
                {
                    report = await runtime.Plan(settings, cancellation).ConfigureAwait(false);
                }
                catch (DirectionPathFailure failure)
                {
                    if (attempt.Exclusions.Length == 0)
                        directionFailure ??= failure;
                    continue;
                }

                var directed = ReadDirections(report, byId);
                var placementPlan = plan(directed);
                IReadOnlyList<Note> placed;
                bool auditing = false;
                try
                {
                    placed = ChartPlacement.Place(directed, placementPlan, cancellation);
                    auditing = true;
                    placed = await validate(placed, report, cancellation).ConfigureAwait(false);
                }
                catch (PlacementDeadEnd failure) when (!auditing || !stopAfterParityRepair)
                {
                    if (Environment.GetEnvironmentVariable("BS_DOWNMAP_TRACE_DOMAINS") == "1")
                        Console.Error.WriteLine($"{mode}: {failure.Message}");
                    if (auditing)
                        auditFailure = failure;
                    else
                    {
                        placementFailure = failure;
                        var blocked = (failure.Beat, failure.Reason);
                        repeatedGeometry.TryGetValue(blocked, out int repeats);
                        repeatedGeometry[blocked] = repeats + 1;
                        if (best == null && (repeats >= 5 ||
                            tryHandsEarly && repeats >= 1 && failure.Reason == "No joint geometry"))
                            throw;
                    }
                    QueueFailureAlternatives(attempt, failure, directed, placementPlan);
                    if (repairEmptyDomains && !auditing)
                        QueueGroupAlternatives(attempt, failure.Beat, failure.Notes, placementPlan);
                    continue;
                }
                var result = new DirectedPlacementResult(placed, report, settings, attempts.Sum());
                if (!boundedRepair)
                    return result;
                var charged = placed.Where(note => placementPlan.RepairWindows.ContainsKey(note.Id))
                    .Select(note => (Note: note, Cost: RepairCost(note, placementPlan))).ToArray();
                double total = charged.Sum(item => item.Cost);
                double motion = charged.Sum(item =>
                {
                    var seed = byId[item.Note.Id].Note;
                    return (item.Note.Direction == seed.Direction ? 0 : 1) + .25 *
                        new SwingClearance.Point(item.Note.X - seed.X, item.Note.Y - seed.Y).Length;
                });
                if ((total, motion).CompareTo(bestRank) < 0)
                {
                    best = result;
                    bestRank = (total, motion);
                }
                if (total == 0)
                    break;
                var alternatives = charged.Where(item => !byId[item.Note.Id].Fixed && item.Cost > 0)
                    .OrderByDescending(item => item.Cost).Take(3).Select(item => item.Note);
                QueueAlternatives(attempt, alternatives, directed);
            }

            if (best != null)
                return best with { Attempts = attempts.Sum() };
            if (auditFailure != null)
                throw auditFailure;
            if (placementFailure != null)
                throw placementFailure;
            throw directionFailure ?? new DirectionPathFailure("No supported direction path.");

            void QueueFailureAlternatives(Attempt attempt, PlacementDeadEnd failure, IReadOnlyList<Note> directed,
                PlacementPlan placementPlan)
            {
                var repairs = directed.Where(note => placementPlan.RepairWindows.ContainsKey(note.Id))
                    .Select(note => (Note: note, Gap: Math.Abs(placementPlan.Seconds(note.Beat) -
                        placementPlan.Seconds(failure.Beat))))
                    .Where(item => item.Gap <= .75).OrderBy(item => item.Gap).Take(6)
                    .Select(item => item.Note);
                QueueAlternatives(attempt, failure.Notes.Concat(repairs).Reverse(), directed);
            }

            void QueueAlternatives(Attempt attempt, IEnumerable<Note> candidates, IReadOnlyList<Note> directed)
            {
                var selected = directed.ToDictionary(note => note.Id);
                foreach (var affected in candidates)
                {
                    if (!byId.TryGetValue(affected.Id, out var seed) || seed.Fixed)
                        continue;
                    int chosen = (int)selected[affected.Id].Direction;
                    var exclusion = (Id: seed.Representative, Direction: chosen);
                    if (attempt.Exclusions.Contains(exclusion) ||
                        !seed.Directions.Any(direction => direction != chosen &&
                            !attempt.Exclusions.Contains((seed.Representative, direction))))
                        continue;
                    pending.Add(new Attempt(attempt.Mode, attempt.Exclusions.Append(exclusion)
                        .OrderBy(item => item.Id).ThenBy(item => item.Direction).ToArray()));
                }
            }

            void QueueGroupAlternatives(Attempt attempt, double beat, IReadOnlyList<Note> candidates, PlacementPlan placementPlan)
            {
                var hands = candidates.GroupBy(note => note.Hand).Select(group => group.ToArray()).ToArray();
                if (hands.Any(hand => hand.Length > 1))
                {
                    var shared = candidates.Select(note => (int)byId[note.Id].Note.Direction).Distinct().ToArray();
                    if (hands.Length == 2 && shared.Length == 1 && shared[0] is >= 0 and <= 7 &&
                        candidates.Any(note => note.Direction != shared[0]))
                    {
                        var whole = Force(attempt.Exclusions, candidates, shared[0]);
                        if (whole != null)
                            pending.Add(new Attempt(attempt.Mode, whole, beat));
                    }
                    var exclusions = new List<(int Id, int Direction)[]> { attempt.Exclusions };
                    foreach (var hand in hands.Where(hand => hand.Length > 1))
                    {
                        var authored = hand.Select(note => (int)byId[note.Id].Note.Direction).Distinct().ToArray();
                        if (authored.Length != 1 || authored[0] is < 0 or > 7)
                            return;
                        int[] opposites = { 1, 0, 3, 2, 7, 6, 5, 4 };
                        var directions = new[] { authored[0], opposites[authored[0]] };
                        if (placementPlan.Walls.Any(wall => wall.LegacyCrouch &&
                            wall.Beat <= beat && beat < wall.Beat + wall.Duration))
                            directions = directions.Concat(new[] { 0, 1, 2, 3 }).Distinct().ToArray();
                        exclusions = (from previous in exclusions
                                      from direction in directions
                                      let next = Force(previous, hand, direction)
                                      where next != null select next).ToList();
                    }
                    foreach (var choice in exclusions)
                        pending.Add(new Attempt(attempt.Mode, choice, beat));
                }
                else if (candidates.Count == 2)
                {
                    int direction = (int)byId[candidates[0].Id].Note.Direction;
                    if (direction is >= 2 and <= 7 && byId[candidates[1].Id].Note.Direction == direction &&
                        candidates.Any(note => note.Direction != direction))
                    {
                        var choice = Force(attempt.Exclusions, candidates, direction);
                        if (choice != null)
                            pending.Add(new Attempt(attempt.Mode, choice, beat));
                    }
                }

                (int Id, int Direction)[] Force((int Id, int Direction)[] prior,
                    IReadOnlyList<Note> group, int direction)
                {
                    var seeds = group.Select(note => byId[note.Id]).ToArray();
                    if (seeds.Any(seed => seed.Fixed || !seed.Directions.Contains(direction) ||
                        prior.Contains((seed.Representative, direction))))
                        return null;
                    return prior.Concat(seeds.SelectMany(seed => seed.Directions.Where(value => value != direction)
                            .Select(value => (Id: seed.Representative, Direction: value))))
                        .Distinct().OrderBy(item => item.Id).ThenBy(item => item.Direction).ToArray();
                }
            }
        }

        private static double RepairCost(Note note, PlacementPlan plan)
        {
            var domain = plan.Domains[note.Id];
            var context = domain.Context;
            if (domain.LinkedAnchor)
                return 0;
            var reasons = PlacementExclusions.Evaluate(note, context, domain.Repair, domain.FlattenOutward);
            double wallGap = PlacementPolicy.SideWallRecoveryGap(note, context.Walls, context.GapSeconds);
            return PlacementPolicy.RelaxationCost(note, context.Placement, false, true, wallGap,
                domain.RepairCostScale, reasons) ?? throw new InvalidDataException("Placement used an unsupported relaxation.");
        }

        private static SearchNote[] ReadSearchNotes(JsonObject input)
        {
            var representatives = new Dictionary<(double Beat, int Hand), int>();
            var notes = new List<SearchNote>();
            foreach (var row in (JsonArray)input["notes"])
            {
                var note = ReadNote(row);
                bool linked = Flag(row, "linkedAnchor");
                bool fixedDirection = linked || Flag(row, "streamEntry");
                int representative = note.Id;
                if (!linked)
                {
                    var key = (note.Beat, note.Hand);
                    representatives.TryAdd(key, note.Id);
                    representative = representatives[key];
                }
                var directions = (row["geometryDirections"] as JsonArray)?.Select(node => node.GetValue<int>()).ToArray()
                    ?? new[] { (int)note.Direction };
                notes.Add(new SearchNote(note, representative, fixedDirection, directions));
            }
            return notes.ToArray();

            static bool Flag(JsonNode row, string key) => row[key] is JsonValue flag &&
                flag.TryGetValue<bool>(out bool value) && value;
        }

        private static Note[] ReadDirections(JsonObject report, IReadOnlyDictionary<int, SearchNote> source)
        {
            var output = new List<Note>();
            var seen = new HashSet<int>();
            foreach (var row in (JsonArray)report["notes"])
            {
                var note = ReadNote(row);
                if (!seen.Add(note.Id) || !source.TryGetValue(note.Id, out var input) ||
                    note.Beat != input.Note.Beat || note.Hand != input.Note.Hand)
                    throw new InvalidDataException("Direction planning changed the approved beat or hand signature.");
                output.Add(note);
            }
            if (output.Count != source.Count)
                throw new InvalidDataException("Direction planning omitted approved notes.");
            return output.ToArray();
        }

        private static Note ReadNote(JsonNode row) => new(row["id"].GetValue<int>(), row["b"].GetValue<double>(),
            row["c"].GetValue<int>(), row["x"].GetValue<double>(), row["y"].GetValue<double>(),
            row["d"].GetValue<double>(), row["a"]?.GetValue<double>() ?? 0);
    }
}
