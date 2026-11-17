using System.Text.Json.Nodes;
using BsDownmap.Engine;

internal static class EditorTimingPlacement
{
    private sealed record Trial(IReadOnlyList<Note> Notes, HashSet<(double Beat, int Hand)> Errors, ISet<int> Protected);

    internal static async Task<IReadOnlyList<Note>> Compare(PreparedTiming prepared, IReadOnlyList<Note> rhythm,
        StandardLevel foundation, StandardLevel reference, JsonObject profile, double defaultAdherence,
        double maximumGap, double speed, double approach, DirectionRuntime runtime, bool linkRepairs)
    {
        const int maximumTrials = 9;
        var level = prepared.Level;
        var clock = level.Clock;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        bool trace = Environment.GetEnvironmentVariable("BS_DOWNMAP_BENCHMARK_PARITY") == "1";
        int trials = 0;
        var baseline = await Place(rhythm);
        if (baseline == null)
            return rhythm;

        var authored = Errors(await PlacementParity.Audit(runtime, foundation.Gameplay.Notes,
            foundation.Gameplay.Bombs, Array.Empty<ParityReentry>(), foundation.Bpm, foundation.Clock.Seconds,
            foundation.Clock.Regions.Count != 1 || foundation.Clock.Regions[0].SecondsPerBeat != 60 / foundation.Bpm,
            CancellationToken.None));
        var bestRhythm = rhythm;
        var best = baseline;
        var alternated = TimingParityRepair.AlternateHands(prepared, rhythm, maximumGap);
        if (!rhythm.SequenceEqual(alternated))
            await Consider(alternated, "automatic alternation");

        var mirrored = TimingParityRepair.AdditionalHands(prepared).Values.ToLookup(note => (note.Beat, note.Hand));
        var authoredHands = prepared.Context.Notes.ToLookup(note => (note.Beat, note.Hand));
        var tried = new HashSet<(double Beat, int Hand)>();
        foreach (var error in best.Errors.Except(authored).OrderBy(item => item.Beat).ThenBy(item => item.Hand).ToArray())
        {
            if (!best.Errors.Contains(error))
                continue;
            var protectedIds = best.Protected.Concat(prepared.Context.ProtectedIds).Concat(prepared.ResetIds).ToHashSet();
            var nearby = NestedTimings.Group(bestRhythm).Where(group =>
                group.Notes.Count == 1 && !protectedIds.Contains(group.Notes[0].Id) &&
                group.Beat <= error.Beat + .0001 && clock.Gap(group.Beat, error.Beat) <= 1.25)
                .Select(group => group.Notes[0]).Reverse().ToArray();
            var candidates = nearby.Where(note => note.Hand == error.Hand).Take(2)
                .Concat(nearby.Where(note => note.Hand != error.Hand).Take(1));
            foreach (var note in candidates)
            {
                if (trials >= maximumTrials || budget.IsCancellationRequested)
                    return bestRhythm;
                var key = (note.Beat, 1 - note.Hand);
                if (!tried.Add(key))
                    continue;
                var replacements = authoredHands[key].Concat(mirrored[key]).Take(1);
                foreach (var replacement in replacements)
                {
                    var proposal = bestRhythm.Select(item => item.Id == note.Id ? replacement : item).ToArray();
                    if (await Consider(proposal, $"swap at {note.Beat:R} for {error.Beat:R}"))
                        break;
                }
                if (!best.Errors.Contains(error))
                    break;
            }
        }
        return bestRhythm;

        async Task<bool> Consider(IReadOnlyList<Note> proposed, string label)
        {
            var result = await Place(proposed);
            bool accepted = result != null && result.Errors.IsProperSubsetOf(best.Errors) &&
                best.Errors.Where(authored.Contains).All(result.Errors.Contains);
            if (trace)
                Console.Error.WriteLine($"Placed timing {profile["name"]}: {label}: " +
                    (result == null ? "placement unavailable" : $"{best.Errors.Count} -> {result.Errors.Count} errors") +
                    (accepted ? " accepted" : " rejected"));
            if (!accepted)
                return false;
            best = result;
            bestRhythm = proposed;
            return true;
        }

        async Task<Trial> Place(IReadOnlyList<Note> notes)
        {
            if (trials++ >= maximumTrials || budget.IsCancellationRequested)
                return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(trials == 1 ? 14 : 8));
            var pattern = PatternPreparation.Prepare(foundation, prepared, notes);
            var seeds = pattern.Seeds.Select(seed => seed.Note).ToArray();
            Trial result = null;
            PlacementDraft geometryDraft = null;
            IReadOnlyList<Note> returned = null;
            ISet<ChartLinkId> released = null;
            try
            {
                (returned, released) = await NativeChartGenerator.Generate(prepared, pattern, seeds, profile, speed, approach,
                    runtime, timeout.Token, draft: candidate => geometryDraft = candidate,
                    foundation: foundation, defaultAdherence: defaultAdherence,
                    parityReference: reference, linkParityRepairs: linkRepairs,
                    audited: (placed, audit) => result = new Trial(placed, Errors(audit),
                        MappingIntent.Detect(placed, level.Gameplay.Bombs, clock, pattern.Anchors)
                            .ResetNotes().Concat(pattern.Anchors).ToHashSet()));
            }
            catch (PlacementDeadEnd error) when (error.Reason.StartsWith("Unexpected parity ", StringComparison.Ordinal))
            {
                if (result != null && ReferenceEquals(geometryDraft?.Notes, result.Notes))
                    released = geometryDraft.Released;
            }
            catch (Exception error) when (error is PlacementDeadEnd or DirectionPathFailure ||
                error is OperationCanceledException && timeout.IsCancellationRequested)
            {
                return null;
            }

            if (result == null || released == null || returned != null && !ReferenceEquals(returned, result.Notes))
                return null;
            try { level.Gameplay.WriteWithLinks(result.Notes, prepared.Links.Optional, released); }
            catch (InvalidDataException) { return null; }
            return result;
        }
    }

    private static HashSet<(double Beat, int Hand)> Errors(JsonObject audit) =>
        audit["issues"].AsArray().Where(issue => issue["status"].GetValue<string>() == "error")
            .Select(issue => (Math.Round(StandardSong.Number(issue["beat"]), 4), issue["hand"].GetValue<int>())).ToHashSet();
}
