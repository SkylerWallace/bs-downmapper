using System.Text.Json.Nodes;
using BsDownmap.Engine;

internal static class EditorParityPreview
{
    private enum Search { Nearby, SameHandLookback, RestoreTiming }
    private readonly record struct Edit(Note? Before, Note After);
    private sealed record Candidate(string Kind, double Cost, Edit[] Edits, double[] LinkedBeats = null);
    private sealed record Scope(RepeatWindow Origin, RepeatWindow[] Occurrences, int[][] Links);
    private sealed record Trial(string Kind, double Cost, double Beat, int Hand, double[] EditBeats, int Occurrences,
        double[] SectionStarts, double[] MirroredStarts, string Result, Note[] Changed,
        object[] Cleared, object[] Introduced, double HandBalanceCost, double NoteBudgetCost,
        Trial[] FollowUp, double[] LinkedBeats);

    internal static async Task<object> Run(JsonObject request)
    {
        string root = EditorPreview.Required(request, "engine");
        string difficulty = EditorPreview.Required(request, "difficulty");
        var song = new StandardSong(EditorPreview.Required(request, "source"));
        var level = song.Read(difficulty);
        var saved = song.SavedTimings();
        var higher = TimingPreparation.Standard.TakeWhile(rank => rank != difficulty).Where(saved.ContainsKey).ToArray();
        if (higher.Length == 0)
            throw new InvalidDataException("Parity preview requires a saved higher Standard chart.");
        var reference = song.Read(higher[^1]);
        var foundation = song.Read(request["foundation"]?.GetValue<string>() ?? higher[0]);
        var higherNotes = NestedTimings.Group(reference.Gameplay.Notes.Concat(foundation.Gameplay.Notes));
        var profile = EditorPreview.ReadObject(Path.Combine(root, "profiles", difficulty.ToLowerInvariant() + ".json"));
        profile["parityAdherence"] = 1;
        int referenceCount = song.Read(higher[0]).Gameplay.Notes.Count;
        double targetFraction = request["target-note-fraction"]?.GetValue<double>() ??
            (StandardSong.Number(profile["minimumSourceNoteRatio"]) +
             StandardSong.Number(profile["maximumSourceNoteRatio"])) / 2;
        int targetCount = TimingPreparation.BudgetFromFraction(referenceCount, targetFraction);
        double targetLeftShare = (double)reference.Gameplay.Notes.Count(note => note.Hand == 0) /
            reference.Gameplay.Notes.Count;
        var prepared = TimingPreparation.Prepare(level, profile, automatic: false);
        var runtime = new DirectionRuntime(Path.Combine(root, ".tools", "quickjs-runtime"));
        var (speed, _, halfJump) = EditorGeneration.JumpSettings(song, level.Bpm, difficulty, null);
        var original = prepared.Context.Notes;
        IReadOnlyList<Note> current = original;
        var links = TimelineRepeats.Find(level, original);
        var groups = NestedTimings.Group(original);
        var repeats = links.Sections.Concat(links.Subsections.SelectMany(scope => scope.Groups)).ToArray();
        bool restoreTimings = request["restore-linked-timings"]?.GetValue<bool>() != false;
        var referenceGroups = NestedTimings.Group(reference.Gameplay.Notes);
        var referenceLinks = new Lazy<ILookup<int, int[]>>(() =>
        {
            var found = TimelineRepeats.Find(reference, reference.Gameplay.Notes);
            return found.Sections.Concat(found.Subsections.SelectMany(scope => scope.Groups))
                .SelectMany(repeat => repeat.Links).SelectMany(link => link.Select(index => (index, link)))
                .ToLookup(item => item.index, item => item.link);
        });
        var pattern = PatternPreparation.Prepare(foundation, prepared, original);
        var protectedIds = pattern.Anchors.Concat(prepared.ResetIds).Concat(prepared.Context.ProtectedIds).ToHashSet();
        var returns = MappingIntent.Detect(original, level.Gameplay.Bombs, level.Clock, pattern.Anchors).Returns;
        var resetEnds = returns.Select(reset => (reset.Second.Beat, reset.Second.Hand)).ToHashSet();
        var reentries = resetEnds.Select(key => new ParityReentry(key.Hand, key.Beat)).ToArray();
        var inherited = Errors(await Audit(foundation, foundation.Gameplay.Notes));
        var baseline = Errors(await Audit(level, original));
        var errors = new HashSet<(double Beat, int Hand)>(baseline);
        var trials = new List<Trial>();
        var accepted = new List<Trial>();
        var baselineGeometry = new Lazy<PlacementDeadEnd>(() =>
        {
            try { Place(original); }
            catch (PlacementDeadEnd failure) { return failure; }
            return null;
        });
        var focus = request["focus"] as JsonObject;
        int budget = Math.Clamp(request["candidate-limit"]?.GetValue<int>() ?? 200, 2, 2000);
        var targets = baseline.Except(inherited).Except(resetEnds)
            .OrderBy(key => key.Beat).ThenBy(key => key.Hand).ToList();
        (double Beat, int Hand)? focusedTarget = null;
        if (focus != null)
        {
            var target = (Beat: StandardSong.Number(focus["errorBeat"]), Hand: focus["hand"].GetValue<int>());
            if (!targets.Contains(target))
                throw new InvalidDataException("The focused beat and hand are not an unresolved parity error in this draft.");
            focusedTarget = target;
        }

        // Give every linked target a chance before committing a local edit.
        var linkedOrder = targets.OrderByDescending(target => target == focusedTarget)
            .ThenBy(target => repeats.Where(repeat => repeat.Occurrences.Any(window => Contains(window, target.Beat)))
                .Select(repeat => repeat.Occurrences[0].Start).DefaultIfEmpty(double.PositiveInfinity).Min())
            .ToArray();
        var localOrder = targets.OrderByDescending(target => target == focusedTarget).ToArray();
        await Pass(linkedOrder, linked: true, budget / 4, Search.Nearby);
        await Pass(localOrder, linked: false, budget / 2, Search.Nearby);
        await Pass(localOrder, linked: false, restoreTimings ? 3 * budget / 4 : budget, Search.SameHandLookback);
        if (restoreTimings)
            await Pass(localOrder, linked: false, budget, Search.RestoreTiming);
        var originalById = original.ToDictionary(note => note.Id);
        return new
        {
            difficulty, parityAdherence = 1, reference = reference.Difficulty, restoreLinkedTimings = restoreTimings,
            sourceSha256 = SpreadGeneration.Fingerprint(Path.Combine(level.Folder, level.Filename)),
            baseline = Findings(baseline.Except(inherited).Except(resetEnds)),
            remaining = Findings(errors.Except(inherited).Except(resetEnds)),
            resetEnds = Findings(resetEnds), inherited = Findings(errors.Intersect(inherited)),
            accepted, trials, candidateLimit = budget,
            existingPlacementFailure = baselineGeometry.IsValueCreated ? baselineGeometry.Value?.Message : null,
            hitCandidateLimit = trials.Count >= budget,
            noteCountBefore = original.Count, noteCountAfter = current.Count,
            targetNoteFraction = targetFraction, targetNoteCount = targetCount, budgetReferenceNoteCount = referenceCount,
            targetLeftShare, leftShareBefore = LeftShare(original), leftShareAfter = LeftShare(current),
            proposedNotes = current.Where(note => !originalById.TryGetValue(note.Id, out var before) || before != note),
        };

        async Task Pass(IReadOnlyList<(double Beat, int Hand)> order, bool linked, int limit, Search search)
        {
            for (int index = 0; index < order.Count; index++)
            {
                var target = order[index];
                if (!errors.Contains(target) || trials.Count >= limit)
                    continue;
                int targetLimit = trials.Count + Math.Max(1, (limit - trials.Count) / (order.Count - index));
                int currentCount = current.Count;
                int currentLeft = current.Count(note => note.Hand == 0);
                (Note[] Notes, HashSet<(double Beat, int Hand)> Errors, int Trial)? best = null;
                foreach (var proposal in Proposals(target, search).OrderBy(proposal => linked ? proposal.Cost :
                    proposal.Cost + Balance(proposal.Edits).Total))
                {
                    if (trials.Count >= targetLimit)
                        break;
                    var scopes = linked ? Scopes(proposal) : new Scope[] { null };
                    foreach (var scope in scopes)
                    {
                        if (trials.Count >= targetLimit)
                            break;
                        var edits = Copy(proposal, scope, out var mirrored, out string failure);
                        var balance = Balance(edits);
                        double cost = proposal.Cost + balance.Total;
                        var cleared = Array.Empty<(double Beat, int Hand)>();
                        var introduced = Array.Empty<(double Beat, int Hand)>();
                        var followUp = Array.Empty<Trial>();
                        var changedNotes = edits.Select(edit => edit.After).ToArray();
                        if (failure == null)
                        {
                            int next = current.Max(note => note.Id);
                            var changed = edits.Where(edit => edit.Before.HasValue).ToDictionary(edit => edit.Before.Value.Id);
                            var added = edits.Where(edit => !edit.Before.HasValue)
                                .Select(edit => edit.After with { Id = ++next }).ToArray();
                            changedNotes = changed.Values.Select(edit => edit.After).Concat(added).ToArray();
                            var candidate = current.Select(note => changed.TryGetValue(note.Id, out var edit) ? edit.After : note)
                                .Concat(added)
                                .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();
                            var proposedErrors = Errors(await Audit(level, candidate));
                            cleared = errors.Except(proposedErrors).Except(inherited).Except(resetEnds).ToArray();
                            introduced = proposedErrors.Except(errors).ToArray();
                            if (!cleared.Contains(target))
                                failure = "Target parity error remains";
                            else if ((introduced.Length > 0 && search != Search.RestoreTiming) ||
                                errors.Intersect(inherited).Any(key => !proposedErrors.Contains(key)))
                                failure = "Changes parity elsewhere";
                            else
                            {
                                try
                                {
                                    CheckGeometry(candidate);
                                    if (introduced.Length > 0)
                                    {
                                        var savedNotes = current;
                                        var savedErrors = errors;
                                        int acceptedCount = accepted.Count;
                                        current = candidate;
                                        errors = proposedErrors;
                                        var queue = introduced.OrderBy(key => key.Beat).ThenBy(key => key.Hand).ToArray();
                                        // Reserve one entry for the restoration transaction itself.
                                        int followUpLimit = Math.Min(targetLimit - 1, trials.Count + 36);
                                        await Pass(queue, linked: false, followUpLimit, Search.Nearby);
                                        await Pass(queue, linked: false, followUpLimit, Search.SameHandLookback);
                                        if (errors.Contains(target) || !errors.IsSubsetOf(savedErrors))
                                            failure = "Follow-up left new parity errors";
                                        followUp = accepted.Skip(acceptedCount).ToArray();
                                        cost += followUp.Sum(patch => patch.Cost);
                                        candidate = current.ToArray();
                                        proposedErrors = errors;
                                        cleared = savedErrors.Except(errors).Except(inherited).Except(resetEnds).ToArray();
                                        current = savedNotes;
                                        errors = savedErrors;
                                        accepted.RemoveRange(acceptedCount, accepted.Count - acceptedCount);
                                        foreach (var patch in followUp)
                                        {
                                            int trialIndex = trials.IndexOf(patch);
                                            if (trialIndex >= 0)
                                                trials[trialIndex] = patch with { Result = "Provisional follow-up" };
                                        }
                                    }
                                    if (failure == null && (best == null || proposedErrors.Count < best.Value.Errors.Count ||
                                        (proposedErrors.Count == best.Value.Errors.Count && cost < trials[best.Value.Trial].Cost)))
                                        best = (candidate, proposedErrors, trials.Count);
                                }
                                catch (PlacementDeadEnd error)
                                {
                                    failure = error.Message;
                                }
                            }
                        }
                        var trial = new Trial(proposal.Kind, cost, target.Beat, target.Hand,
                            proposal.Edits.Select(edit => edit.After.Beat).Distinct().ToArray(),
                            scope?.Occurrences.Length ?? proposal.LinkedBeats?.Length ?? 1,
                            scope?.Occurrences.Select(window => window.Start).ToArray() ?? Array.Empty<double>(),
                            mirrored, failure ?? "Eligible", changedNotes, Findings(cleared), Findings(introduced),
                            balance.Hand, balance.Budget, followUp, proposal.LinkedBeats ?? Array.Empty<double>());
                        trials.Add(trial);
                        if (CanStop())
                            break;
                    }
                    if (CanStop())
                        break;
                }
                if (best is { } result)
                {
                    current = result.Notes;
                    errors = result.Errors;
                    var winner = trials[result.Trial] with { Result = "Accepted" };
                    trials[result.Trial] = winner;
                    accepted.Add(winner);
                }

                // A mirrored copy can rebalance the original edit, so linked trials
                // use their full copied cost when choosing a winner.
                bool CanStop() => !linked && best is { } result &&
                    trials[result.Trial].FollowUp.Length == 0 &&
                    !result.Errors.Except(inherited).Except(resetEnds).Any();

                (double Hand, double Budget, double Total) Balance(Edit[] edits)
                {
                    int count = currentCount + edits.Count(edit => !edit.Before.HasValue);
                    int left = currentLeft + edits.Count(edit => edit.After.Hand == 0) -
                        edits.Count(edit => edit.Before?.Hand == 0);
                    var costs = RatioCosts(currentCount, currentLeft, count, left, targetCount, targetLeftShare);
                    return (costs.Hand, costs.Budget, costs.Hand + costs.Budget);
                }
            }
        }

        IEnumerable<Candidate> Proposals((double Beat, int Hand) target, Search search)
        {
            double Distance(double beat) => Math.Abs(level.Clock.Seconds(beat) - level.Clock.Seconds(target.Beat));
            var attacks = NestedTimings.Group(current);
            if (search == Search.RestoreTiming)
            {
                foreach (var restore in TimingRestorations(target, Distance))
                    yield return restore;
                yield break;
            }
            var neighborhood = attacks.Where(group => Distance(group.Beat) <= 1.8)
                .OrderBy(group => Math.Abs(group.Beat - target.Beat)).Take(9).ToArray();
            double preferred = focus != null && StandardSong.Number(focus["errorBeat"]) == target.Beat &&
                focus["hand"].GetValue<int>() == target.Hand ? StandardSong.Number(focus["restoreBeat"]) : double.NaN;
            var singleGroups = search == Search.SameHandLookback ? Lookback(attacks, target, Distance, preferred) : neighborhood;
            var singles = singleGroups.Where(group => group.Notes.Count == 1 && !protectedIds.Contains(group.Notes[0].Id))
                .Select(group => group.Notes[0]).OrderByDescending(note => note.Beat == preferred).ToArray();
            var editable = neighborhood.SelectMany(group => group.Notes)
                .Where(note => !protectedIds.Contains(note.Id) &&
                    current.Count(other => other.Beat == note.Beat && other.Hand == note.Hand) == 1).ToArray();
            var arrows = editable.SelectMany(note => new[] { 0, 1 }.Where(direction => direction != note.Direction)
                .Select(direction => new Edit(note, note with { Direction = direction, Angle = 0 }))).ToArray();
            var swaps = singles.SelectMany(note => new[] { (int)Mirror(note).Direction, 0, 1 }.Distinct()
                .Select(direction => new Edit(note, Mirror(note) with { Direction = direction, Angle = 0 })))
                .ToArray();
            foreach (var arrow in arrows.Where(_ => search == Search.Nearby))
                yield return new Candidate("Adjust arrow", .7 + Distance(arrow.After.Beat), new[] { arrow });
            foreach (var swap in swaps.Where(_ => search == Search.Nearby))
            {
                double cost = 2.1 + Distance(swap.After.Beat);
                yield return new Candidate("Swap hand and arrow", cost, new[] { swap });
                foreach (var arrow in arrows.Where(edit => edit.After.Id != swap.After.Id))
                    yield return new Candidate("Swap hand and adjust neighboring arrow",
                        cost + .8 + Distance(arrow.After.Beat), new[] { swap, arrow });
            }

            foreach (var note in singles)
            {
                var authored = higherNotes.Where(group => Math.Abs(group.Beat - note.Beat) <= .0001)
                    .SelectMany(group => group.Notes)
                    .Where(other => other.Hand != note.Hand && Math.Abs(other.Beat - note.Beat) <= .0001)
                    .Select(other => other with { Beat = note.Beat, Id = -1 }).Distinct().ToArray();
                int hand = 1 - note.Hand;
                // Start with simple own-side positions, then search alternative cuts and cells.
                var variations = from direction in new[] { 0, 1, 2, 3, 4, 5, 6, 7 }
                                 from x in (hand == 0 ? new[] { 0, 1, 2 } : new[] { 3, 2, 1 })
                                 from y in new[] { 0, 1, 2 }
                                 where (x, y) != (note.X, note.Y)
                                 select new Note(-1, note.Beat, hand, x, y, direction, 0);
                foreach (var addition in authored.Concat(variations).Distinct())
                {
                    double shapeCost = authored.Contains(addition) ? 0 :
                        (authored.Length == 0 ? .75 : .45) + .12 * addition.Y +
                        .1 * Math.Abs(addition.X - (hand == 0 ? 0 : 3)) + (addition.Direction < 2 ? 0 : .3);
                    double distance = search == Search.SameHandLookback && double.IsFinite(preferred) ?
                        Math.Abs(level.Clock.Seconds(note.Beat) - level.Clock.Seconds(preferred)) : Distance(note.Beat);
                    double cost = 1.35 + distance + shapeCost + (hand == target.Hand ? 0 : .75) +
                        (note.Beat > target.Beat ? .8 : 0) -
                        (note.Beat == preferred ? 1.35 : 0);
                    var add = new Edit(null, addition);
                    if (hand == target.Hand)
                    {
                        yield return new Candidate("Restore double", cost, new[] { add });
                        foreach (var arrow in arrows.Where(edit => search == Search.Nearby &&
                            edit.After.Hand == hand && edit.After.Beat != note.Beat))
                            yield return new Candidate("Restore double and adjust arrow",
                                cost + .55 + .35 * Distance(arrow.After.Beat), new[] { add, arrow });
                    }
                    foreach (var swap in swaps.Where(edit => search == Search.Nearby && edit.After.Beat != note.Beat))
                        yield return new Candidate("Restore double and swap hand",
                            cost + 1.25 + .35 * Distance(swap.After.Beat), new[] { add, swap });
                }
            }

        }

        IEnumerable<NestedTimings.BeatGroup> Lookback(IReadOnlyList<NestedTimings.BeatGroup> attacks,
            (double Beat, int Hand) target, Func<double, double> distance, double preferred)
        {
            double start = attacks.LastOrDefault(group => group.Beat < target.Beat &&
                group.Notes.Any(note => note.Hand == target.Hand))?.Beat ?? target.Beat;
            var available = attacks.Where(group => group.Beat < target.Beat && distance(group.Beat) is > 1.8 and <= 12 &&
                group.Notes.Count == 1 && group.Notes[0].Hand != target.Hand && !protectedIds.Contains(group.Notes[0].Id));
            return available.Where(group => group.Beat == preferred || group.Beat >= start)
                .OrderByDescending(group => group.Beat == preferred)
                .ThenBy(group => Math.Abs(level.Clock.Seconds(group.Beat) - level.Clock.Seconds(start))).Take(8);
        }

        IEnumerable<Candidate> TimingRestorations((double Beat, int Hand) target, Func<double, double> distance)
        {
            var occupied = NestedTimings.DistinctBeats(current);
            for (int index = 0; index < referenceGroups.Count; index++)
            {
                var origin = referenceGroups[index];
                if (distance(origin.Beat) > 1.8 || NestedTimings.Nearest(origin.Beat, occupied) >= 0 ||
                    !origin.Notes.Any(note => note.Hand == target.Hand))
                    continue;
                var peers = new HashSet<int> { index };
                var queue = new Queue<int>();
                queue.Enqueue(index);
                while (queue.Count > 0)
                    foreach (int peer in referenceLinks.Value[queue.Dequeue()].SelectMany(link => link))
                        if (peers.Add(peer))
                            queue.Enqueue(peer);
                var missing = peers.OrderBy(peer => peer).Select(peer => referenceGroups[peer])
                    .Where(group => NestedTimings.Nearest(group.Beat, occupied) < 0).ToArray();
                if (missing.Any(group => higher.Any(rank => NestedTimings.Nearest(group.Beat, saved[rank]) < 0)))
                    continue;
                var edits = missing.SelectMany(group => group.Notes)
                    .Select(note => new Edit(null, note with { Id = -1 })).ToArray();
                yield return new Candidate("Restore linked timing", 2.7 + distance(origin.Beat), edits,
                    peers.Select(peer => referenceGroups[peer].Beat).OrderBy(beat => beat).ToArray());
            }
        }

        IEnumerable<Scope> Scopes(Candidate candidate)
        {
            foreach (var repeat in repeats.OrderBy(repeat => repeat.Occurrences[0].Start))
            foreach (var origin in repeat.Occurrences)
                if (candidate.Edits.All(edit => Contains(origin, edit.After.Beat)))
                    yield return new Scope(origin, repeat.Occurrences, repeat.Links);
        }

        Edit[] Copy(Candidate proposal, Scope scope, out double[] mirroredStarts, out string failure)
        {
            var mirrored = new List<double>();
            mirroredStarts = Array.Empty<double>();
            failure = null;
            if (scope == null)
                return proposal.Edits;
            var copied = new List<Edit>();
            foreach (var occurrence in scope.Occurrences)
            {
                bool? mirror = occurrence == scope.Origin ? false : Mirrored(scope, occurrence);
                if (!mirror.HasValue)
                {
                    failure = $"Unclear hand correspondence at section {occurrence.Start}";
                    return proposal.Edits;
                }
                if (mirror.Value)
                    mirrored.Add(occurrence.Start);
                foreach (var edit in proposal.Edits)
                {
                    double? beat = occurrence == scope.Origin ? edit.After.Beat : Counterpart(scope, edit.After.Beat, occurrence);
                    if (!beat.HasValue)
                    {
                        failure = $"No aligned beat for {edit.After.Beat} in section {occurrence.Start}";
                        return proposal.Edits;
                    }
                    var after = (mirror.Value ? Mirror(edit.After) : edit.After) with { Beat = beat.Value };
                    var atBeat = current.Where(note => note.Beat == beat).ToArray();
                    if (edit.Before is Note before)
                    {
                        int hand = mirror.Value ? 1 - before.Hand : before.Hand;
                        var matches = atBeat.Where(note => note.Hand == hand).ToArray();
                        if (matches.Length != 1 || protectedIds.Contains(matches[0].Id) ||
                            (before.Hand != edit.After.Hand && atBeat.Length != 1))
                        {
                            failure = $"No editable matching swing at {beat}";
                            return proposal.Edits;
                        }
                        copied.Add(new Edit(matches[0], after with { Id = matches[0].Id }));
                    }
                    else if (atBeat.Length == 1 && atBeat[0].Hand != after.Hand && !protectedIds.Contains(atBeat[0].Id))
                        copied.Add(new Edit(null, after));
                    else
                    {
                        failure = $"No editable matching single at {beat}";
                        return proposal.Edits;
                    }
                }
            }
            mirroredStarts = mirrored.ToArray();
            return copied.ToArray();
        }

        double? Counterpart(Scope scope, double beat, RepeatWindow occurrence)
        {
            var matches = scope.Links.Where(link => link.Any(index => groups[index].Beat == beat))
                .SelectMany(link => link).Distinct().Select(index => groups[index].Beat)
                .Where(other => Contains(occurrence, other)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        bool? Mirrored(Scope scope, RepeatWindow occurrence)
        {
            var pairs = groups.Where(group => group.Notes.Count == 1 && Contains(scope.Origin, group.Beat))
                .Select(group => (Hand: group.Notes[0].Hand, Other: Counterpart(scope, group.Beat, occurrence)))
                .Where(pair => pair.Other.HasValue)
                .Join(groups.Where(group => group.Notes.Count == 1), pair => pair.Other.Value, group => group.Beat,
                    (pair, group) => pair.Hand != group.Notes[0].Hand).ToArray();
            int flipped = pairs.Count(value => value);
            if (pairs.Length < 2)
                return null;
            if (flipped * 4 >= pairs.Length * 3)
                return true;
            return (pairs.Length - flipped) * 4 >= pairs.Length * 3 ? false : null;
        }

        void CheckGeometry(Note[] candidate)
        {
            foreach (var reset in returns)
                if (candidate.Any(note => note.Hand == reset.First.Hand &&
                    reset.First.Beat < note.Beat && note.Beat < reset.Second.Beat))
                    throw new PlacementDeadEnd(reset.Second.Beat, new[] { reset.First, reset.Second },
                        "Repair interrupts a verified reset");
            try
            {
                Place(candidate);
            }
            catch (PlacementDeadEnd failure)
            {
                var existing = baselineGeometry.Value;
                if (existing == null || existing.Reason != failure.Reason || Math.Abs(existing.Beat - failure.Beat) > .0001)
                    throw;
                var originalNotes = original.ToDictionary(note => note.Id);
                var changed = candidate.Where(note => !originalNotes.TryGetValue(note.Id, out var before) || before != note)
                    .Select(note => level.Clock.Seconds(note.Beat)).Distinct().OrderBy(time => time).ToArray();
                double radius = Math.Max(2, halfJump * 60 / level.Bpm);
                double blocker = level.Clock.Seconds(failure.Beat);
                if (changed.Any(time => Math.Abs(time - blocker) <= radius))
                    throw;
                // The saved chart's unrelated blocker prevents a whole-chart
                // placement pass. Still validate the edited regions with context.
                foreach (double time in changed)
                    Place(candidate.Where(note => Math.Abs(level.Clock.Seconds(note.Beat) - time) <= radius).ToArray());
            }
            level.Gameplay.WriteWithLinks(candidate, prepared.Links.Optional, pattern.ReleasedLinks);
        }

        void Place(IReadOnlyList<Note> notes)
        {
            var source = notes.ToDictionary(note => note.Id);
            var cues = notes.ToDictionary(note => note.Id, note => new PlacementNoteCue(null,
                AllowedCells: new HashSet<(double X, double Y)> { (note.X, note.Y) }));
            var plan = PlacementPreparation.Build(notes, source, level.Gameplay.Walls, level.Gameplay.Bombs,
                pattern.Anchors, pattern.Endpoints, cues, prepared.Sections, level.Clock, level.Bpm, speed,
                halfJump * 60 / level.Bpm, profile, Array.Empty<double>());
            ChartPlacement.Place(notes, plan);
        }

        Task<JsonObject> Audit(StandardLevel chart, IReadOnlyList<Note> notes) =>
            PlacementParity.Audit(runtime, notes, chart.Gameplay.Bombs, ReferenceEquals(chart, level) ? reentries : Array.Empty<ParityReentry>(), chart.Bpm,
                chart.Clock.Seconds, chart.Clock.Regions.Count != 1 || chart.Clock.Regions[0].SecondsPerBeat != 60 / chart.Bpm,
                CancellationToken.None);
    }

    private static bool Contains(RepeatWindow window, double beat) => beat >= window.Start && beat < window.End;

    internal static (double Hand, double Budget) RatioCosts(int beforeCount, int beforeLeft, int afterCount,
        int afterLeft, int targetCount, double targetLeftShare)
    {
        // Measure hand-share changes in note equivalents so a single edit
        // has comparable weight on short and long charts.
        double hand = .35 * beforeCount * (Math.Abs((double)afterLeft / afterCount - targetLeftShare) -
            Math.Abs((double)beforeLeft / beforeCount - targetLeftShare));
        double budget = .25 * (Math.Abs(afterCount - targetCount) - Math.Abs(beforeCount - targetCount));
        return (hand, budget);
    }

    private static double LeftShare(IReadOnlyList<Note> notes) =>
        (double)notes.Count(note => note.Hand == 0) / notes.Count;

    internal static Note Mirror(Note note) => note with
    {
        Hand = 1 - note.Hand, X = 3 - note.X, Angle = -note.Angle,
        Direction = note.Direction switch { 2 => 3, 3 => 2, 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => note.Direction },
    };

    private static HashSet<(double Beat, int Hand)> Errors(JsonObject audit) => audit["issues"].AsArray()
        .Where(issue => issue["status"].GetValue<string>() == "error")
        .Select(issue => (StandardSong.Number(issue["beat"]), issue["hand"].GetValue<int>())).ToHashSet();

    private static object[] Findings(IEnumerable<(double Beat, int Hand)> keys) => keys.OrderBy(key => key.Beat)
        .ThenBy(key => key.Hand).Select(key => (object)new { beat = key.Beat, hand = key.Hand }).ToArray();
}
