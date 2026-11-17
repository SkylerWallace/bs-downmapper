using System.Text.Json;
using System.Text.Json.Nodes;
using BsDownmap.Engine;

internal static class EditorPreview
{
    private const string Nested = "nested-priority-v1";

    internal sealed record Selection(PreparedTiming Prepared, IReadOnlyList<Note> Notes);

    // A spread stages all targets together. Its highest source chart and audio stay fixed.
    internal sealed class RankCache
    {
        private readonly Dictionary<(string Path, bool MatchRepeats), NativeReferencePlan> plans = new();

        internal NativeReferencePlan Get(StandardLevel reference, bool matchRepeats, Func<NativeReferencePlan> create)
        {
            var key = (Path.Combine(reference.Folder, reference.Filename), matchRepeats);
            if (!plans.TryGetValue(key, out var plan))
                plans.Add(key, plan = create());
            return plan;
        }
    }

    internal static async Task<Selection> Select(JsonObject request,
        IReadOnlyDictionary<string, double[]> savedOverride = null, RankCache cache = null,
        Action<float, string> progress = null)
    {
        string folder = Required(request, "source");
        string target = Required(request, "difficulty");
        string mode = Required(request, "rhythm-mode");
        string root = Required(request, "engine");
        string model = request["timing-model"]?.GetValue<string>() ?? Nested;
        var song = new StandardSong(folder);
        int targetRank = Array.IndexOf(TimingPreparation.Standard.ToArray(), target);
        if (targetRank <= 0)
            throw new InvalidDataException("Choose a Standard target below Expert+.");
        var saved = savedOverride ?? song.SavedTimings();
        var higher = TimingPreparation.Standard.Take(targetRank).Where(saved.ContainsKey)
            .ToDictionary(rank => rank, rank => saved[rank]);
        bool automatic = mode == "best-guess";
        bool nested = model == Nested;
        string source = request["rhythm-difficulty"]?.GetValue<string>() ??
            (automatic ? (nested ? higher.Keys.FirstOrDefault() : higher.Keys.LastOrDefault()) : target);
        if (automatic && (source == null || !higher.ContainsKey(source)))
            throw new InvalidDataException("Choose a saved Standard timing source above the target difficulty.");
        var level = song.Read(source);
        var profiles = TimingPreparation.Standard.Skip(1).ToDictionary(rank => rank, rank =>
            ReadObject(Path.Combine(root, "profiles", rank.ToLowerInvariant() + ".json")));
        var prepared = TimingPreparation.Prepare(level, profiles[target], automatic ? higher : null,
            automatic, preserveSourceCues: automatic && nested && source == higher.Keys.First());
        progress?.Invoke(.30f, automatic ? "Selecting timings" : "Reading saved timings");
        var original = prepared.Context.Notes;
        IReadOnlyList<Note> selected = original;
        var required = TimingPreparation.Standard.Skip(targetRank + 1).Where(saved.ContainsKey)
            .SelectMany(rank => saved[rank]).Distinct().ToArray();
        if (automatic && nested)
        {
            var reference = source == higher.Keys.First() ? level : song.Read(higher.Keys.First());
            bool matchRepeats = request["no-rhythm-links"]?.GetValue<bool>() != true;
            var priority = cache == null ? CreatePlan() : cache.Get(reference, matchRepeats, CreatePlan);
            double? fraction = request["target-note-fraction"]?.GetValue<double>();
            selected = TimingPreparation.Select(prepared, priority, target, required, fraction);
            double adherence = StandardSong.Number(request["parity-adherence"] ?? profiles[target]["parityAdherence"]);
            if (!double.IsFinite(adherence) || adherence < 0 || adherence > 1)
                throw new InvalidDataException("Parity adherence must be between zero and one.");
            var runtime = new DirectionRuntime(Path.Combine(root, ".tools", "quickjs-runtime"));
            selected = await TimingParityRepair.RestoreAuthoredSwings(prepared, selected, runtime, adherence: adherence,
                repairHandAssignments: request["repair-hand-assignments"]?.GetValue<bool>() == true);
            double alternation = StandardSong.Number(request["hand-alternation"] ?? profiles[target]["handAlternation"]);
            double maximumGap = double.IsFinite(alternation) ? Math.Clamp(alternation, 0, 1.5) : 0;
            bool comparePlacement = maximumGap > 0 && adherence >= 1 &&
                request["skip-placed-timing-comparison"]?.GetValue<bool>() != true;
            if (maximumGap > 0 && !comparePlacement)
                selected = adherence >= 1
                    ? await TimingParityRepair.AlternateHandsWhenParitySafe(prepared, selected, maximumGap, runtime)
                    : TimingParityRepair.AlternateHands(prepared, selected, maximumGap);
            if (request["placement-hand-fallback"]?.GetValue<bool>() == true)
            {
                var groups = NestedTimings.Group(original);
                var links = TimelineRepeats.Find(level, original).Sections.SelectMany(section => section.Links).ToArray();
                var beats = groups.Select(group => group.Beat).ToArray();
                var kept = selected.Select(note => NestedTimings.Nearest(note.Beat, beats)).ToHashSet();
                var additions = new List<Note>();
                int previousCount;
                do
                {
                    previousCount = kept.Count;
                    foreach (var link in links.Where(link => link.Any(kept.Contains)))
                        foreach (int index in link)
                            if (kept.Add(index))
                                additions.Add(groups[index].Notes[0]);
                } while (previousCount != kept.Count);
                selected = selected.Concat(additions).OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();
            }

            if (comparePlacement)
            {
                progress?.Invoke(.48f, "Comparing placed rhythm proposals");
                var profile = (JsonObject)profiles[target].DeepClone();
                double defaultAdherence = StandardSong.Number(profile["parityAdherence"]);
                var settings = request["placement-settings"] as JsonObject ??
                    (request["placement-settings"] is JsonValue path ? ReadObject(path.GetValue<string>()) : null);
                EditorGeneration.ApplySettings(profile, settings);
                profile["parityAdherence"] = adherence;
                var foundation = song.Read(request["source-difficulty"]?.GetValue<string>() ?? higher.Keys.First());
                var (speed, _, halfJump) = EditorGeneration.JumpSettings(song, level.Bpm, target, null);
                selected = await EditorTimingPlacement.Compare(prepared, selected, foundation, song.Read(higher.Keys.Last()),
                    profile, defaultAdherence, maximumGap, speed, halfJump * 60 / level.Bpm, runtime, matchRepeats);
            }

            NativeReferencePlan CreatePlan()
            {
                using var asset = File.OpenRead(Required(request, "ranker"));
                using var audio = new MixAudio(Required(request, "decoder"));
                return TimingPreparation.Rank(reference, profiles, TreeEnsemble.Load(asset), audio, matchRepeats);
            }
        }
        else if (automatic)
        {
            var (package, manifest) = FindModel(Required(request, "models"), "timing", model);
            var reference = song.Read(higher.Keys.First());
            double? fraction = request["target-note-fraction"]?.GetValue<double>();
            int suggestedCount = fraction.HasValue
                ? Math.Min(original.Count, TimingPreparation.BudgetFromFraction(reference.Gameplay.Notes.Count, fraction.Value))
                : TimingPreparation.Seed(prepared, profiles[target], reference.Gameplay.Notes).Count;
            var context = JsonSerializer.SerializeToNode(new
            {
                level = ModelLevel(prepared),
                difficulty = target,
                suggested_count = suggestedCount,
                protected_ids = prepared.Context.ProtectedIds,
                required_beats = required,
                reference = ModelLevel(TimingPreparation.Prepare(reference, profiles[target])),
            }).AsObject();
            var settings = request["timing-model-settings"] is JsonValue options ? ReadObject(options.GetValue<string>()) : new JsonObject();
            var result = await CustomModelHost.Invoke(package, manifest, context, settings,
                request["python"]?.GetValue<string>(), Path.Combine(root, "tools", "custom_model_adapter.py"), CancellationToken.None);
            selected = CustomModelHost.ApplyTimingResult(result, original, prepared.Context.ProtectedIds, required);
        }

        return new Selection(prepared, selected);
    }

