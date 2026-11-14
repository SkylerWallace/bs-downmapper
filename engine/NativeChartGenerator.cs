using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    internal sealed record PlacementDraft(IReadOnlyList<Note> Notes, ISet<ChartLinkId> Released, JsonObject Audit);

    internal static class NativeChartGenerator
    {
        internal static async Task<(IReadOnlyList<Note> Notes, ISet<ChartLinkId> Released)> Generate(PreparedTiming rhythm, PreparedPattern pattern,
            IReadOnlyList<Note> seeds, JsonObject profile, double jumpSpeed, double approach, DirectionRuntime runtime,
            CancellationToken cancellation, Action checking = null, Action<PlacementDraft> draft = null,
            StandardLevel foundation = null, double defaultAdherence = 1, ISet<int> fixedOutsideWindow = null)
        {
            var level = rhythm.Level;
            var clock = level.Clock;
            var bombs = level.Gameplay.Bombs;
            var walls = level.Gameplay.Walls;
            var source = seeds.ToDictionary(note => note.Id);
            var anchors = pattern.Anchors.ToHashSet();
            var released = pattern.ReleasedLinks.ToHashSet();
            var chartLinks = level.Gameplay.ReadLinks();
            var endpoints = pattern.Endpoints;
            var gestures = MappingIntent.Detect(seeds, bombs, clock, anchors);
            var (sections, streams) = PatternFoundation.Sections(seeds,
                (int)Number(profile, "densityWindowBeats"), (int)Number(profile, "regularStreamMinimumNotes"),
                Number(profile, "regularStreamMaximumGapBeats"), clock.Gap);
            var entries = streams.Where(stream => stream.SourceDownDownEntry)
                .SelectMany(stream => stream.NoteIds.Take(2)).ToHashSet();
            var cues = new Dictionary<int, PlacementNoteCue>();
            var arrows = new Dictionary<int, int[]>();
            var targetDirections = new Dictionary<int, int>();
            var flexiblePairs = new HashSet<int>();
            foreach (var pair in gestures.Pairs)
            {
                int direction = pair.First.Direction is 1 or 6 or 7 ? 1 : 0;
                double x = pair.First.X, y = Math.Min(pair.First.Y, 1);
                double limit = Math.Max(MotionVisibility.Reach(pair.First), MotionVisibility.Reach(pair.Second));
                bool flexible = Flag(profile, "flexiblePairedPhase");
                foreach (var note in new[] { pair.First, pair.Second })
                {
                    var intent = new PlacementIntent("paired_hand_call_response", x, y);
                    cues[note.Id] = new PlacementNoteCue(new PlacementCue(intent, pair.Pair), ReachLimit: limit);
                    arrows[note.Id] = flexible ? new[] { 0, 1 } : new[] { direction };
                    targetDirections[note.Id] = direction;
                    if (flexible)
                        flexiblePairs.Add(note.Id);
                    direction = 1 - direction;
                }
            }
            foreach (var reset in gestures.Returns)
                foreach (var note in new[] { reset.First, reset.Second })
                {
                    cues[note.Id] = new PlacementNoteCue(new PlacementCue(
                        new PlacementIntent("bomb_return_reset", note.X, note.Y)), PreservePosition: true);
                    arrows[note.Id] = new[] { (int)note.Direction };
                    targetDirections[note.Id] = (int)note.Direction;
                }
            foreach (var exit in gestures.Exits)
            {
                var note = exit.Note;
                cues[note.Id] = new PlacementNoteCue(new PlacementCue(new PlacementIntent(
                    "reset_exit", exit.X ?? note.X, exit.Y ?? note.Y, exit.X.HasValue ? 2 : null)));
                arrows[note.Id] = new[] { exit.Direction };
                targetDirections[note.Id] = exit.Direction;
            }
            if (fixedOutsideWindow != null)
                foreach (var note in seeds.Where(note => fixedOutsideWindow.Contains(note.Id)))
                {
                    cues[note.Id] = new PlacementNoteCue(new PlacementCue(
                        new PlacementIntent("fixed_repair_boundary", note.X, note.Y)), PreservePosition: true);
                    arrows[note.Id] = new[] { (int)note.Direction };
                }
            var parallel = new Dictionary<int, int>();
            if (Flag(profile, "comfortableDoubles"))
                foreach (var attack in NestedTimings.Group(seeds))
                {
                    var pair = attack.Notes;
                    if (pair.Count != 2 || pair[0].Hand == pair[1].Hand ||
                        pair.Any(note => anchors.Contains(note.Id) || cues.ContainsKey(note.Id) ||
                            note.Angle != 0 || note.Direction == 8))
                        continue;
                    var horizontal = pair.Where(note => note.Direction is 2 or 3).ToArray();
                    if (horizontal.Length == 0)
                        continue;
                    int direction = (int)horizontal[0].Direction;
                    int horizontalAxis = direction == 2 ? -1 : 1;
                    if (pair.All(note => note.Direction is 2 or 4 or 6 ? horizontalAxis == -1 :
                        note.Direction is 3 or 5 or 7 && horizontalAxis == 1))
                        foreach (var note in pair)
                            parallel[note.Id] = direction;
                }

            var repairs = new List<double>();
            var clearance = new HashSet<int>();
            var relocated = new HashSet<int>();
            var stackAxes = new HashSet<int>();
            var parityPositions = new Dictionary<int, Note>();
            IReadOnlyList<Note> lastAudited = null;
            var refinedParityWindows = new HashSet<double>();
            var adherence = Number(profile, "parityAdherence", Number(profile["drumming"], "parityAdherence", 1));
            var authored = foundation ?? level;
            var original = await PlacementParity.Audit(runtime, authored.Gameplay.Notes, authored.Gameplay.Bombs,
                Array.Empty<ParityReentry>(), level.Bpm, clock.Seconds, VariableTempo(), cancellation).ConfigureAwait(false);
            var drumming = (JsonObject)(profile["drumming"]?.DeepClone() ?? new JsonObject());
            drumming["parityAdherence"] = adherence;
            bool localRepair = adherence >= .75 && draft != null;
            int feedbackPasses = localRepair ? 0 : (int)Math.Round(4 * adherence);
            var inheritedErrors = Errors(original);
            var plannerParity = (JsonArray)original["issues"].DeepClone();
            if (draft != null || fixedOutsideWindow != null)
            {
                var seedAudit = await PlacementParity.Audit(runtime, seeds, bombs, Array.Empty<ParityReentry>(),
                    level.Bpm, clock.Seconds, VariableTempo(), cancellation).ConfigureAwait(false);
                draft?.Invoke(new PlacementDraft(seeds, released.ToHashSet(), seedAudit));
                if (fixedOutsideWindow != null)
                    foreach (var issue in seedAudit["issues"].AsArray())
                    {
                        double beat = StandardSong.Number(issue["beat"]);
                        int hand = issue["hand"].GetValue<int>();
                        if (seeds.Any(note => note.Beat == beat && note.Hand == hand && fixedOutsideWindow.Contains(note.Id)))
                            plannerParity.Add(issue.DeepClone());
                    }
            }
            for (int attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                var plan = PlacementPreparation.Build(seeds, source, walls, bombs, anchors, endpoints,
                    cues, sections, clock, level.Bpm, jumpSpeed, approach, profile, repairs);
                IReadOnlyDictionary<int, DirectionDomain> currentDomains = null;
                try
                {
                    var domains = DirectionDomains.Build(seeds, plan, arrows, clearance, attempt > 0);
                    currentDomains = domains;
                    var updated = plan with
                    {
                        Domains = plan.Domains.ToDictionary(pair => pair.Key, pair => pair.Value with { Repair = domains[pair.Key].Repair }),
                    };
                    var input = new JsonObject
                    {
                        ["bpm"] = level.Bpm,
                        ["notes"] = new JsonArray(seeds.Select(note => (JsonNode)DirectionNote(note, updated, domains[note.Id])).ToArray()),
                        ["bombs"] = new JsonArray(bombs.Select(bomb => (JsonNode)new JsonObject
                        {
                            ["b"] = bomb.Beat, ["x"] = bomb.X, ["y"] = bomb.Y,
                        }).ToArray()),
                        ["controls"] = DirectionControls(plan),
                        ["drumming"] = drumming.DeepClone(),
                        ["authoredParity"] = plannerParity.DeepClone(),
                        ["beginnerDirections"] = profile["beginnerDirections"]?.DeepClone() ?? new JsonObject(),
                        ["transitionPreferences"] = profile["transitionPreferences"]?.DeepClone() ?? new JsonObject(),
                    };
                    var result = await DirectedPlacement.Place(runtime, input, _ => updated, Validate,
                        attempt > 0, repairs.Count > 0, cancellation, localRepair).ConfigureAwait(false);
                    return (result.Notes, released);

                    async Task<IReadOnlyList<Note>> Validate(IReadOnlyList<Note> candidate, JsonObject report, CancellationToken token)
                    {
                        checking?.Invoke();
                        var audit = await PlacementParity.Audit(runtime, candidate, bombs, Array.Empty<ParityReentry>(),
                            level.Bpm, clock.Seconds, VariableTempo(), token).ConfigureAwait(false);
                        int outstanding = NewErrors(audit);
                        if (localRepair && outstanding > 0)
                        {
                            var domainsBySwing = candidate.GroupBy(note => (note.Beat, note.Hand))
                                .ToDictionary(group => group.Key,
                                    group => inheritedErrors.Contains((Math.Round(group.Key.Beat, 4), group.Key.Hand)) ? null :
                                    group.Any(note => anchors.Contains(note.Id) || entries.Contains(note.Id) || cues.ContainsKey(note.Id)) ? Array.Empty<int>() :
                                    group.Select(note => domains[note.Id].Directions.AsEnumerable())
                                        .Aggregate((a, b) => a.Intersect(b)).ToArray());
                            var proposal = await PlacementParity.Audit(runtime, candidate, bombs, Array.Empty<ParityReentry>(),
                                level.Bpm, clock.Seconds, VariableTempo(), token, domainsBySwing).ConfigureAwait(false);
                            var changes = proposal["edits"].AsArray().ToDictionary(item =>
                                (StandardSong.Number(item["beat"]), item["hand"].GetValue<int>()),
                                item => item["after"][0].GetValue<int>());
                            foreach (var batch in RepairBatches(changes))
                            {
                                var edited = candidate.Select(note => batch.TryGetValue((note.Beat, note.Hand), out int direction) &&
                                    direction != note.Direction ? note with { Direction = direction, Angle = 0 } : note).ToArray();
                                IReadOnlyList<Note> revised = null;
                                var fixedPlan = updated with
                                {
                                    Domains = edited.ToDictionary(note => note.Id, note => updated.Domains[note.Id] with
                                    {
                                        AllowedCells = new HashSet<(double X, double Y)> { (note.X, note.Y) },
                                    }),
                                };
                                try { revised = ChartPlacement.Place(edited, fixedPlan, token); }
                                catch (PlacementDeadEnd) { /* Keep the original placement if the proposed arrows cannot be placed. */ }
                                if (revised != null)
                                {
                                    var revisedAudit = await PlacementParity.Audit(runtime, revised, bombs, Array.Empty<ParityReentry>(),
                                        level.Bpm, clock.Seconds, VariableTempo(), token).ConfigureAwait(false);
                                    var oldErrors = Errors(audit);
                                    var newErrors = Errors(revisedAudit);
                                    int remaining = NewErrors(revisedAudit);
                                    if (remaining < outstanding && newErrors.IsSubsetOf(oldErrors) &&
                                        oldErrors.Where(inheritedErrors.Contains).All(newErrors.Contains))
                                    {
                                        candidate = revised;
                                        audit = revisedAudit;
                                        outstanding = remaining;
                                    }
                                }
                            }
                        }
                        lastAudited = candidate;
                        var placed = candidate.ToDictionary(note => note.Id);
                        foreach (var reset in gestures.Returns)
                        {
                            var a = placed[reset.First.Id];
                            var b = placed[reset.Second.Id];
                            if (a.Direction != reset.First.Direction || b.Direction != reset.Second.Direction ||
                                a.Angle != b.Angle || (a.X, a.Y) == (b.X, b.Y) ||
                                reset.Cues.Any(i => new SwingClearance.Point(bombs[i].X - a.X, bombs[i].Y - a.Y).Length > 1.00001))
                                throw new InvalidDataException("Placement changed a bomb-cued reset.");
                        }
                        var gaps = updated.Domains.ToDictionary(pair => pair.Key, pair => pair.Value.Context.Placement.Gap);
                        var sparse = updated.Domains.Where(pair => pair.Value.Context.Placement.SparseAllowance > 0)
                            .Select(pair => pair.Key).ToHashSet();
                        double gap = Number(profile["drumming"], "minimumGapSeconds", .95);
                        try
                        {
                            PlacementParity.Validate(candidate, bombs, new HashSet<ProjectedReset>(), gaps, sparse, gap,
                                Number(profile["drumming"], "sparseMinimumGapSeconds", gap), report, original, audit, adherence);
                        }
                        catch (PlacementDeadEnd) when (draft != null)
                        {
                            draft(new PlacementDraft(candidate, released.ToHashSet(), audit));
                            throw;
                        }
                        return candidate;
                    }
                }
                catch (PlacementDeadEnd failure) when (attempt < 17 &&
                    (!localRepair || !failure.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal)))
                {
                    if (failure.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal) &&
                        feedbackPasses > 0 && lastAudited != null)
                    {
                        feedbackPasses--;
                        foreach (var note in lastAudited)
                            parityPositions[note.Id] = note;
                        continue;
                    }
                    if (failure.Reason == "No geometric direction domain" && Relocate(failure.Notes))
                        continue;
                    var fixedNotes = failure.Notes.Select(note => note.Id).Concat(failure.Blockers ?? Array.Empty<int>()).ToArray();
                    if ((failure.VisibilityTarget.HasValue || repairs.Any(beat => Math.Abs(beat - failure.Beat) <= .0001)) &&
                        ReleaseOptional(fixedNotes))
                        continue;
                    if (failure.Reason is "No joint geometry" or "Bounded placement budget exhausted" &&
                        failure.Notes.GroupBy(note => note.Hand).Any(hand => hand.Count() > 1) && PreserveStackAxes(failure.Beat))
                        continue;
                    if (repairs.Count >= PlacementPolicy.MaximumRepairWindows ||
                        repairs.Any(beat => Math.Abs(beat - failure.Beat) <= .0001))
                    {
                        var candidates = seeds.Where(note => Math.Abs(clock.Gap(note.Beat, failure.Beat)) <= .30 &&
                            !anchors.Contains(note.Id) && !clearance.Contains(note.Id) &&
                            cues.GetValueOrDefault(note.Id)?.PreservePosition != true).Select(note => note.Id).ToArray();
                        if (candidates.Length == 0)
                        {
                            if (TryRefineParityPositions(failure))
                                continue;
                            if (Environment.GetEnvironmentVariable("BS_DOWNMAP_TRACE_DOMAINS") == "1" && currentDomains != null)
                                Console.Error.WriteLine(string.Join("; ", failure.Notes.Select(note =>
                                    $"{note.Id}/{note.Hand}/{note.Direction}: [{string.Join(",", currentDomains[note.Id].Directions)}]")));
                            throw;
                        }
                        clearance.UnionWith(candidates);
                        continue;
                    }
                    repairs.Add(failure.Beat);
                    clearance.UnionWith(failure.Notes.Where(note => !anchors.Contains(note.Id) &&
                        cues.GetValueOrDefault(note.Id)?.PreservePosition != true).Select(note => note.Id));
                }
                catch (DirectionPathFailure failure) when (attempt < 17)
                {
                    var match = Regex.Match(failure.Message, @"at beat (\S+) \((left|right) hand\)");
                    if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double beat))
                        throw;
                    int hand = match.Groups[2].Value == "left" ? 0 : 1;
                    var recent = seeds.Where(note => note.Hand == hand && note.Beat <= beat + .0001 &&
                        clock.Gap(note.Beat, beat) <= 1).OrderByDescending(note => note.Beat).Take(6).ToArray();
                    var exits = recent.Where(note => cues.GetValueOrDefault(note.Id)?.Cue.Intent.Kind == "reset_exit" &&
                        arrows.ContainsKey(note.Id)).ToArray();
                    if (exits.Length > 0)
                    {
                        foreach (var exit in exits)
                            arrows.Remove(exit.Id);
                        continue;
                    }
                    var editable = recent.Where(note => !anchors.Contains(note.Id) &&
                        cues.GetValueOrDefault(note.Id)?.PreservePosition != true && !clearance.Contains(note.Id))
                        .Select(note => note.Id).ToArray();
                    if (editable.Length > 0)
                    {
                        clearance.UnionWith(editable);
                        continue;
                    }
                    var pair = gestures.Pairs.FirstOrDefault(pair => !flexiblePairs.Contains(pair.First.Id) &&
                        recent.Any(note => note.Id == pair.First.Id || note.Id == pair.Second.Id));
                    if (pair == null)
                    {
                        if (ReleaseOptional(recent.Select(note => note.Id)))
                            continue;
                        throw;
                    }
                    foreach (int id in new[] { pair.First.Id, pair.Second.Id })
                    {
                        flexiblePairs.Add(id);
                        arrows[id] = new[] { 0, 1 };
                    }
                }
                catch (PlacementDeadEnd failure)
                {
                    if (Environment.GetEnvironmentVariable("BS_DOWNMAP_TRACE_DOMAINS") == "1" && currentDomains != null)
                        Console.Error.WriteLine(string.Join("; ", failure.Notes.Select(note =>
                            $"{note.Id}/{note.Hand}/{note.Direction}: [{string.Join(",", currentDomains[note.Id].Directions)}]")));
                    throw;
                }
            }

            bool TryRefineParityPositions(PlacementDeadEnd failure)
            {
                if (!failure.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal) ||
                    !failure.Notes.Any(note => note.Direction == 8))
                    return false;
                var changed = failure.Notes.Where(note =>
                {
                    if (anchors.Contains(note.Id) || entries.Contains(note.Id) || cues.ContainsKey(note.Id))
                        return false;
                    var prior = parityPositions.GetValueOrDefault(note.Id, source[note.Id]);
                    return (prior.X, prior.Y) != (note.X, note.Y);
                }).ToArray();
                if (changed.Length == 0 || !refinedParityWindows.Add(failure.Beat))
                    return false;
                // The parity checker infers a dot's cut from neighboring cells.
                // Retry planning with the placements that the checker actually saw.
                foreach (var note in changed)
                    parityPositions[note.Id] = note;
                return true;
            }

            bool ReleaseOptional(IEnumerable<int> affected)
            {
                if (released.Count - pattern.ReleasedLinks.Count >= 5)
                    return false;
                var ids = affected.ToHashSet();
                var selected = chartLinks.FirstOrDefault(link => rhythm.Links.Optional.Contains(link.Id) &&
                    !released.Contains(link.Id) && link.AnchorIds.Overlaps(ids));
                if (selected == null)
                    return false;
                released.Add(selected.Id);
                var retained = chartLinks.Where(link => !released.Contains(link.Id)).ToArray();
                anchors = retained.SelectMany(link => link.AnchorIds).ToHashSet();
                endpoints = retained.SelectMany(link => link.Endpoints).ToArray();
                return true;
            }

            bool PreserveStackAxes(double beat)
            {
                bool changed = false;
                var attacks = NestedTimings.Group(seeds.Where(note => Math.Abs(clock.Gap(note.Beat, beat)) <= 1.8));
                foreach (var attack in attacks)
                {
                    var notes = attack.Notes;
                    double direction = notes[0].Direction;
                    if (notes.Count != 3 || notes.Select(note => note.Hand).Distinct().Count() != 2 ||
                        direction is < 0 or > 7 || notes.Any(note => note.Direction != direction ||
                        note.Angle != 0 || anchors.Contains(note.Id) || cues.ContainsKey(note.Id)))
                        continue;
                    foreach (var note in notes)
                    {
                        if (!stackAxes.Add(note.Id))
                            continue;
                        arrows[note.Id] = new[] { (int)direction };
                        clearance.Add(note.Id);
                        changed = true;
                    }
                }
                return changed;
            }

            bool Relocate(IReadOnlyList<Note> blocked)
            {
                var ids = blocked.Select(note => note.Id).ToHashSet();
                var affected = gestures.Returns.Where(reset => ids.Contains(reset.First.Id) || ids.Contains(reset.Second.Id)).ToArray();
                bool changed = false;
                foreach (var reset in affected)
                {
                    changed |= relocated.Add(reset.First.Id);
                    changed |= relocated.Add(reset.Second.Id);
                }
                if (!changed)
                    return false;
                foreach (var reset in gestures.Returns.Where(reset =>
                    relocated.Contains(reset.First.Id) || relocated.Contains(reset.Second.Id)))
                {
                    var tail = cues[reset.Second.Id];
                    cues[reset.Second.Id] = tail with { Cue = tail.Cue with { PreviousResetNoteId = reset.First.Id } };
                }
                foreach (int id in relocated)
                {
                    var cue = cues[id];
                    var bombIds = gestures.Returns.Where(reset => reset.First.Id == id).SelectMany(reset => reset.Cues).Distinct().ToArray();
                    var cells = (from x in Enumerable.Range(0, 4)
                                 from y in Enumerable.Range(0, 3)
                                 where bombIds.All(i => new SwingClearance.Point(bombs[i].X - x, bombs[i].Y - y).Length <= 1.00001)
                                 select ((double)x, (double)y)).ToHashSet();
                    cues[id] = cue with { PreservePosition = false, AllowedCells = cells };
                }
                return true;
            }

            bool VariableTempo() => clock.Regions.Count != 1 || clock.Regions[0].SecondsPerBeat != 60 / level.Bpm;

            int NewErrors(JsonObject audit) => Errors(audit).Count(issue => !inheritedErrors.Contains(issue));

            IEnumerable<IReadOnlyDictionary<(double Beat, int Hand), int>> RepairBatches(
                Dictionary<(double Beat, int Hand), int> changes)
            {
                if (changes.Count == 0)
                    yield break;
                yield return changes;
                if (changes.Count < 2)
                    yield break;
                foreach (var hand in changes.GroupBy(pair => pair.Key.Hand))
                {
                    var window = new Dictionary<(double Beat, int Hand), int>();
                    double previous = double.NegativeInfinity;
                    foreach (var item in hand.OrderBy(pair => pair.Key.Beat))
                    {
                        if (window.Count > 0 && clock.Gap(previous, item.Key.Beat) > 1.8)
                        {
                            yield return window;
                            window = new Dictionary<(double Beat, int Hand), int>();
                        }
                        window.Add(item.Key, item.Value);
                        previous = item.Key.Beat;
                    }
                    if (window.Count < changes.Count)
                        yield return window;
                }
            }

            static HashSet<(double Beat, int Hand)> Errors(JsonObject audit) =>
                audit["issues"].AsArray().Where(item => item["status"].GetValue<string>() == "error")
                    .Select(item => (Math.Round(StandardSong.Number(item["beat"]), 4), item["hand"].GetValue<int>())).ToHashSet();

            JsonObject DirectionControls(PlacementPlan plan)
            {
                var controls = (JsonObject)(profile["geometry"]?.DeepClone() ?? new JsonObject());
                foreach (var (key, number) in plan.Domains.Values.First().Context.Placement.Controls)
                    if (!controls.ContainsKey(key))
                        controls[key] = number;
                return controls;
            }

            JsonObject DirectionNote(Note note, PlacementPlan plan, DirectionDomain domain)
            {
                var cue = cues.GetValueOrDefault(note.Id);
                var controls = plan.Domains[note.Id].Context.Placement.Controls;
                var position = parityPositions.GetValueOrDefault(note.Id, note);
                var value = new JsonObject
                {
                    ["id"] = note.Id, ["b"] = note.Beat, ["c"] = note.Hand, ["x"] = position.X, ["y"] = position.Y,
                    ["d"] = note.Direction, ["a"] = note.Angle, ["seconds"] = clock.Seconds(note.Beat),
                    ["linkedAnchor"] = anchors.Contains(note.Id), ["streamEntry"] = entries.Contains(note.Id),
                    ["geometryDirections"] = DirectionArray(domain.Directions), ["linkedDirectionRepair"] = domain.LinkedDirectionRepair,
                    ["recoveryCue"] = walls.Any(wall => wall.Y >= 2 && wall.Width >= 3 &&
                        clock.Gap(wall.Beat, wall.Beat + wall.Duration) >= controls["resetCueMinimumCrouchSeconds"] &&
                        wall.Beat <= note.Beat && clock.Gap(wall.Beat + wall.Duration, note.Beat) <= controls["crouchRecoverySeconds"]),
                };
                if (parallel.TryGetValue(note.Id, out int parallelDirection))
                    value["parallelDoubleDirection"] = parallelDirection;
                if (cue != null)
                {
                    var intent = new JsonObject { ["kind"] = cue.Cue.Intent.Kind };
                    if (targetDirections.TryGetValue(note.Id, out int direction))
                        intent["targetDirection"] = direction;
                    if (arrows.TryGetValue(note.Id, out var fixedDirections))
                        intent["allowedDirections"] = DirectionArray(fixedDirections);
                    if (cue.Cue.PairId.HasValue)
                        intent["pairId"] = cue.Cue.PairId.Value;
                    if (flexiblePairs.Contains(note.Id))
                        intent["flexiblePhase"] = true;
                    if (cue.PreservePosition)
                        intent["preservePosition"] = true;
                    value["intent"] = intent;
                }
                else if (!entries.Contains(note.Id))
                {
                    var context = plan.Domains[note.Id].Context.Placement;
                    value["diagonalCost"] = Number(profile, "diagonalPreference", 0) * context.RampScale;
                    double minimum = Number(profile["drumming"], "minimumGapSeconds", .95);
                    if (context.SparseAllowance > 0)
                        minimum = Number(profile["drumming"], "sparseMinimumGapSeconds", minimum);
                    if (context.Gap >= minimum && double.IsFinite(minimum))
                    {
                        value["minimumDrumGapSeconds"] = minimum;
                        // Retain the preset's drumming weight at its default adherence.
                        value["downstrokePreference"] = Number(profile["drumming"], "downstrokePreference", 7.5) *
                            ((1 - adherence) / (defaultAdherence is >= 0 and < 1 ? 1 - defaultAdherence : 1)) *
                            context.RampScale;
                    }
                    if (context.Gap >= .30 && note.Direction is 2 or 3)
                        value["horizontalRetention"] = Number(profile, "horizontalRetention", 0);
                }
                return value;
            }
        }

        private static JsonArray DirectionArray(IEnumerable<int> directions) =>
            new(directions.Select(direction => (JsonNode)JsonValue.Create(direction)).ToArray());

        private static bool Flag(JsonNode profile, string key) =>
            profile?[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;

        private static double Number(JsonNode profile, string key, double fallback = double.NaN)
        {
            double value = StandardSong.Number(profile?[key]);
            return double.IsFinite(value) ? value : fallback;
        }
    }
}
