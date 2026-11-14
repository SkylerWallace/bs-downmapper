using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BsDownmap.Engine;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
try
{
    if (args.Length != 1)
        throw new ArgumentException("Supply one engine operation.");
    var input = JsonNode.Parse(Console.In.ReadToEnd())?.AsObject() ??
        throw new InvalidDataException("The native engine needs a JSON request.");
    object response = args[0] switch
    {
        "nested-priority" => BuildPriority(input),
        "reference-priority" => ReferencePriority(input),
        "editor-preview" => await EditorPreview.Run(input),
        "editor-generate" => await EditorGeneration.Run(input),
        "publish" => Publish(input),
        "place" => Place(input),
        "custom-model" => await InvokeModel(input),
        _ => throw new ArgumentException($"Unknown engine operation {args[0]}."),
    };
    if (args[0] == "editor-preview")
        Console.Write("TIMELINE_JSON: ");
    Console.WriteLine(JsonSerializer.Serialize(response));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(args.Length == 1 && args[0].StartsWith("editor-", StringComparison.Ordinal) ?
        "DOWNMAP_ERROR: " + error.Message : error.Message);
    return 1;
}

static object BuildPriority(JsonObject input)
{
    var reference = new StandardSong(Text(input, "folder")).Read(Text(input, "difficulty"));
    var contexts = new Dictionary<string, RepeatTimingContext>();
    var seeds = new Dictionary<string, IReadOnlyList<Note>>();
    var accents = new Dictionary<string, ISet<double>>();
    foreach (var (tier, value) in input["tiers"].AsObject())
    {
        var notes = value["notes"].AsArray().Select(ReadNote).ToArray();
        var byId = notes.ToDictionary(note => note.Id);
        var protectedIds = Integers(value["protected"]).ToHashSet();
        var required = Numbers(value["required"]).ToHashSet();
        var windows = value["windows"].AsArray().Select(row =>
            new RepeatWindow(row[0].GetValue<double>(), row[1].GetValue<double>())).ToArray();
        contexts.Add(tier, new RepeatTimingContext(notes, protectedIds, required, windows));
        seeds.Add(tier, Integers(value["seed"]).Select(id => byId[id]).ToArray());
        accents.Add(tier, Numbers(value["accents"]).ToHashSet());
    }
    var sections = input["sections"].AsArray().Select(row => new DensitySection(
        row[0].GetValue<double>(), row[1].GetValue<double>(), 0, 0, 0)).ToArray();
    var streams = input["streams"].AsArray().Select(row => new AlternatingStream(0, 0, 0, Integers(row), false)).ToArray();
    var settings = input["easy"];
    using var modelFile = File.OpenRead(Text(input, "model"));
    using var audio = new MixAudio(Text(input, "decoder"));
    var plan = NestedTimingPlanner.Create(reference, TreeEnsemble.Load(modelFile), audio,
        contexts, seeds, accents, Numbers(input["endpoints"]), sections, streams,
        new EasyDraftSettings(settings["gap"].GetValue<double>(), settings["pulse"].GetValue<double>(),
            settings["accents"].GetValue<bool>(), settings["swaps"].GetValue<bool>(), settings["budget"].GetValue<bool>()),
        input["matchRepeats"].GetValue<bool>());
    return new { order = plan.Priority, budgets = plan.Budgets };
}

static object ReferencePriority(JsonObject input)
{
    var reference = new StandardSong(Text(input, "folder")).Read(Text(input, "difficulty"));
    var profiles = TimingPreparation.Standard.Skip(1).ToDictionary(rank => rank, rank =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Text(input, "engine"), "profiles",
            rank.ToLowerInvariant() + ".json"))).AsObject());
    using var modelFile = File.OpenRead(Text(input, "model"));
    using var audio = new MixAudio(Text(input, "decoder"));
    var result = TimingPreparation.Rank(reference, profiles, TreeEnsemble.Load(modelFile), audio,
        input["matchRepeats"].GetValue<bool>());
    return new { order = result.Plan.Priority, budgets = result.Plan.Budgets, mandatory = result.Mandatory };
}

static object Publish(JsonObject input)
{
    var targets = input["targets"].AsObject().ToDictionary(pair => pair.Key, pair => pair.Value.GetValue<string>());
    SpreadPublisher.Publish(Text(input, "source"), Text(input, "staged"), Text(input, "reports"), Text(input, "info"), targets);
    return new { published = true };
}

