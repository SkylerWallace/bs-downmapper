using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    internal static class TimingParityRepair
    {
        internal static IReadOnlyList<Note> AlternateHands(PreparedTiming prepared, IReadOnlyList<Note> notes, double maximumGapSeconds)
        {
            if (maximumGapSeconds <= 0)
                return notes;
            var mirrored = AdditionalHands(prepared).Values.ToDictionary(note => (note.Beat, note.Hand));
            var groups = NestedTimings.Group(notes);
            var replacements = new Dictionary<int, Note>();
            var clock = prepared.Level.Clock;
            for (int index = 1; index + 1 < groups.Count; index++)
            {
                var before = groups[index - 1].Notes;
                var current = groups[index].Notes;
                var after = groups[index + 1].Notes;
                if (before.Count != 1 || current.Count != 1 || after.Count != 1 ||
                    replacements.ContainsKey(before[0].Id) || before[0].Hand != current[0].Hand ||
                    current[0].Hand != after[0].Hand)
                    continue;
                var note = current[0];
                if (clock.Gap(before[0].Beat, note.Beat) > maximumGapSeconds ||
                    clock.Gap(note.Beat, after[0].Beat) > maximumGapSeconds ||
                    !mirrored.TryGetValue((note.Beat, 1 - note.Hand), out var swap) ||
                    notes.Any(other => other.Hand == swap.Hand && Math.Abs(clock.Gap(note.Beat, other.Beat)) < .55) ||
                    prepared.Level.Gameplay.Bombs.Any(bomb => before[0].Beat <= bomb.Beat && bomb.Beat <= after[0].Beat) ||
                    before.Concat(current).Concat(after).Any(hit => prepared.ResetIds.Contains(hit.Id)))
                    continue;
                replacements.Add(note.Id, swap);
            }
            return notes.Select(note => replacements.GetValueOrDefault(note.Id, note)).ToArray();
        }

        internal static IReadOnlyDictionary<int, Note> AdditionalHands(PreparedTiming prepared)
        {
            var output = new Dictionary<int, Note>();
            int id = prepared.Level.Gameplay.Notes.Max(note => note.Id);
            var linked = prepared.Level.Gameplay.ReadLinks().SelectMany(link => link.AnchorIds).ToHashSet();
            var eligible = prepared.Context.Notes.Select(note => note.Id).ToHashSet();
            foreach (var group in NestedTimings.Group(prepared.Level.Gameplay.Notes))
            {
                if (group.Notes.Count != 1)
                    continue;
                var note = group.Notes[0];
                int nextId = ++id;
                if (linked.Contains(note.Id) || prepared.Context.ProtectedIds.Contains(note.Id) ||
                    !eligible.Contains(note.Id))
                    continue;
                double direction = note.Direction switch { 2 => 3, 3 => 2, 4 => 5, 5 => 4, 6 => 7, 7 => 6, _ => note.Direction };
                output.Add(nextId, note with { Id = nextId, Hand = 1 - note.Hand, X = 3 - note.X,
                    Direction = direction, Angle = -note.Angle });
            }
            return output;
        }

        private const double LookbackSeconds = 2.5;
        private const double MinimumHandGapSeconds = .17;
        private const int MaximumTrials = 64;
        private const int MaximumRestorations = 20;

        private readonly record struct Finding(double Beat, int Hand, string Status);

        internal static async Task<IReadOnlyList<Note>> RestoreAuthoredSwings(
            PreparedTiming prepared, IReadOnlyList<Note> selected, DirectionRuntime runtime,
            CancellationToken cancellation = default, double adherence = 1, bool repairHandAssignments = false)
        {
            bool restoreBeats = adherence >= .95;
            int restorationLimit = (int)Math.Round((restoreBeats ? MaximumRestorations : 8) *
                Math.Clamp(2 * adherence - 1, 0, 1));
            if (restorationLimit == 0)
                return selected;
            var level = prepared.Level;
            var clock = level.Clock;
            var gameplay = level.Gameplay;
            var kept = selected.Select(note => note.Id).ToHashSet();
            var linked = gameplay.ReadLinks().SelectMany(link => link.AnchorIds).ToHashSet();
            var protectedIds = prepared.Context.ProtectedIds;
            var candidates = new List<(Note Note, bool NewBeat)>();
            foreach (var group in NestedTimings.Group(prepared.Context.Notes))
            {
                if (group.Notes.Any(note => protectedIds.Contains(note.Id) || linked.Contains(note.Id)))
                    continue;
                var partners = group.Notes.Where(note => kept.Contains(note.Id)).ToArray();
                bool newBeat = partners.Length == 0;
                if (newBeat && (!restoreBeats || group.Notes.Count > 2))
                    continue;
                foreach (var missing in group.Notes.Where(note => !kept.Contains(note.Id)))
                {
                    if (partners.Any(partner => BlocksRestoration(missing, partner)) ||
                        SwingClearance.WallConflicts(missing, gameplay.Walls, clock.Seconds, .20).Any() ||
                        SwingClearance.BombClearances(missing, gameplay.Bombs, clock.Seconds, level.Bpm)
                            .Values.Any(distance => distance <= .5))
                        continue;
                    candidates.Add((missing, newBeat));
                }
            }
            if (candidates.Count == 0 && !restoreBeats)
                return selected;

            var inherited = await Audit(gameplay.Notes).ConfigureAwait(false);
            var findings = await Audit(selected).ConfigureAwait(false);
            var breaks = findings.Where(issue => issue.Status == "error" && !inherited.Contains(issue))
                .OrderBy(issue => issue.Beat).ThenBy(issue => issue.Hand).ToArray();
            var accents = prepared.Links.AccentBeats;
            int trials = 0;
            int restored = 0;
            foreach (var issue in breaks)
            {
                if (trials >= MaximumTrials || restored >= restorationLimit)
                    break;
                if (!findings.Contains(issue))
                    continue;
                var hand = selected.Where(note => note.Hand == issue.Hand).ToArray();
                var ranked = candidates.Where(candidate => candidate.Note.Hand == issue.Hand &&
                        candidate.Note.Beat <= issue.Beat && !kept.Contains(candidate.Note.Id))
                    .Select(candidate => (Candidate: candidate.Note, candidate.NewBeat,
                        Score: Score(candidate.Note, issue, hand, candidate.NewBeat)))
                    .Where(option => double.IsFinite(option.Score))
                    .OrderBy(option => option.NewBeat).ThenBy(option => option.Score)
                    .ThenByDescending(option => option.Candidate.Beat);
                foreach (var option in ranked)
                {
                    if (trials++ >= MaximumTrials)
                        break;
                    var addition = option.Candidate;
                    if (selected.Any(note => note.Beat == addition.Beat && BlocksRestoration(addition, note)))
                        continue;
                    var repaired = selected.Append(addition).OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();
                    var updated = await Audit(repaired).ConfigureAwait(false);
                    if (updated.Contains(issue) ||
                        updated.Any(finding => finding.Status == "error" && !findings.Contains(finding)) ||
                        findings.Any(finding => inherited.Contains(finding) && !updated.Contains(finding)))
                        continue;
                    selected = repaired;
                    findings = updated;
                    kept.Add(addition.Id);
                    restored++;
                    break;
                }
            }
            if (!restoreBeats || !repairHandAssignments)
                return selected;

            // Try the authored double texture as a unit when pruning left
            // several consecutive singles in a short phrase.
            int phraseTrials = 0;
            foreach (var issue in findings.Where(issue => issue.Status == "error" && !inherited.Contains(issue))
                .OrderBy(issue => issue.Beat).ThenBy(issue => issue.Hand).ToArray())
            {
                if (phraseTrials >= 12)
                    break;
                if (!findings.Contains(issue))
                    continue;
                var additions = candidates.Where(item => !item.NewBeat && !kept.Contains(item.Note.Id) &&
                        selected.Any(note => note.Beat == item.Note.Beat) &&
                        !selected.Any(note => note.Beat == item.Note.Beat && BlocksRestoration(item.Note, note)) &&
                        item.Note.Beat <= issue.Beat && clock.Gap(item.Note.Beat, issue.Beat) <= LookbackSeconds)
                    .Select(item => item.Note).OrderBy(note => note.Beat).TakeLast(4).ToArray();
                if (additions.Length < 2 || !additions.Any(note => note.Hand == issue.Hand) ||
                    additions.Zip(additions.Skip(1), (a, b) => clock.Gap(a.Beat, b.Beat)).Any(gap => gap > .8) ||
                    gameplay.Bombs.Any(bomb => additions[0].Beat <= bomb.Beat && bomb.Beat <= issue.Beat) ||
                    selected.Any(note => additions[0].Beat <= note.Beat && note.Beat <= issue.Beat &&
                        prepared.ResetIds.Contains(note.Id)))
                    continue;
                phraseTrials++;
                var trial = selected.Concat(additions).OrderBy(note => note.Beat).ThenBy(note => note.Hand).ToArray();
                var updated = await Audit(trial).ConfigureAwait(false);
                if (!Accepts(updated, issue))
                    continue;
                selected = trial;
                findings = updated;
                kept.UnionWith(additions.Select(note => note.Id));
            }

            var eligible = AdditionalHands(prepared).Values.ToArray();
            int repairTrials = 0, accepted = 0;
            foreach (var issue in findings.Where(issue => issue.Status == "error" && !inherited.Contains(issue))
                .OrderBy(issue => issue.Beat).ThenBy(issue => issue.Hand).ToArray())
            {
                if (repairTrials >= 32 || accepted >= 8)
                    break;
                if (!findings.Contains(issue))
                    continue;
                var hand = selected.Where(note => note.Hand == issue.Hand).ToArray();
                var options = eligible.Where(note => note.Hand == issue.Hand && note.Beat <= issue.Beat)
                    .Select(note => (Note: note, Score: Score(note, issue, hand, false)))
                    .Where(option => double.IsFinite(option.Score))
                    .OrderBy(option => option.Score).ThenByDescending(option => option.Note.Beat);
                foreach (var option in options.Take(6))
                {
                    var note = option.Note;
                    var partners = selected.Where(partner => partner.Beat == note.Beat).ToArray();
                    if (partners.Length != 1 || partners[0].Hand == note.Hand ||
                        SwingClearance.WallConflicts(note, gameplay.Walls, clock.Seconds, .20).Any() ||
                        SwingClearance.BombClearances(note, gameplay.Bombs, clock.Seconds, level.Bpm)
                            .Values.Any(distance => distance <= .5))
                        continue;
                    foreach (bool swap in new[] { false, true })
                    {
                        if (repairTrials >= 32)
                            break;
                        if (!swap && BlocksRestoration(note, partners[0]))
                            continue;
                        repairTrials++;
                        var trial = selected.Where(hit => !swap || hit.Id != partners[0].Id)
                            .Append(note).OrderBy(hit => hit.Beat).ThenBy(hit => hit.Hand).ToArray();
                        var updated = await Audit(trial).ConfigureAwait(false);
                        if (!Accepts(updated, issue))
                            continue;
                        selected = trial;
                        findings = updated;
                        accepted++;
                        break;
                    }
                    if (!findings.Contains(issue))
                        break;
                }
            }
            return selected;

            bool Accepts(HashSet<Finding> updated, Finding issue) =>
                !updated.Contains(issue) &&
                !updated.Any(finding => finding.Status == "error" && !findings.Contains(finding)) &&
                !findings.Any(finding => inherited.Contains(finding) && !updated.Contains(finding));

            bool BlocksRestoration(Note missing, Note partner) =>
                partner.Hand == missing.Hand || SwingClearance.GroupRules(missing, partner).Any();

            double Score(Note candidate, Finding issue, IReadOnlyList<Note> hand, bool newBeat)
            {
                double beat = candidate.Beat;
                double lag = clock.Gap(beat, issue.Beat);
                if (lag > LookbackSeconds)
                    return double.PositiveInfinity;
                double nearest = hand.Select(note => Math.Abs(clock.Gap(beat, note.Beat))).DefaultIfEmpty(1).Min();
                if (nearest < (newBeat ? .10 : MinimumHandGapSeconds))
                    return double.PositiveInfinity;
                double previous = hand.Where(note => note.Beat < beat).Select(note => note.Beat)
                    .DefaultIfEmpty(beat).Max();
                if (gameplay.Bombs.Any(bomb => previous <= bomb.Beat && bomb.Beat <= issue.Beat) ||
                    hand.Any(note => previous <= note.Beat && note.Beat <= issue.Beat && prepared.ResetIds.Contains(note.Id)))
                    return double.PositiveInfinity;

                int intervening = hand.Where(note => beat < note.Beat && note.Beat < issue.Beat)
                    .Select(note => note.Beat).Distinct().Count();
                bool crossesSection = prepared.Sections.Any(section => beat < section.Start && section.Start <= issue.Beat);
                bool accent = accents.Contains(beat) || prepared.Context.RequiredBeats.Contains(beat);
                return .25 * lag + .14 * intervening + (crossesSection ? .35 : 0) -
                    (accent ? .20 : 0) - .85 * Math.Min(nearest, .6);
            }

            async Task<HashSet<Finding>> Audit(IReadOnlyList<Note> notes)
            {
                var report = await PlacementParity.Audit(runtime, notes, gameplay.Bombs,
                    Array.Empty<ParityReentry>(), level.Bpm, clock.Seconds,
                    clock.Regions.Count > 1, cancellation).ConfigureAwait(false);
                return report["issues"].AsArray().Select(issue => new Finding(
                    issue["beat"].GetValue<double>(), issue["hand"].GetValue<int>(),
                    issue["status"].GetValue<string>())).ToHashSet();
            }
        }
    }
}
