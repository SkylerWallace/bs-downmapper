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
            bool fullyPlaced = false;
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
                    RecordDraft, foundation, defaultAdherence);
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
            }
            if (fullyPlaced && draft != null && warnings.LastOrDefault()?.Difficulty == target.Difficulty &&
                mode == "best-guess" && entry["edits"] == null &&
                entry["patternModel"] == null &&
                (entry["model"]?.GetValue<string>() ?? "nested-priority-v1") == "nested-priority-v1" &&
                adherence >= .95)
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
                using var searchBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                searchBudget.CancelAfter(TimeSpan.FromSeconds(28));
                var searchToken = searchBudget.Token;
                var baselineById = approved.ToDictionary(note => note.Id);
                var reasons = new Dictionary<(double Beat, int Hand), string>();
                try
                {
                    foreach (var finding in previous.Except(inherited).OrderBy(item => item.Beat).ThenBy(item => item.Hand).Take(16).ToArray())
                    {
                        if (!previous.Contains(finding))
                            continue;
                        var options = new List<(ParityRhythmOptions.Option Option, int Errors)>();
                        reasons[finding] = "Search interrupted";
                        int screened = 0;
                        foreach (var option in ParityRhythmOptions.Around(prepared, best.Notes, finding.Beat, finding.Hand, protectedIds, resetIds)
                            .GroupBy(option => option.EditCount).SelectMany(group =>
                                group.OrderBy(option => option.Cost).Take(group.Key == 1 ? 20 : 18)))
                        {
                            screened++;
                            var findings = Errors(await Audit(option.Notes, level.Gameplay.Bombs, searchToken));
                            if (!findings.Contains(finding) && findings.IsSubsetOf(previous) &&
                                previous.Where(inherited.Contains).All(findings.Contains))
                                options.Add((option, findings.Count));
                        }
                        reasons[finding] = screened == 0 ? "No eligible local edits" :
                            options.Count == 0 ? "No candidate improved parity while preserving inherited findings" :
                            "Placement search interrupted";
                        foreach (var (option, _) in options.OrderBy(item => item.Errors).ThenBy(item => item.Option.Cost).Take(3))
                        {
                            var rhythm = option.Notes.Select(note => baselineById.GetValueOrDefault(note.Id, note)).ToArray();
                            var optionPattern = PatternPreparation.Prepare(foundation, prepared, rhythm);
                            var settled = option.Notes.ToDictionary(note => note.Id);
                            var trialSeeds = optionPattern.Seeds.Select(seed => optionPattern.Anchors.Contains(seed.Note.Id)
                                ? seed.Note : settled[seed.Note.Id]).ToArray();
                            PlacementDraft placed = null;
                            bool sawSeeds = false;
                            try
                            {
                                var result = await NativeChartGenerator.Generate(prepared, optionPattern,
                                    trialSeeds, profile, speed,
                                    halfJump * 60 / level.Bpm, runtime, searchToken, draft: candidate =>
                                    {
                                        if (sawSeeds) placed = candidate;
                                        sawSeeds = true;
                                    },
                                    foundation: foundation, defaultAdherence: defaultAdherence);
                                placed = new PlacementDraft(result.Notes, result.Released,
                                    await Audit(result.Notes, level.Gameplay.Bombs, searchToken));
                            }
                            catch (PlacementDeadEnd failure) when (failure.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal))
                            {
                                // The candidate passed geometry and reset checks before its parity finding.
                            }
                            catch (Exception error) when (error is PlacementDeadEnd or DirectionPathFailure)
                            {
                                reasons[finding] = error.Message;
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
                            reasons[finding] = "Placed candidate failed parity or protected-note checks";
                            if (findings.Contains(finding) || !findings.IsSubsetOf(previous) ||
                                !previous.Where(inherited.Contains).All(findings.Contains) ||
                                best.Notes.Where(note => protectedIds.Contains(note.Id))
                                    .Any(note => !placedById.TryGetValue(note.Id, out var output) || output != note))
                                continue;
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

                static HashSet<(double Beat, int Hand)> Errors(JsonObject report) => report["issues"].AsArray()
                    .Where(issue => issue["status"].GetValue<string>() == "error")
                    .Select(issue => (StandardSong.Number(issue["beat"]), issue["hand"].GetValue<int>())).ToHashSet();
            }
            var (chart, _, _) = prepared.Level.Gameplay.WriteWithLinks(generated.Notes, prepared.Links.Optional, generated.Released);
            StageChart.Write(transaction.Staging, prepared.Level, foundation, target.Difficulty, chart, speed, offset, approved);

            bool Survives(double beat) => transaction.Completed.All(rank => NestedTimings.Nearest(beat, saved[rank]) >= 0);
        }
    }

    internal sealed record DraftWarning(string Difficulty, string Failure, double? Beat,
        bool ReviewEntireChart, DraftFinding[] ParityFindings);

    internal sealed record DraftFinding(double Beat, string Hand, string Status, string Reason = null);

    private static (double Speed, double Offset, double HalfJump) JumpSettings(StandardSong song, double bpm,
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

    private static void ApplySettings(JsonObject profile, JsonObject settings)
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
