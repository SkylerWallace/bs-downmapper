using System.Text.Json;
using System.Text.Json.Nodes;
using BsDownmap.Engine;

internal static class EditorGeneration
{
    internal static async Task<object> Run(JsonObject request, Action<float, string> progress = null)
    {
        string root = EditorPreview.Required(request, "engine");
        string folder = EditorPreview.Required(request, "source");
        string planFile = EditorPreview.Required(request, "batch-plan");
        var plan = EditorPreview.ReadObject(planFile);
        var entries = plan["targets"].AsArray().Select(row => row.AsObject()).ToArray();
        var targets = entries.Select(entry => new SpreadTarget(
            EditorPreview.Required(entry, "difficulty"),
            (entry["foundation"] ?? plan["foundation"]).GetValue<string>(),
            EditorPreview.Required(entry, "targetSha256"))).ToArray();
        string baseReports = request["reports"]?.GetValue<string>() ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bs-downmap", "runs");
        string reports = Path.Combine(baseReports, "native-spread-" + Guid.NewGuid().ToString("N"));
        var runtime = new DirectionRuntime(Path.Combine(root, ".tools", "quickjs-runtime"));
        var warnings = new List<DraftWarning>();
        string reviewPath = Path.Combine(reports, "placement-review.json");
        var rankCache = new EditorPreview.RankCache();
        progress?.Invoke(.04f, "Preparing song");
        await SpreadGeneration.Run(folder, reports, EditorPreview.Required(plan, "infoSha256"), targets, Build,
            beforePublish: () =>
            {
                progress?.Invoke(.97f, "Checking and saving difficulties");
                if (warnings.Count > 0) File.WriteAllText(reviewPath, JsonSerializer.Serialize(warnings));
            });
        progress?.Invoke(1f, "Difficulties saved");
        return new { updated = targets.Select(target => target.Difficulty), reports, warnings,
            reviewPath = warnings.Count > 0 ? reviewPath : null };

        async Task Build(SpreadTargetContext transaction, CancellationToken token)
        {
            if (transaction.Completed.Count == 0)
            {
                var assembly = typeof(EditorGeneration).Assembly;
                File.WriteAllText(Path.Combine(reports, "build-info.json"), JsonSerializer.Serialize(new
                {
                    assembly = assembly.GetName().Name,
                    version = assembly.GetName().Version.ToString()
                }));
            }
            var target = transaction.Target;
            void Report(float phase, string message) =>
                progress?.Invoke(.08f + .88f * (transaction.Completed.Count + phase) / targets.Length,
                    $"{target.Difficulty} ({transaction.Completed.Count + 1}/{targets.Length}): {message}");
            Report(0f, "Selecting timings");
            var entry = entries.Single(row => row["difficulty"].GetValue<string>() == target.Difficulty);
            string mode = EditorPreview.Required(entry, "mode");
            if (mode is not ("authored" or "best-guess" or "manual"))
                throw new InvalidDataException($"Unknown rhythm mode {mode}.");
            var originalSong = new StandardSong(transaction.Source);
            var stagedSong = new StandardSong(transaction.Staging);
            var saved = stagedSong.SavedTimings().Where(pair => !transaction.Pending.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var profile = EditorPreview.ReadObject(Path.Combine(root, "profiles", target.Difficulty.ToLowerInvariant() + ".json"));
            double defaultAdherence = StandardSong.Number(profile["parityAdherence"]);
            ApplySettings(profile, entry["settings"] as JsonObject);
            var selectionRequest = (JsonObject)request.DeepClone();
            selectionRequest["source"] = mode == "authored" || entry["edits"] != null ? transaction.Source : transaction.Staging;
            selectionRequest["difficulty"] = target.Difficulty;
            selectionRequest["rhythm-mode"] = mode;
            selectionRequest["parity-adherence"] = profile["parityAdherence"]?.DeepClone();
            selectionRequest["source-difficulty"] = target.Foundation;
            selectionRequest["placement-settings"] = entry["settings"]?.DeepClone();
            if (entry["patternModel"] != null)
                selectionRequest["skip-placed-timing-comparison"] = true;
            selectionRequest["hand-alternation"] = entry["handAlternation"]?.DeepClone() ?? profile["handAlternation"]?.DeepClone();
            if (entry["rhythm"] != null) selectionRequest["rhythm-difficulty"] = entry["rhythm"].DeepClone();
            if (entry["targetNoteFraction"] != null)
                selectionRequest["target-note-fraction"] = entry["targetNoteFraction"].DeepClone();
            if (entry["model"] != null) selectionRequest["timing-model"] = entry["model"].DeepClone();
            if (entry["rhythmLinks"]?.GetValue<bool>() == false) selectionRequest["no-rhythm-links"] = true;
            string settingsPath = null;
            if (entry["timingModelSettings"] is JsonObject timingSettings)
            {
                settingsPath = Path.Combine(Path.GetDirectoryName(transaction.Staging), target.Difficulty + "-timing.json");
                File.WriteAllText(settingsPath, timingSettings.ToJsonString());
                selectionRequest["timing-model-settings"] = settingsPath;
            }

            PreparedTiming prepared;
            IReadOnlyList<Note> approved;
            if (entry["edits"] is JsonObject edits)
            {
                string rank = EditorPreview.Required(selectionRequest, "rhythm-difficulty");
                var level = originalSong.Read(rank);
                string fingerprint = SpreadGeneration.Fingerprint(Path.Combine(level.Folder, level.Filename));
                if (!string.Equals(fingerprint, edits["sourceSha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The timing source changed. Reopen the timing editor.");
                int targetRank = Array.IndexOf(TimingPreparation.Standard.ToArray(), target.Difficulty);
                var higher = originalSong.SavedTimings().Where(pair =>
                    TimingPreparation.Standard.Take(targetRank).Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
                prepared = TimingPreparation.Prepare(level, profile, mode == "authored" ? null : higher, mode == "best-guess",
                    preserveSourceCues: mode == "best-guess" &&
                        (entry["model"]?.GetValue<string>() ?? "nested-priority-v1") == "nested-priority-v1" &&
                        rank == TimingPreparation.Standard.FirstOrDefault(higher.ContainsKey));
                var merges = (edits["merges"] as JsonArray ?? new JsonArray()).Select(row =>
                    new TimingMerge(row["noteId"].GetValue<int>(), row["targetId"].GetValue<int>())).ToArray();
                var available = prepared.Context.Notes;
                if (edits["proposedHandIds"] is JsonArray proposed && proposed.Count > 0)
                {
                    if (mode != "best-guess")
                        throw new InvalidDataException("Hand proposals require automatic timings.");
                    var supported = TimingParityRepair.AdditionalHands(prepared);
                    available = available.Concat(proposed.Select(node => node.GetValue<int>()).Distinct()
                        .Select(id => supported.TryGetValue(id, out var note) ? note :
                            throw new InvalidDataException("A hand proposal no longer exists. Reopen the timing editor."))).ToArray();
                }
                var byId = available.ToDictionary(note => note.Id);
                merges = merges.Where(merge => byId.TryGetValue(merge.NoteId, out var a) &&
                    byId.TryGetValue(merge.TargetId, out var b) && Survives(a.Beat) && Survives(b.Beat)).ToArray();
                approved = TimingMerges.ApplyEditorEdits(available,
                    (edits["removed"] as JsonArray ?? new JsonArray()).Select(node => node.GetValue<double>()).ToArray(),
                    (edits["removedNoteIds"] as JsonArray ?? new JsonArray()).Select(node => node.GetValue<int>()).ToArray(),
                    merges, prepared.Links.LockedAnchors, level.EndBeat);
            }
            else
            {
                if (mode == "manual")
                    throw new InvalidDataException("Open the timing editor and approve the rhythm first.");
                (prepared, approved) = await EditorPreview.Select(selectionRequest, saved, rankCache);
            }
            approved = approved.Where(note => Survives(note.Beat)).ToArray();
            var conflicts = NestedTimings.Conflicts(saved, target.Difficulty, NestedTimings.DistinctBeats(approved));
            if (conflicts.Count > 0)
                throw new InvalidDataException($"Standard {target.Difficulty} has timing conflicts with " +
                    string.Join(", ", conflicts.Select(conflict => $"{conflict.Difficulty} at {string.Join(", ", conflict.Beats.Take(3))}")) + ".");

            Report(.27f, "Planning patterns");
            var foundation = originalSong.Read(target.Foundation);
            var higherDifficulty = TimingPreparation.Standard.TakeWhile(rank => rank != target.Difficulty)
                .LastOrDefault(saved.ContainsKey);
            var parityReference = higherDifficulty == null ? foundation : stagedSong.Read(higherDifficulty);
            var linkParityRepairs = entry["rhythmLinks"]?.GetValue<bool>() != false;
            var pattern = PatternPreparation.Prepare(foundation, prepared, approved);
            IReadOnlyList<Note> seeds = pattern.Seeds.Select(seed => seed.Note).ToArray();
            if (entry["patternModel"] is JsonValue model)
            {
                var (package, manifest) = EditorPreview.FindModel(EditorPreview.Required(request, "models"),
                    "pattern", model.GetValue<string>());
                var protectedIds = MappingIntent.Detect(seeds, prepared.Level.Gameplay.Bombs,
                    prepared.Level.Clock, pattern.Anchors).ResetNotes();
                protectedIds.UnionWith(pattern.Anchors);
                var context = JsonSerializer.SerializeToNode(new
                {
                    seeds = seeds.Select(EditorPreview.ModelNote),
                    foundation = EditorPreview.ModelLevel(TimingPreparation.Prepare(foundation, profile, automatic: false)),
                    rhythm = EditorPreview.ModelLevel(prepared with
                    {
                        Context = prepared.Context with { Notes = approved },
                    }),
                    difficulty = target.Difficulty,
                    protected_ids = protectedIds,
                }).AsObject();
                var output = await CustomModelHost.Invoke(package, manifest, context,
                    entry["patternModelSettings"] as JsonObject ?? new JsonObject(),
                    request["python"]?.GetValue<string>(), Path.Combine(root, "tools", "custom_model_adapter.py"), token);
                seeds = CustomModelHost.ApplyPatternResult(output, seeds, protectedIds);
            }

            var jump = entry["jumpSettings"] as JsonObject;
            var (speed, offset, halfJump) = JumpSettings(originalSong, prepared.Level.Bpm, target.Difficulty, jump);
            (IReadOnlyList<Note> Notes, ISet<ChartLinkId> Released) generated;
            PlacementDraft draft = null;
            bool benchmarkParity = Environment.GetEnvironmentVariable("BS_DOWNMAP_BENCHMARK_PARITY") == "1";
            var parityAudits = new System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<Note>, JsonObject>();
            void RecordAudit(IReadOnlyList<Note> notes, JsonObject audit)
            {
                parityAudits.Remove(notes);
                parityAudits.Add(notes, audit);
            }
            bool fullyPlaced = false;
            PlacementDeadEnd lastBlocker = null;
            bool tryHandsEarly = request["placement-hand-fallback"]?.GetValue<bool>() == true &&
                mode == "best-guess" && entry["edits"] == null && entry["patternModel"] == null;
            double adherence = StandardSong.Number(profile["parityAdherence"]);
            if (!double.IsFinite(adherence))
                adherence = 1;
            double parityRepairSeconds = 3 + 12 * Math.Clamp(adherence, 0, 1);
            using var repairBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
            void RecordDraft(PlacementDraft candidate)
            {
                // The first snapshot is the pattern seed. Subsequent snapshots
                // passed geometry checks and preserve validated reset cues.
                if (draft == null)
                {
                    draft = candidate;
                    repairBudget.CancelAfter(TimeSpan.FromSeconds(45));
                    return;
                }
                if (!fullyPlaced || candidate.Audit["issues"].AsArray().Count < draft.Audit["issues"].AsArray().Count)
                    draft = candidate;
                if (!fullyPlaced)
                {
                    Report(.80f, "Trying parity repairs");
                    repairBudget.CancelAfter(TimeSpan.FromSeconds(parityRepairSeconds));
                }
                fullyPlaced = true;
            }
            try
            {
                generated = await NativeChartGenerator.Generate(prepared, pattern, seeds, profile, speed,
                    halfJump * 60 / prepared.Level.Bpm, runtime, repairBudget.Token,
                    () => { if (!fullyPlaced) Report(.70f, "Checking placements and parity"); },
                    RecordDraft, foundation, defaultAdherence,
                    blocked: failure =>
                    {
                        if (lastBlocker == null || lastBlocker.Reason == "No geometric direction domain" ||
                            failure.Reason == "No joint geometry")
                            lastBlocker = failure;
                    }, tryHandsEarly: tryHandsEarly, audited: RecordAudit, parityReference: parityReference,
                    linkParityRepairs: linkParityRepairs);
            }
            catch (Exception error) when (error is PlacementDeadEnd or DirectionPathFailure ||
                error is OperationCanceledException && draft != null && repairBudget.IsCancellationRequested && !token.IsCancellationRequested)
            {
                if (draft == null)
                    throw new InvalidDataException($"Standard {target.Difficulty}: {error.Message}", error);

                var findings = draft.Audit["issues"].AsArray().Select(issue =>
                    new DraftFinding(StandardSong.Number(issue["beat"]),
                        issue["hand"].GetValue<int>() == 0 ? "Left" : "Right",
                        issue["status"].GetValue<string>(),
                        error is OperationCanceledException ? "Placement or local repair budget exhausted" :
                            fullyPlaced ? "No local direction repair passed placement and parity validation" :
                                "Placement failed before local parity repair")).ToArray();
                string failure = error is OperationCanceledException
                    ? "Placement and parity repair reached its time limit. Review the draft."
                    : error.Message;
                warnings.Add(new DraftWarning(target.Difficulty, failure,
                    (error as PlacementDeadEnd)?.Beat, !fullyPlaced, findings));
                generated = (draft.Notes, draft.Released);
                var blocker = error as PlacementDeadEnd ?? lastBlocker;
                if (error is DirectionPathFailure)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(error.Message, @"at beat (\S+) \((left|right) hand\)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double blockedBeat))
                    {
                        int hand = match.Groups[2].Value == "left" ? 0 : 1;
                        var blocked = seeds.Where(note => note.Hand == hand && Math.Abs(note.Beat - blockedBeat) <= .0001).ToArray();
                        if (blocked.Length > 0)
                            blocker = new PlacementDeadEnd(blockedBeat, blocked, "No supported direction path");
                    }
                }
                if (request["placement-hand-fallback"]?.GetValue<bool>() == true &&
                    !fullyPlaced && blocker != null && mode == "best-guess" && entry["edits"] == null &&
                    entry["patternModel"] == null && !blocker.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal))
                {
                    var protectedIds = prepared.Context.ProtectedIds.Concat(pattern.Anchors).ToHashSet();
                    var resetIds = MappingIntent.Detect(seeds, prepared.Level.Gameplay.Bombs,
                        prepared.Level.Clock, pattern.Anchors).ResetNotes();
                    resetIds.UnionWith(prepared.ResetIds);
                    protectedIds.UnionWith(resetIds);
                    using var fallback = CancellationTokenSource.CreateLinkedTokenSource(token);
                    fallback.CancelAfter(TimeSpan.FromSeconds(360));
                    var beats = NestedTimings.DistinctBeats(approved);
                    var handOptions = ParityRhythmOptions.Around(prepared, seeds, blocker.Beat, blocker.Notes[0].Hand,
                        protectedIds, resetIds, splitStacks: true)
                        .Where(option => NestedTimings.DistinctBeats(option.Notes).SequenceEqual(beats))
                        .GroupBy(option => option.EditCount)
                        .SelectMany(group => group.OrderBy(option => option.Cost).Take(4));
                    var sourceGroups = NestedTimings.Group(prepared.Context.Notes);
                    var links = TimelineRepeats.Find(prepared.Level, prepared.Context.Notes).Sections
                        .SelectMany(section => section.Links)
                        .Select(link => link.Select(index => sourceGroups[index].Beat).ToArray()).ToArray();
                    IEnumerable<ParityRhythmOptions.Option> RemoveWindow(IReadOnlyList<Note> rhythm, double failureBeat)
                    {
                        var groups = NestedTimings.Group(rhythm);
                        var attack = groups.Where(group => group.Beat <= failureBeat + .00001)
                            .TakeLast(3).ToArray();
                        return Enumerable.Range(1, attack.Length).Select(width =>
                        {
                            var removed = attack.TakeLast(width).Select(group => group.Beat).ToHashSet();
                            int count;
                            do
                            {
                                count = removed.Count;
                                foreach (var link in links)
                                    if (link.Any(removed.Contains))
                                        removed.UnionWith(link);
                            } while (count != removed.Count);
                            return removed;
                        }).Where(removed =>
                            !groups.Any(group => removed.Contains(group.Beat) &&
                                group.Notes.Any(note => protectedIds.Contains(note.Id) ||
                                    prepared.Links.LockedAnchors.Contains(note.Id))) &&
                            !saved.Where(pair => TimingPreparation.Standard.SkipWhile(rank => rank != target.Difficulty)
                                .Skip(1).Contains(pair.Key)).SelectMany(pair => pair.Value)
                                .Any(beat => NestedTimings.Nearest(beat, removed.OrderBy(value => value).ToArray()) >= 0))
                            .Select(removed => new ParityRhythmOptions.Option(
                                groups.Where(group => !removed.Contains(group.Beat))
                                    .SelectMany(group => group.Notes).ToArray(), removed.Count, removed.Count));
                    }
                    var compound = error is DirectionPathFailure
                        ? ParityRhythmOptions.CompoundDirectionRepair(prepared.Level, seeds, blocker.Beat,
                            blocker.Notes[0].Hand, protectedIds).OrderBy(option => option.Cost).Take(12).ToArray()
                        : Array.Empty<ParityRhythmOptions.Option>();
                    var options = new LinkedList<ParityRhythmOptions.Option>(compound.Concat(handOptions)
                        .Concat(RemoveWindow(approved, blocker.Beat)));
                    var visited = new HashSet<string>();
                    var triedParityWindows = new HashSet<string>();
                    var triedGeometryWindows = new HashSet<(double Beat, int Hand, int BeatCount)>();
                    var pruningSteps = new Dictionary<ParityRhythmOptions.Option, int>();
                    var localTargets = new Dictionary<ParityRhythmOptions.Option, (double Beat, int Hand)>();
                    var boundaryPlacements = new Dictionary<ParityRhythmOptions.Option, PlacementDraft>();
                    try
                    {
                        for (int attempt = 0; options.Count > 0 && attempt < 180; attempt++)
                        {
                            fallback.Token.ThrowIfCancellationRequested();
                            var option = options.First.Value;
                            options.RemoveFirst();
                            if (!visited.Add(string.Join(",", option.Notes.Select(note => $"{note.Id}/{note.Hand}")))) continue;
                            bool traceCompound = benchmarkParity && compound.Contains(option);
                            var revised = PatternPreparation.Prepare(foundation, prepared, option.Notes);
                            var trialSeeds = revised.Seeds.Select(seed => seed.Note).ToArray();
                            if (traceCompound)
                            {
                                var original = seeds.ToDictionary(note => note.Id);
                                var editedBeats = option.Notes.Where(note =>
                                    !original.TryGetValue(note.Id, out var before) || before != note)
                                    .Select(note => note.Beat).ToHashSet();
                                var planned = trialSeeds.Where(note => editedBeats.Contains(note.Beat));
                                Console.Error.WriteLine($"Compound near {blocker.Beat:R}: " +
                                    string.Join("; ", planned.Select(note => FormattableString.Invariant(
                                        $"{note.Beat:R} hand={note.Hand} cell=({note.X},{note.Y}) cut={note.Direction} angle={note.Angle}"))));
                            }
                            HashSet<int> fixedOutside = null;
                            if (boundaryPlacements.Remove(option, out var boundary))
                            {
                                var prior = boundary.Notes.ToDictionary(note => note.Id);
                                var attacks = NestedTimings.Group(boundary.Notes);
                                double beat = localTargets[option].Beat;
                                double first = attacks.Where(group => group.Beat < beat).TakeLast(4)
                                    .Select(group => group.Beat).DefaultIfEmpty(beat).First();
                                double last = attacks.Where(group => group.Beat > beat).Take(4)
                                    .Select(group => group.Beat).DefaultIfEmpty(beat).Last();
                                var clock = prepared.Level.Clock;
                                fixedOutside = trialSeeds.Where(note => prior.ContainsKey(note.Id) && !revised.Anchors.Contains(note.Id) &&
                                    (clock.Gap(note.Beat, first) > .75 || clock.Gap(last, note.Beat) > .75))
                                    .Select(note => note.Id).ToHashSet();
                                trialSeeds = trialSeeds.Select(note => fixedOutside.Contains(note.Id) ? prior[note.Id] : note).ToArray();
                            }
                            PlacementDraft validated = null;
                            bool sawDraft = false;
                            using var trial = CancellationTokenSource.CreateLinkedTokenSource(fallback.Token);
                            trial.CancelAfter(TimeSpan.FromSeconds(9));
                            try
                            {
                                var result = await NativeChartGenerator.Generate(prepared, revised,
                                    trialSeeds, profile, speed,
                                    halfJump * 60 / prepared.Level.Bpm, runtime, trial.Token, foundation: foundation, parityReference: parityReference,
                                    defaultAdherence: defaultAdherence, tryHandsEarly: true, fixedOutsideWindow: fixedOutside,
                                    draft: candidate =>
                                    {
                                        if (sawDraft)
                                            validated = candidate;
                                        sawDraft = true;
                                    }, audited: RecordAudit);
                                prepared.Level.Gameplay.WriteWithLinks(result.Notes, prepared.Links.Optional, result.Released);
                                generated = result;
                                approved = option.Notes;
                                warnings.RemoveAt(warnings.Count - 1);
                                if (Environment.GetEnvironmentVariable("BS_DOWNMAP_TRACE_DOMAINS") == "1")
                                    Console.Error.WriteLine($"Accepted attack repair: {pruningSteps.GetValueOrDefault(option)} parity pruning steps.");
                                if (Environment.GetEnvironmentVariable("BS_DOWNMAP_BENCHMARK_PARITY") == "1")
                                    Console.Error.WriteLine($"Parity timing fallback [{target.Difficulty}]: {pruningSteps.GetValueOrDefault(option)}");
                                break;
                            }
                            catch (Exception rejected) when (rejected is PlacementDeadEnd or DirectionPathFailure ||
                                rejected is OperationCanceledException && trial.IsCancellationRequested && !token.IsCancellationRequested)
                            {
                                if (traceCompound)
                                    Console.Error.WriteLine($"Compound rejected: {rejected.GetType().Name}: {rejected.Message}");
                                if (Environment.GetEnvironmentVariable("BS_DOWNMAP_TRACE_DOMAINS") == "1")
                                    Console.Error.WriteLine($"Attack repair near beat {blocker.Beat:R} ({option.Notes.Count} notes): {rejected.Message}");
                                if (rejected is PlacementDeadEnd next)
                                {
                                    bool parity = next.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal);
                                    if (parity && localTargets.TryGetValue(option, out var targetFinding) &&
                                        targetFinding == (next.Beat, next.Notes[0].Hand))
                                        continue;
                                    int count = pruningSteps.GetValueOrDefault(option);
                                    foreach (var candidate in RemoveWindow(option.Notes, next.Beat).Take(1))
                                    {
                                        pruningSteps[candidate] = count + (parity ? 1 : 0);
                                        options.AddFirst(candidate);
                                    }
                                    var distinct = NestedTimings.DistinctBeats(option.Notes);
                                    if (next.Reason == "No joint geometry" &&
                                        triedGeometryWindows.Add((next.Beat, next.Notes[0].Hand, distinct.Length)))
                                    {
                                        var geometryOptions = ParityRhythmOptions.Around(prepared, trialSeeds, next.Beat,
                                                next.Notes[0].Hand, protectedIds, resetIds, splitStacks: true)
                                            .Where(candidate => NestedTimings.DistinctBeats(candidate.Notes).SequenceEqual(distinct))
                                            .OrderBy(candidate => candidate.Cost).Take(4).Reverse();
                                        foreach (var candidate in geometryOptions)
                                        {
                                            pruningSteps[candidate] = count;
                                            options.AddFirst(candidate);
                                        }
                                    }
                                    string windowKey = next.Beat.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                                        "/" + next.Notes[0].Hand + "/" + distinct.Length;
                                    if (parity && triedParityWindows.Add(windowKey))
                                    {
                                        var local = ParityRhythmOptions.Around(prepared, option.Notes, next.Beat,
                                                next.Notes[0].Hand, protectedIds, resetIds)
                                            .Where(candidate => NestedTimings.DistinctBeats(candidate.Notes).SequenceEqual(distinct))
                                            .OrderBy(candidate => candidate.Cost).Take(3).Reverse();
                                        foreach (var candidate in local)
                                        {
                                            pruningSteps[candidate] = count;
                                            localTargets[candidate] = (next.Beat, next.Notes[0].Hand);
                                            if (validated != null)
                                                boundaryPlacements[candidate] = validated;
                                            options.AddFirst(candidate);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (fallback.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        // Retain the original draft when no candidate finishes.
                    }
                }
            }
            if (fullyPlaced && draft != null && warnings.LastOrDefault()?.Difficulty == target.Difficulty &&
                mode == "best-guess" && entry["edits"] == null &&
                entry["patternModel"] == null &&
                (entry["model"]?.GetValue<string>() ?? "nested-priority-v1") == "nested-priority-v1" &&
                adherence > .5)
            {
                var level = prepared.Level;
                var clock = level.Clock;
                bool variable = clock.Regions.Count != 1 || clock.Regions[0].SecondsPerBeat != 60 / level.Bpm;
                int warningIndex = warnings.Count - 1;
                var baseAudit = await Audit(foundation.Gameplay.Notes, foundation.Gameplay.Bombs, token);
                var inherited = Errors(baseAudit);
                var fixedAnchors = level.Gameplay.ReadLinks()
                    .Where(link => !prepared.Links.Optional.Contains(link.Id))
                    .SelectMany(link => link.AnchorIds);
                var protectedIds = prepared.Context.ProtectedIds.Concat(fixedAnchors).ToHashSet();
                var resetIds = MappingIntent.Detect(generated.Notes, level.Gameplay.Bombs, clock, pattern.Anchors).ResetNotes();
                resetIds.UnionWith(prepared.ResetIds);
                protectedIds.UnionWith(resetIds);
                var best = draft;
                var previous = Errors(best.Audit);
                // Keep both sides of a surviving authored parity break fixed.
                foreach (var finding in previous.Intersect(inherited))
                    protectedIds.UnionWith(best.Notes.Where(note => note.Hand == finding.Hand && note.Beat <= finding.Beat)
                        .GroupBy(note => note.Beat).OrderByDescending(group => group.Key).Take(2)
                        .SelectMany(group => group.Select(note => note.Id)));
                double searchSeconds = 28 * Math.Clamp((adherence - .5) / .45, 0, 1);
                var baselineById = approved.ToDictionary(note => note.Id);
                var reasons = new Dictionary<(double Beat, int Hand), string>();
                var directionBlockers = new HashSet<(double Beat, int Hand)>();
                var authoredDoubles = NestedTimings.Group(foundation.Gameplay.Notes)
                    .Where(attack => attack.Notes.Select(note => note.Hand).Distinct().Count() == 2)
                    .Select(attack => attack.Beat).ToHashSet();
                await Search(2, previous.Except(inherited), searchSeconds, quick: true);
                await Search(4, previous.Except(inherited).OrderBy(item => item.Beat).ThenBy(item => item.Hand), searchSeconds);
                int beforeBlockerSearch = previous.Count;
                await Search(8, directionBlockers.Where(previous.Contains).OrderBy(item => item.Beat)
                    .ThenBy(item => item.Hand), searchSeconds);
                if (previous.Count < beforeBlockerSearch)
                {
                    await Search(2, previous.Except(inherited), searchSeconds, quick: true);
                    await Search(4, previous.Except(inherited).OrderBy(item => item.Beat).ThenBy(item => item.Hand),
                        searchSeconds, reusePlacement: true);
                }
                var remainingQuickTargets = previous.Except(inherited).ToArray();
                if (remainingQuickTargets.Length is > 0 and <= 6)
                    await Search(2, remainingQuickTargets, searchSeconds, quick: true);

                async Task Search(int radius, IEnumerable<(double Beat, int Hand)> targets, double seconds,
                    bool reusePlacement = false, bool quick = false)
                {
                    using var searchBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    searchBudget.CancelAfter(TimeSpan.FromSeconds(seconds));
                    var searchToken = searchBudget.Token;
                    try
                    {
                        foreach (var finding in targets.OrderBy(item => item.Beat).ThenBy(item => item.Hand)
                            .Take(quick ? int.MaxValue : radius == 4 ? 16 : 3).ToArray())
                        {
                            if (!previous.Contains(finding))
                                continue;
                            bool wide = radius > 4;
                            var options = new List<(ParityRhythmOptions.Option Option, int Errors)>();
                            var setupDoubles = new HashSet<ParityRhythmOptions.Option>();
                            var existingIds = best.Notes.Select(note => note.Id).ToHashSet();
                            if (wide)
                                options.Add((new ParityRhythmOptions.Option(best.Notes, 0, 0), previous.Count));
                            var previousRaw = Errors(best.Audit, includeDrumming: true);
                            reasons[finding] = "Search interrupted";
                            int screened = 0;
                            var attacks = NestedTimings.Group(best.Notes);
                            double first = attacks.Where(attack => attack.Beat < finding.Beat).TakeLast(radius)
                                .Select(attack => attack.Beat).DefaultIfEmpty(finding.Beat).First();
                            double last = attacks.Where(attack => attack.Beat > finding.Beat).Take(radius)
                                .Select(attack => attack.Beat).DefaultIfEmpty(finding.Beat).Last();
                            var candidates = quick
                                ? ParityRhythmOptions.PrecedingAttackEdits(best.Notes, finding.Beat, finding.Hand, protectedIds,
                                    level.Gameplay.Notes.Max(note => note.Id), foundation.Gameplay.Notes)
                                : ParityRhythmOptions.Around(prepared, best.Notes, finding.Beat,
                                finding.Hand, protectedIds, resetIds, windowRadius: radius)
                                .GroupBy(option => option.EditCount).SelectMany(group => group
                                    .OrderBy(option => option.Cost).Take(!wide && group.Key == 1 ? 20 : 18));
                            foreach (var option in candidates)
                            {
                                screened++;
                                var findings = Errors(await Audit(option.Notes, level.Gameplay.Bombs, searchToken));
                                if (wide || !findings.Contains(finding) || option.EditCount == 1)
                                {
                                    options.Add((option, findings.Count(previous.Contains)));
                                    if (wide && !findings.Contains(finding) &&
                                        option.Notes.Count == best.Notes.Count + option.EditCount &&
                                        option.Notes.Where(note => !existingIds.Contains(note.Id))
                                            .All(note => level.Gameplay.Bombs.Any(bomb => Math.Abs(bomb.Beat - note.Beat) <= .0001)))
                                        setupDoubles.Add(option);
                                }
                            }
                            reasons[finding] = screened == 0 ? "No eligible local edits" :
                                options.Count == 0 ? "No candidate improved parity while preserving inherited findings" :
                                "Placement search interrupted";
                            foreach (var (option, _) in options.OrderBy(item => setupDoubles.Contains(item.Option) ? 0 :
                                    item.Option.EditCount == 0 ? 1 : 2)
                                .ThenBy(item => item.Errors)
                                .ThenByDescending(item => NestedTimings.Group(item.Option.Notes).Count(attack =>
                                    authoredDoubles.Contains(attack.Beat) &&
                                    attack.Notes.Select(note => note.Hand).Distinct().Count() == 2))
                                .ThenBy(item => item.Option.Cost).Take(quick ? 6 : wide ? 6 : 3))
                            {
                                var rhythm = option.Notes.Select(note =>
                                    baselineById.GetValueOrDefault(note.Id, note) with { Hand = note.Hand }).ToArray();
                                var optionPattern = PatternPreparation.Prepare(foundation, prepared, rhythm);
                                var settled = option.Notes.ToDictionary(note => note.Id);
                                var trialSeeds = optionPattern.Seeds.Select(seed => optionPattern.Anchors.Contains(seed.Note.Id)
                                    ? seed.Note : settled[seed.Note.Id]).ToArray();
                                bool exactSetup = quick || setupDoubles.Contains(option);
                                if (quick && benchmarkParity)
                                    Console.Error.WriteLine($"Quick parity trial {target.Difficulty} {finding.Beat:R}/{finding.Hand}: " +
                                        string.Join(", ", option.Notes.Where(note => !existingIds.Contains(note.Id))
                                            .Select(note => FormattableString.Invariant($"{note.Beat:R}/{note.Hand}/{note.X}/{note.Y}/{note.Direction}"))));
                                var fixedOutside = wide || reusePlacement || quick ? trialSeeds
                                    .Where(note => !optionPattern.Anchors.Contains(note.Id) &&
                                        (exactSetup || clock.Gap(note.Beat, first) > .30 || clock.Gap(last, note.Beat) > .30))
                                    .Select(note => note.Id).ToHashSet() : null;
                                if (!quick && exactSetup && benchmarkParity)
                                    Console.Error.WriteLine($"Testing bomb double setup at {string.Join(", ", option.Notes
                                        .Where(note => !existingIds.Contains(note.Id)).Select(note => note.Beat))} for {finding.Beat}.");
                                PlacementDraft placed = null;
                                bool sawSeeds = false;
                                using var quickTrial = quick ? CancellationTokenSource.CreateLinkedTokenSource(searchToken) : null;
                                quickTrial?.CancelAfter(TimeSpan.FromSeconds(1.5));
                                try
                                {
                                    (IReadOnlyList<Note> Notes, ISet<ChartLinkId> Released) result;
                                    try
                                    {
                                        result = await PlaceTrial(fixedOutside);
                                    }
                                    catch (PlacementDeadEnd deadEnd) when (quick &&
                                        deadEnd.Reason is "Bounded placement budget exhausted" or "No joint geometry" or "No source-relative geometry")
                                    {
                                        // The quick pass pins the candidate's cells. If those cells cannot
                                        // join the approach, reopen only the local, unprotected notes.
                                        var boundary = fixedOutside.Where(id => protectedIds.Contains(id) ||
                                            Math.Abs(clock.Gap(settled[id].Beat, deadEnd.Beat)) > .75).ToHashSet();
                                        if (boundary.Count == fixedOutside.Count)
                                            throw;
                                        placed = null;
                                        sawSeeds = false;
                                        quickTrial.CancelAfter(TimeSpan.FromSeconds(2));
                                        result = await PlaceTrial(boundary);
                                    }
                                    placed = new PlacementDraft(result.Notes, result.Released,
                                        parityAudits.TryGetValue(result.Notes, out var validatedAudit)
                                            ? validatedAudit : await Audit(result.Notes, level.Gameplay.Bombs, searchToken));

                                    Task<(IReadOnlyList<Note> Notes, ISet<ChartLinkId> Released)> PlaceTrial(ISet<int> pinned) =>
                                        NativeChartGenerator.Generate(prepared, optionPattern,
                                        trialSeeds, profile, speed,
                                        halfJump * 60 / level.Bpm, runtime, quickTrial?.Token ?? searchToken, draft: candidate =>
                                        {
                                            if (sawSeeds) placed = candidate;
                                            sawSeeds = true;
                                        },
                                        foundation: foundation, defaultAdherence: defaultAdherence, parityReference: parityReference,
                                        fixedOutsideWindow: pinned, audited: RecordAudit);
                                }
                                catch (OperationCanceledException) when (quickTrial?.IsCancellationRequested == true &&
                                    !searchToken.IsCancellationRequested)
                                {
                                    reasons[finding] = "Quick double placement budget exhausted";
                                    if (benchmarkParity)
                                        Console.Error.WriteLine($"Quick parity trial {finding.Beat:R}: placement budget exhausted.");
                                    continue;
                                }
                                catch (PlacementDeadEnd failure) when (failure.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal))
                                {
                                    // The candidate passed geometry and reset checks before its parity finding.
                                }
                                catch (Exception error) when (error is PlacementDeadEnd or DirectionPathFailure)
                                {
                                    reasons[finding] = error.Message;
                                    if (quick && benchmarkParity)
                                        Console.Error.WriteLine($"Quick parity trial {finding.Beat:R}: {error.Message}");
                                    if (error is DirectionPathFailure)
                                    {
                                        var match = System.Text.RegularExpressions.Regex.Match(error.Message,
                                            @"at beat (\S+) \((left|right) hand\)");
                                        if (match.Success && double.TryParse(match.Groups[1].Value,
                                            System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out double blockedBeat))
                                        {
                                            int hand = match.Groups[2].Value == "left" ? 0 : 1;
                                            directionBlockers.UnionWith(previous.Where(item =>
                                                item.Hand == hand && Math.Abs(item.Beat - blockedBeat) <= .0001));
                                        }
                                    }
                                    continue;
                                }
                                if (placed == null)
                                    continue;
                                try
                                {
                                    level.Gameplay.WriteWithLinks(placed.Notes, prepared.Links.Optional, placed.Released);
                                }
                                catch (InvalidDataException)
                                {
                                    reasons[finding] = "Retained link validation rejected the candidate";
                                    continue;
                                }
                                var findings = Errors(placed.Audit);
                                var placedById = placed.Notes.ToDictionary(note => note.Id);
                                string rejection = findings.Contains(finding) ? "Target parity error remained" :
                                    !findings.IsSubsetOf(previous) ? "Candidate introduced new parity errors" :
                                    !previousRaw.Where(inherited.Contains).All(Errors(placed.Audit, includeDrumming: true).Contains)
                                        ? "Candidate changed an inherited parity finding" :
                                    best.Notes.Where(note => protectedIds.Contains(note.Id))
                                        .Any(note => !placedById.TryGetValue(note.Id, out var output) || output != note)
                                        ? "Candidate changed a protected note" : null;
                                if (rejection != null)
                                {
                                    reasons[finding] = rejection;
                                    if (quick && benchmarkParity)
                                        Console.Error.WriteLine($"Quick parity trial {finding.Beat:R}: {rejection}");
                                    continue;
                                }
                                approved = rhythm;
                                baselineById = approved.ToDictionary(note => note.Id);
                                generated = (placed.Notes, placed.Released);
                                best = placed;
                                previous = findings;
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (searchBudget.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        // Keep the best completed alternative.
                    }
                }
                if (best != draft || reasons.Count > 0)
                {
                    var remaining = previous.Except(inherited).OrderBy(item => item.Beat).ThenBy(item => item.Hand).ToArray();
                    if (remaining.Length == 0)
                        warnings.RemoveAt(warningIndex);
                    else
                        warnings[warningIndex] = new DraftWarning(target.Difficulty, "Parity findings remain after rhythm repair.",
                            remaining[0].Beat, false, remaining.Select(item => new DraftFinding(item.Beat,
                                item.Hand == 0 ? "Left" : "Right", "error",
                                reasons.GetValueOrDefault(item, "Search budget or finding limit reached"))).ToArray());
                }

                Task<JsonObject> Audit(IReadOnlyList<Note> notes, IReadOnlyList<Bomb> bombs, CancellationToken cancellation) =>
                    PlacementParity.Audit(runtime, notes, bombs, Array.Empty<ParityReentry>(), level.Bpm,
                        clock.Seconds, variable, cancellation);

                static HashSet<(double Beat, int Hand)> Errors(JsonObject report, bool includeDrumming = false) => report["issues"].AsArray()
                    .Where(issue => issue["status"].GetValue<string>() == "error" &&
                        (includeDrumming || issue["allowedDrumming"]?.GetValue<bool>() != true))
                    .Select(issue => (StandardSong.Number(issue["beat"]), issue["hand"].GetValue<int>())).ToHashSet();
            }
            var (chart, _, _) = prepared.Level.Gameplay.WriteWithLinks(generated.Notes, prepared.Links.Optional, generated.Released);
            StageChart.Write(transaction.Staging, prepared.Level, foundation, target.Difficulty, chart, speed, offset, approved);
            if (benchmarkParity)
            {
                var level = prepared.Level;
                var final = parityAudits.TryGetValue(generated.Notes, out var validated)
                    ? validated
                    : await AuditChart(level, generated.Notes);
                var baseline = await AuditChart(foundation, foundation.Gameplay.Notes);
                File.WriteAllText(Path.Combine(reports, target.Difficulty + "-parity-audit.json"),
                    JsonSerializer.Serialize(new { baseline = baseline["issues"], final = final["issues"],
                        placementAudited = validated != null }));

                Task<JsonObject> AuditChart(StandardLevel chartLevel, IReadOnlyList<Note> notes) =>
                    PlacementParity.Audit(runtime, notes, chartLevel.Gameplay.Bombs, Array.Empty<ParityReentry>(),
                        chartLevel.Bpm, chartLevel.Clock.Seconds,
                        chartLevel.Clock.Regions.Count != 1 || chartLevel.Clock.Regions[0].SecondsPerBeat != 60 / chartLevel.Bpm, token);
            }

            bool Survives(double beat) => transaction.Completed.All(rank => NestedTimings.Nearest(beat, saved[rank]) >= 0);

        }
    }

    internal sealed record DraftWarning(string Difficulty, string Failure, double? Beat,
        bool ReviewEntireChart, DraftFinding[] ParityFindings);

    internal sealed record DraftFinding(double Beat, string Hand, string Status, string Reason = null);

    internal static (double Speed, double Offset, double HalfJump) JumpSettings(StandardSong song, double bpm,
        string target, JsonObject supplied)
    {
        double reference = double.NaN;
        if (song.SavedTimings().ContainsKey("ExpertPlus"))
        {
            var meta = song.Read("ExpertPlus").Metadata;
            reference = StandardSong.Number(meta["noteJumpMovementSpeed"] ?? meta["_noteJumpMovementSpeed"]);
        }
        double speed = target switch
        {
            "Hard" => reference >= 10 ? Math.Floor(2 * (10 + (reference - 10) / 3)) / 2 : 12,
            "Expert" => reference >= 10 ? Math.Floor(2 * (10 + 2 * (reference - 10) / 3)) / 2 : 14,
            _ => 10,
        };
        double secondsPerBeat = 60 / bpm, baseHalf = 4;
        while (baseHalf > 1 && speed * secondsPerBeat * baseHalf > 17.999)
            baseHalf /= 2;
        double unit = .5 * speed * secondsPerBeat;
        double minimum = Math.Max(1, Math.Ceiling(20 / unit - 1e-9)), maximum = Math.Floor(32 / unit + 1e-9);
        if (minimum > maximum)
            throw new InvalidDataException("No quarter-beat jump offset fits the required distance.");
        double half = Math.Clamp(Math.Round((target == "Expert" ? 25 : 27) / unit), minimum, maximum) / 4;
        double offset = half - baseHalf;
        if (supplied != null && (supplied["njs"].GetValue<double>() != speed ||
            supplied["offset"].GetValue<double>() != offset || supplied["halfJumpDuration"].GetValue<double>() != half))
            throw new InvalidDataException("Jump settings changed since the request was approved.");
        return (speed, offset, half);
    }

    internal static void ApplySettings(JsonObject profile, JsonObject settings)
    {
        if (settings == null)
            return;
        foreach (var (key, node) in settings)
        {
            double value = StandardSong.Number(node);
            double maximum = key == "diagonalPreference" ? 10 : 1;
            if (!double.IsFinite(value) || value < 0 || value > maximum)
                throw new InvalidDataException($"Invalid generator setting {key}.");
            switch (key)
            {
                case "height": case "width": case "crossover":
                    profile["geometry"][key] = value;
                    break;
                case "parityAdherence":
                    profile[key] = value;
                    break;
                case "rampAmount":
                    profile["difficultyRamp"]["enabled"] = value > 0;
                    profile["difficultyRamp"]["amount"] = value;
                    break;
                case "diagonalPreference":
                    profile[key] = value;
                    break;
                default: throw new InvalidDataException($"Unsupported generator setting {key}.");
            }
        }
    }
}
