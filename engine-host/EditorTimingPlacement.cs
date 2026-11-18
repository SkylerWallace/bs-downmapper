using System.Text.Json.Nodes;
using BsDownmap.Engine;

internal static class EditorTimingPlacement
{
    private sealed record Trial(IReadOnlyList<Note> Notes, HashSet<(double Beat, int Hand)> Errors, ISet<int> Protected);

    internal static async Task<IReadOnlyList<Note>> Compare(PreparedTiming prepared, IReadOnlyList<Note> rhythm,
        StandardLevel foundation, StandardLevel reference, JsonObject profile, double defaultAdherence,
        double maximumGap, double speed, double approach, DirectionRuntime runtime, bool linkRepairs,
        double highestLeftShare, IReadOnlyList<Note> ranked, IReadOnlyList<Note> budgetRhythm,
        IReadOnlyList<Note> rankedBudgetRhythm, IReadOnlyList<double> requiredBeats, bool explicitBudget)
    {
        const int maximumTrials = 14;
        var level = prepared.Level;
        var clock = level.Clock;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        bool trace = Environment.GetEnvironmentVariable("BS_DOWNMAP_BENCHMARK_PARITY") == "1";
        int trials = 0;
        var baseline = await Place(ranked);
        if (baseline == null)
            return rhythm;

        var authored = Errors(await PlacementParity.Audit(runtime, foundation.Gameplay.Notes,
            foundation.Gameplay.Bombs, Array.Empty<ParityReentry>(), foundation.Bpm, foundation.Clock.Seconds,
            foundation.Clock.Regions.Count != 1 || foundation.Clock.Regions[0].SecondsPerBeat != 60 / foundation.Bpm,
            CancellationToken.None));
        var bestRhythm = ranked;
        var best = baseline;
        double targetLeftShare = (TimingPreparation.LeftShare(reference.Gameplay.Notes) + highestLeftShare) / 2;
        if (!ranked.SequenceEqual(rhythm))
            await Consider(rhythm, "balanced rhythm");
        if (budgetRhythm.Count != rhythm.Count)
            await Consider(budgetRhythm, "balanced rhythm at the selected budget");
        if (rankedBudgetRhythm.Count != ranked.Count)
            await Consider(rankedBudgetRhythm, "ranked rhythm at the selected budget");
        var alternated = TimingParityRepair.AlternateHands(prepared, bestRhythm, maximumGap);
        if (!bestRhythm.SequenceEqual(alternated))
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
        if (!best.Errors.Any(error => !authored.Contains(error)))
        {
            // Keep the placed rhythm's attacks and audition small hand changes
            // when a fully rebalanced rhythm would disturb its parity.
            int wantedHand = TimingPreparation.LeftShare(bestRhythm) < targetLeftShare ? 0 : 1;
            var protectedIds = best.Protected.Concat(prepared.Context.ProtectedIds).Concat(prepared.ResetIds).ToHashSet();
            var swaps = NestedTimings.Group(bestRhythm).Where(group => group.Notes.Count == 1)
                .Select(group => group.Notes[0]).Where(note => note.Hand != wantedHand && !protectedIds.Contains(note.Id))
                .SelectMany(note => authoredHands[(note.Beat, wantedHand)].Take(1).Select(replacement => (note, replacement)))
                .Take(3).ToArray();
            foreach (var (note, replacement) in swaps)
            {
                if (trials >= maximumTrials || budget.IsCancellationRequested)
                    break;
                await Consider(bestRhythm.Select(item => item.Id == note.Id ? replacement : item).ToArray(),
                    $"balance hand at {note.Beat:R}");
            }
            if (explicitBudget && bestRhythm.Count > budgetRhythm.Count)
                await RecoverBudget();
        }
        return bestRhythm;

        async Task RecoverBudget()
        {
            var protectedIds = best.Protected.Concat(prepared.Context.ProtectedIds).Concat(prepared.ResetIds).ToHashSet();
            var placed = best.Notes.ToList();
            var rhythmById = bestRhythm.ToDictionary(note => note.Id);
            int surplusHand = TimingPreparation.LeftShare(placed) > targetLeftShare ? 0 : 1;
            var options = placed.Where(note => !protectedIds.Contains(note.Id) &&
                !level.Gameplay.Bombs.Any(bomb => Math.Abs(bomb.Beat - note.Beat) < .8)).ToArray();
            var density = options.ToDictionary(note => note.Id,
                note => placed.Count(other => Math.Abs(other.Beat - note.Beat) < 1));
            var pairs = from first in options
                        from second in options
                        where second.Hand == first.Hand && second.Beat > first.Beat && second.Beat - first.Beat <= 2.5
                        orderby first.Hand == surplusHand descending, density[first.Id] + density[second.Id] descending
                        select new[] { first, second };
            var singletons = options.OrderByDescending(note => note.Hand == surplusHand)
                .ThenByDescending(note => density[note.Id]).Select(note => new[] { note });
            try
            {
                var baselineErrors = await Audit(placed);
                int attempts = 0;
                foreach (var removal in pairs.Concat(singletons))
                {
                    int excess = placed.Count - budgetRhythm.Count;
                    if (excess == 0 || attempts >= 200 || budget.IsCancellationRequested)
                        break;
                    if (removal.Length > excess || removal.Any(note => !rhythmById.ContainsKey(note.Id)))
                        continue;
                    var ids = removal.Select(note => note.Id).ToHashSet();
                    var candidate = placed.Where(note => !ids.Contains(note.Id)).ToList();
                    if (removal.Any(note => requiredBeats.Any(beat => Math.Abs(beat - note.Beat) <= .0001) &&
                        !candidate.Any(remaining => Math.Abs(remaining.Beat - note.Beat) <= .0001)))
                        continue;
                    attempts++;
                    if (!baselineErrors.SetEquals(await Audit(candidate)))
                        continue;
                    placed = candidate;
                    foreach (var id in ids)
                        rhythmById.Remove(id);
                }
                if (placed.Count == budgetRhythm.Count)
                    await Consider(bestRhythm.Where(note => rhythmById.ContainsKey(note.Id)).ToArray(),
                        "fixed-count paired removal");
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                // Keep the last fully placed and audited rhythm.
            }

            async Task<HashSet<(double Beat, int Hand)>> Audit(IReadOnlyList<Note> notes) =>
                Errors(await PlacementParity.Audit(runtime, notes, level.Gameplay.Bombs, Array.Empty<ParityReentry>(),
                    level.Bpm, clock.Seconds, clock.Regions.Count != 1 || clock.Regions[0].SecondsPerBeat != 60 / level.Bpm,
                    budget.Token));
        }

        async Task<bool> Consider(IReadOnlyList<Note> proposed, string label)
        {
            var result = await Place(proposed);
            int beforeErrors = best.Errors.Count(error => !authored.Contains(error));
            int afterErrors = result?.Errors.Count(error => !authored.Contains(error)) ?? int.MaxValue;
            bool accepted = result != null && result.Errors.Where(authored.Contains).ToHashSet()
                .SetEquals(best.Errors.Where(authored.Contains)) &&
                (afterErrors, Math.Abs(proposed.Count - budgetRhythm.Count),
                    Math.Abs(TimingPreparation.LeftShare(proposed) - targetLeftShare))
                .CompareTo((beforeErrors, Math.Abs(bestRhythm.Count - budgetRhythm.Count),
                    Math.Abs(TimingPreparation.LeftShare(bestRhythm) - targetLeftShare))) < 0;
            if (trace)
                Console.Error.WriteLine($"Placed timing {profile["name"]}: {label}: " +
                    (result == null ? "placement unavailable" : $"{best.Errors.Count} -> {result.Errors.Count} errors") +
                    (accepted ? " accepted" : " rejected") +
                    (result == null ? "" : " [" + string.Join(", ", result.Errors.Except(authored)
                        .OrderBy(error => error.Beat).ThenBy(error => error.Hand)
                        .Select(error => $"{error.Beat:R}/{error.Hand}")) + "]"));
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