static object Place(JsonObject input)
{
    var level = new StandardSong(Text(input, "folder")).Read(Text(input, "difficulty"));
    var source = input["source"].AsArray().Select(ReadNote).ToDictionary(note => note.Id);
    var notes = input["notes"].AsArray().Select(ReadNote).ToArray();
    var rows = input["notes"].AsArray();
    var cues = new Dictionary<int, PlacementNoteCue>();
    foreach (var row in rows)
    {
        int id = row["id"].GetValue<int>();
        var intent = row["intent"] as JsonObject;
        PlacementIntent placementIntent = null;
        if (intent?["kind"] != null || intent?["preferredCell"] != null || intent?["positionWeight"] != null)
        {
            var preferred = intent["preferredCell"] as JsonArray;
            placementIntent = new PlacementIntent(intent["kind"]?.GetValue<string>(),
                preferred?[0]?.GetValue<double>() ?? source[id].X, preferred?[1]?.GetValue<double>() ?? source[id].Y,
                intent["positionWeight"]?.GetValue<double>());
        }
        int? pair = row["pairIndex"]?.GetValue<int>();
        var cue = new PlacementCue(placementIntent, pair, intent?["previousResetNoteId"]?.GetValue<int>());
        var repair = row["domainRepair"]?.GetValue<string>() switch
        {
            "clearance" => CellRepair.Clearance,
            "stack_clearance" => CellRepair.StackClearance,
            "reset_cell" => CellRepair.ResetCell,
            _ => CellRepair.None,
        };
        var allowed = (intent?["allowedCells"] as JsonArray)?.Select(cell =>
            (cell[0].GetValue<double>(), cell[1].GetValue<double>())).ToHashSet();
        double? reach = pair.HasValue ? (intent?["pairIds"] as JsonArray)?.Select(item =>
            MotionVisibility.Reach(source[item.GetValue<int>()])).Max() : null;
        cues[id] = new PlacementNoteCue(cue, repair, Flag(row, "flattenOutward"),
            Flag(intent, "preservePosition"), Flag(row, "retainOpeningDot"), allowed, reach);
    }
    var sections = input["sections"].AsArray().Select(row => new DensitySection(
        row[0].GetValue<double>(), row[1].GetValue<double>(), 0, 0, row[2].GetValue<double>())).ToArray();
    var walls = input["walls"].AsArray().Select(row => new Wall(
        Number(row, "b"), Number(row, "x"), Number(row, "y"), Number(row, "d"), Number(row, "w"), Number(row, "h"))).ToArray();
    var bombs = input["bombs"].AsArray().Select(row => new Bomb(Number(row, "b"), Number(row, "x"), Number(row, "y"))).ToArray();
    var endpoints = input["endpoints"].AsArray().Select(row => new ChartEndpoint(
        row[0].GetValue<double>(), row[1].GetValue<int>(), row[2].GetValue<double>(), row[3].GetValue<double>())).ToArray();
    var plan = PlacementPreparation.Build(notes, source, walls, bombs,
        rows.Where(row => Flag(row, "linkedAnchor")).Select(row => row["id"].GetValue<int>()).ToHashSet(),
        endpoints, cues, sections, level.Clock, level.Bpm, Number(input, "njs"), Number(input, "approach"),
        input["profile"].AsObject(), Numbers(input["repairCenters"]));
    try
    {
        return new { notes = ChartPlacement.Place(notes, plan).Select(note => new { id = note.Id, x = note.X, y = note.Y }) };
    }
    catch (PlacementDeadEnd failure)
    {
        var visibility = failure.VisibilityTarget.HasValue ?
            new { target = failure.VisibilityTarget.Value, blockers = failure.Blockers } : null;
        return new { failure = failure.Reason, beat = failure.Beat, ids = failure.Notes.Select(note => note.Id), visibility };
    }

    static bool Flag(JsonNode row, string key) => row?[key] is JsonValue flag && flag.TryGetValue<bool>(out bool value) && value;
    static double Number(JsonNode row, string key) => row[key].GetValue<double>();
}

static Task<JsonNode> InvokeModel(JsonObject input) => CustomModelHost.Invoke(
    Text(input, "package"), input["manifest"].AsObject(), input["context"].AsObject(),
    input["settings"].AsObject(), input["python"]?.GetValue<string>(), input["adapter"]?.GetValue<string>(), CancellationToken.None);

static Note ReadNote(JsonNode row) => new(row["id"].GetValue<int>(), row["b"].GetValue<double>(),
    row["c"].GetValue<int>(), row["x"].GetValue<double>(), row["y"].GetValue<double>(),
    row["d"].GetValue<double>(), row["a"]?.GetValue<double>() ?? 0);

static string Text(JsonNode row, string key) => row[key]?.GetValue<string>() ??
    throw new InvalidDataException($"The native engine request is missing {key}.");
static int[] Integers(JsonNode row) => row.AsArray().Select(node => node.GetValue<int>()).ToArray();
static double[] Numbers(JsonNode row) => row.AsArray().Select(node => node.GetValue<double>()).ToArray();