    internal static async Task<object> Run(JsonObject request, Action<float, string> progress = null)
    {
        progress?.Invoke(.05f, "Reading chart");
        var (prepared, selected) = await Select(request, progress: progress);
        progress?.Invoke(.65f, "Finding repeated sections");
        var level = prepared.Level;
        var available = prepared.Context.Notes;
        var sourceIds = available.Select(note => note.Id).ToHashSet();
        var original = available.Concat(selected.Where(note => !sourceIds.Contains(note.Id)))
            .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();
        var selectedIds = selected.Select(note => note.Id).ToHashSet();
        var repeats = TimelineRepeats.Find(level, original);
        var links = level.Gameplay.ReadLinks();
        int subsectionGroup = 0;
        var subSections = repeats.Subsections.SelectMany((item, scope) => item.Groups.SelectMany(group =>
        {
            int index = subsectionGroup++;
            return group.Occurrences.Select(window =>
                new { start = window.Start, end = window.End, group = index, scope });
        })).ToArray();
        int dropped = prepared.Links.Released.Count + links.Count(link => prepared.Links.Optional.Contains(link.Id) &&
            !link.AnchorIds.IsSubsetOf(selectedIds));
        progress?.Invoke(.90f, "Preparing timeline");
        return new
        {
            endBeat = level.EndBeat,
            regions = level.Clock.Regions.Select(region => new { beat = region.Beat, seconds = region.Seconds, secondsPerBeat = region.SecondsPerBeat }),
            timings = NestedTimings.Group(original).Select(group => new
            {
                beat = group.Notes[0].Beat,
                count = group.Notes.Count,
                locked = group.Notes.Any(note => prepared.Links.LockedAnchors.Contains(note.Id)),
                hands = group.Notes.Select(note => note.Hand),
                noteIds = group.Notes.Select(note => note.Id),
            }),
            sourceSha256 = EngineFiles.HashFile(Path.Combine(level.Folder, level.Filename)).ToLowerInvariant(),
            rhythmDifficulty = level.Difficulty,
            removedNoteIds = original.Where(note => !selectedIds.Contains(note.Id)).Select(note => note.Id),
            proposedHandIds = selected.Where(note => !sourceIds.Contains(note.Id)).Select(note => note.Id),
            simplifiedLinkCount = dropped,
            merges = Array.Empty<object>(),
            sections = repeats.Sections.SelectMany((group, index) => group.Occurrences.Select(window =>
                new { start = window.Start, end = window.End, group = index })),
            markerLinks = repeats.Sections.SelectMany(group => group.Links),
            subScopes = repeats.Subsections.Select(item => new { start = item.Scope.Start, end = item.Scope.End }),
            subSections,
            subMarkerLinks = repeats.Subsections.SelectMany((item, scope) => item.Groups.SelectMany(group =>
                group.Links.Select(markers => new { scope, markers }))),
        };
    }

    internal static object ModelLevel(PreparedTiming prepared)
    {
        var level = prepared.Level;
        double njs = StandardSong.Number(level.Metadata["noteJumpMovementSpeed"] ?? level.Metadata["_noteJumpMovementSpeed"]);
        double offset = StandardSong.Number(level.Metadata["noteJumpStartBeatOffset"] ?? level.Metadata["_noteJumpStartBeatOffset"]);
        if (!double.IsFinite(njs) || njs <= 0)
            njs = level.Difficulty switch { "ExpertPlus" => 16, "Expert" => 14, "Hard" => 12, _ => 10 };
        return new
        {
            folder = level.Folder, filename = level.Filename, raw = level.Chart, info = level.Info, meta = level.Metadata,
            bpm = level.Bpm, audioData = level.AudioData, njs, offset = double.IsFinite(offset) ? offset : 0,
            version = (level.Chart["version"] ?? level.Chart["_version"])?.GetValue<string>(),
            clock = ModelClock(level.Clock), legacyGridClock = level.LegacyGrid == null ? null : ModelClock(level.LegacyGrid),
            notes = prepared.Context.Notes.Select(ModelNote),
            bombs = level.Gameplay.Bombs.Select(bomb => new { b = bomb.Beat, x = bomb.X, y = bomb.Y }),
            walls = level.Gameplay.Walls.Select(wall => new { b = wall.Beat, x = wall.X, y = wall.Y, d = wall.Duration, w = wall.Width, h = wall.Height }),
            anchorIds = prepared.Links.LockedAnchors,
            linkedEndpoints = prepared.Links.Endpoints.Select(point => new[] { point.Beat, point.Hand, point.X, point.Y }),
        };
    }

    internal static object ModelNote(Note note) => new
    {
        id = note.Id, b = note.Beat, c = note.Hand, x = note.X, y = note.Y, d = note.Direction, a = note.Angle,
    };

    private static object ModelClock(ChartClock clock) => new
    {
        starts = clock.Regions.Select(region => region.Beat),
        times = clock.Regions.Select(region => region.Seconds),
        rates = clock.Regions.Select(region => region.SecondsPerBeat),
    };

    internal static (string Folder, JsonObject Manifest) FindModel(string root, string kind, string id)
    {
        if (Directory.Exists(root))
            foreach (string directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
            {
                string file = Path.Combine(directory, "model.json");
                if (!File.Exists(file))
                    continue;
                var manifest = ReadObject(file);
                if (manifest["id"]?.GetValue<string>() == id && manifest["kind"]?.GetValue<string>() == kind)
                    return (directory, manifest);
            }
        throw new InvalidDataException($"Unknown custom {kind} model {id}.");
    }

    internal static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path)).AsObject();
    internal static string Required(JsonObject request, string key) => request[key]?.GetValue<string>() ??
        throw new InvalidDataException($"The preview request is missing {key}.");
}

