using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BsDownmap.Engine
{
    public sealed record TimingConflict(string Difficulty, bool Higher, double[] Beats);
    public sealed record TimingPriority(string Difficulty, ISet<int> Preferred, IReadOnlyDictionary<double, double> Scores);

    public static class NestedTimings
    {
        private const double Tolerance = .0001;
        private static readonly string[] Difficulties = { "ExpertPlus", "Expert", "Hard", "Normal", "Easy" };

        public static double[] DistinctBeats(IEnumerable<Note> notes) =>
            Group(notes).Select(group => group.Beat).ToArray();

        public static IReadOnlyDictionary<string, IReadOnlyDictionary<double, double>> ScoreSharedBudgets(
            TreeEnsemble model, TimingFeatures features, int sourceCount, IReadOnlyDictionary<string, int> seedCounts)
        {
            if (sourceCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceCount));
            return Difficulties.Skip(1).Reverse().ToDictionary(tier => tier,
                tier => NestedRankerFeatures.Score(model, features, (double)seedCounts[tier] / sourceCount));
        }

        public static (int[] Order, IReadOnlyDictionary<string, int> Budgets) RankSharedProposals(
            IReadOnlyList<Note> reference, IReadOnlyDictionary<string, RepeatTimingContext> contexts,
            IReadOnlyDictionary<string, IReadOnlyList<Note>> seeds,
            IReadOnlyDictionary<string, ISet<double>> linkedAccents,
            IReadOnlyDictionary<string, IReadOnlyDictionary<double, double>> scores,
            IReadOnlyDictionary<double, double> mix, double[] easyDraft, IEnumerable<double> bookmarks,
            double endBeat, Func<double, double> toGrid, Func<double, double> fromGrid,
            Func<double, double> seconds, bool matchRepeats = true)
        {
            var referenceBeats = DistinctBeats(reference);
            var mandatory = contexts.Values.SelectMany(context => context.ProtectedIds).ToHashSet();
            int PhraseAt(double beat) => (int)Math.Floor((toGrid(beat) + Tolerance) / 48);
            var proposals = Difficulties.Skip(1).Reverse().Select(tier =>
            {
                var selected = SharedTimingSelection.Select(contexts[tier], seeds[tier], tier,
                    scores, mix, easyDraft, referenceBeats, linkedAccents[tier], seconds, PhraseAt);
                return new TimingPriority(tier, selected.Select(note => note.Id).ToHashSet(), scores[tier]);
            }).ToArray();
            return BuildRepeatAwarePriority(reference, proposals, mandatory, contexts, bookmarks,
                endBeat, toGrid, fromGrid, seconds, matchRepeats);
        }

        public static (int[] Order, IReadOnlyDictionary<string, int> Budgets) BuildRepeatAwarePriority(
            IReadOnlyList<Note> reference, IReadOnlyList<TimingPriority> proposals, ISet<int> mandatory,
            IReadOnlyDictionary<string, RepeatTimingContext> contexts, IEnumerable<double> bookmarks,
            double endBeat, Func<double, double> toGrid, Func<double, double> fromGrid,
            Func<double, double> seconds, bool matchRepeats = true)
        {
            int PhraseAt(double beat) => (int)Math.Floor((toGrid(beat) + Tolerance) / 48);
            var (order, budgets) = BuildPriority(reference, proposals, mandatory, PhraseAt);
            if (matchRepeats)
            {
                var repeats = RepeatDetection.Find(reference, bookmarks, endBeat, toGrid, fromGrid);
                if (repeats.Any(repeat => repeat.Links.Length > 0))
                    order = RepeatPriorities.Match(reference, order, proposals, budgets, contexts,
                        mandatory, repeats, PhraseAt, seconds);
            }
            return (order, budgets);
        }

        public static (int[] Order, IReadOnlyDictionary<string, int> Budgets) BuildPriority(
            IReadOnlyList<Note> reference, IReadOnlyList<TimingPriority> proposals, IEnumerable<int> mandatory,
            Func<double, int> phraseAt)
        {
            if (proposals.Count != 4 || !proposals.Select(tier => tier.Difficulty)
                .SequenceEqual(Difficulties.Skip(1).Reverse()))
                throw new ArgumentException("Timing priorities must cover Easy through Expert in rank order.");
            var notes = reference.ToDictionary(note => note.Id);
            var beats = Group(reference).SelectMany(group => group.Notes.Select(note => (note.Id, group.Beat)))
                .ToDictionary(item => item.Id, item => item.Beat);
            var phrases = reference.ToDictionary(note => note.Id, note => phraseAt(note.Beat));
            var order = mandatory.Distinct().OrderBy(id => notes[id].Beat).ThenBy(id => id).ToList();
            var kept = order.ToHashSet();
            var budgets = new Dictionary<string, int>();
            foreach (var proposal in proposals)
            {
                int budget = Math.Max(order.Count, proposal.Preferred.Count);
                budgets.Add(proposal.Difficulty, budget);
                int remaining = budget - order.Count;
                if (remaining == 0) continue;
                var wanted = proposal.Preferred.GroupBy(id => phrases[id]).ToDictionary(group => group.Key, group => group.Count());
                var present = order.GroupBy(id => phrases[id]).ToDictionary(group => group.Key, group => group.Count());
                var keptBeats = order.Select(id => beats[id]).ToHashSet();
                var additions = new List<(bool BeyondDeficit, double Position, bool OutsideProposal, double Score, int Id)>();
                foreach (var group in notes.Keys.Where(id => !kept.Contains(id)).GroupBy(id => phrases[id]))
                {
                    var pool = group.OrderBy(id => !proposal.Preferred.Contains(id))
                        .ThenBy(id => keptBeats.Contains(beats[id]))
                        .ThenByDescending(id => proposal.Scores[beats[id]])
                        .ThenBy(id => notes[id].Beat).ThenBy(id => id);
                    IEnumerable<int> ranked = pool;
                    if (proposal.Difficulty == "Normal")
                    {
                        var seen = new HashSet<double>(keptBeats);
                        var singles = new List<int>();
                        var doubles = new List<int>();
                        foreach (int id in pool)
                            (seen.Add(beats[id]) ? singles : doubles).Add(id);
                        ranked = singles.Concat(doubles);
                    }
                    int deficit = Math.Max(0, wanted.GetValueOrDefault(group.Key) - present.GetValueOrDefault(group.Key));
                    int position = 0;
                    foreach (int id in ranked)
                    {
                        additions.Add((position >= deficit, (position + 1.0) / Math.Max(1, deficit),
                            !proposal.Preferred.Contains(id), proposal.Scores[beats[id]], id));
                        position++;
                    }
                }
                foreach (var item in additions.OrderBy(item => item.BeyondDeficit).ThenBy(item => item.Position)
                    .ThenBy(item => item.OutsideProposal).ThenByDescending(item => item.Score).ThenBy(item => item.Id)
                    .Take(remaining))
                {
                    order.Add(item.Id);
                    kept.Add(item.Id);
                }
            }
            var expert = proposals[proposals.Count - 1].Scores;
            order.AddRange(notes.Keys.Where(id => !kept.Contains(id)).OrderByDescending(id => expert[beats[id]])
                .ThenBy(id => notes[id].Beat).ThenBy(id => id));
            return (order.ToArray(), budgets);
        }

        internal static IReadOnlyList<Note> CompleteModelSelection(
            IReadOnlyList<Note> eligible, HashSet<int> selected, IEnumerable<int> protectedIds,
            IEnumerable<double> requiredBeats)
        {
            var available = eligible.Select(note => note.Id).ToHashSet();
            if (selected.Count == 0 || !selected.IsSubsetOf(available))
                throw new InvalidDataException("The timing model must select existing candidate notes.");
            selected.UnionWith(protectedIds.Where(available.Contains));
            var groups = Group(eligible);
            var beats = groups.Select(group => group.Beat).ToArray();
            foreach (double required in requiredBeats)
            {
                int index = Nearest(required, beats);
                if (index < 0)
                    throw new InvalidDataException($"The model's timing source cannot retain required beat {required}.");
                var notes = groups[index].Notes;
                if (!notes.Any(note => selected.Contains(note.Id))) selected.Add(notes[0].Id);
            }
            return eligible.Where(note => selected.Contains(note.Id)).ToArray();
        }

        public static IReadOnlyList<TimingConflict> Conflicts(
            IReadOnlyDictionary<string, double[]> saved, string difficulty, double[] proposed)
        {
            int target = Array.IndexOf(Difficulties, difficulty);
            if (target < 0) throw new ArgumentException($"Unknown Standard difficulty {difficulty}.");
            var conflicts = new List<TimingConflict>();
            for (int rank = 0; rank < Difficulties.Length; rank++)
            {
                if (rank == target || !saved.TryGetValue(Difficulties[rank], out var beats)) continue;
                bool higher = rank < target;
                var required = higher ? proposed : beats;
                var available = higher ? beats : proposed;
                var missing = required.Where(beat => Nearest(beat, available) < 0).ToArray();
                if (missing.Length > 0) conflicts.Add(new TimingConflict(Difficulties[rank], higher, missing));
            }
            return conflicts;
        }

        public static IReadOnlyList<Note> Select(
            IReadOnlyList<Note> reference, IReadOnlyList<int> order, IReadOnlyList<Note> eligible,
            int budget, IEnumerable<int> protectedIds, IEnumerable<double> requiredBeats, bool sourceIsReference,
            double? targetLeftShare = null)
        {
            var byId = eligible.ToDictionary(note => note.Id);
            var groups = Group(eligible);
            var beats = groups.Select(group => group.Beat).ToArray();
            var available = sourceIsReference
                ? order.Where(byId.ContainsKey).Select(id => byId[id]).ToArray()
                : RankStartingChart(reference, order, groups);
            var byBeat = groups.SelectMany(group => group.Notes.Select(note => (note.Id, group.Beat)))
                .ToDictionary(item => item.Id, item => item.Beat);
            var firstAt = new Dictionary<double, int>();
            foreach (var note in available)
                firstAt.TryAdd(byBeat[note.Id], note.Id);

            var mandatory = new HashSet<int>(protectedIds);
            foreach (double beat in requiredBeats)
            {
                int index = Nearest(beat, beats);
                if (index < 0 || !firstAt.TryGetValue(beats[index], out int id))
                    throw new InvalidDataException("A saved lower difficulty needs a timing missing from the eligible higher charts.");
                mandatory.Add(id);
            }
            if (mandatory.Any(id => !byId.ContainsKey(id)))
                throw new InvalidDataException("A protected timing is missing from the eligible higher charts.");
            int remaining = Math.Max(0, budget - mandatory.Count);
            var optional = available.Where(note => !mandatory.Contains(note.Id)).ToArray();
            var selected = optional.Take(remaining).ToList();
            if (targetLeftShare is double share)
            {
                int targetLeft = (int)Math.Round((mandatory.Count + selected.Count) * share);
                int left = mandatory.Count(id => byId[id].Hand == 0) + selected.Count(note => note.Hand == 0);
                int wantedHand = left < targetLeft ? 0 : 1;
                var replacements = new Queue<Note>(optional.Skip(selected.Count).Where(note => note.Hand == wantedHand));
                for (int i = selected.Count - 1; i >= 0 && left != targetLeft && replacements.Count != 0; i--)
                {
                    if (selected[i].Hand == wantedHand)
                        continue;
                    selected[i] = replacements.Dequeue();
                    left += wantedHand == 0 ? 1 : -1;
                }
            }
            var result = available.Where(note => mandatory.Contains(note.Id))
                .Concat(selected)
                .OrderBy(note => note.Beat).ThenBy(note => note.Hand).ThenBy(note => note.Id).ToArray();
            if (result.Length == 0) throw new InvalidDataException("The nested timing model found no eligible notes.");
            return result;
        }

        private static Note[] RankStartingChart(
            IReadOnlyList<Note> reference, IReadOnlyList<int> order, List<BeatGroup> eligible)
        {
            var positions = order.Select((id, position) => (id, position)).ToDictionary(item => item.id, item => item.position);
            var source = Group(reference);
            var beats = source.Select(group => group.Beat).ToArray();
            var groups = source.Select(group => new[]
            {
                group.Notes.Where(note => note.Hand == 0).Select(note => positions[note.Id]).OrderBy(i => i).ToArray(),
                group.Notes.Where(note => note.Hand == 1).Select(note => positions[note.Id]).OrderBy(i => i).ToArray(),
            }).ToArray();
            var ranked = new List<(int Priority, double Beat, int HandIndex, Note Note)>();
            foreach (var group in eligible)
            {
                int index = Nearest(group.Beat, beats);
                int[] seen = { 0, 0 };
                foreach (var note in group.Notes.OrderBy(note => note.Hand).ThenBy(note => note.Id))
                {
                    int count = seen[note.Hand]++;
                    int priority = order.Count;
                    if (index >= 0)
                    {
                        var options = groups[index][note.Hand];
                        if (count < options.Length) priority = options[count];
                        else if (count == 0) priority = source[index].Notes.Min(member => positions[member.Id]);
                    }
                    ranked.Add((priority, group.Beat, count, note));
                }
            }
            return ranked.OrderBy(item => item.Priority).ThenBy(item => item.Beat).ThenBy(item => item.HandIndex)
                .ThenBy(item => item.Note.Id).Select(item => item.Note).ToArray();
        }

        internal static List<BeatGroup> Group(IEnumerable<Note> notes)
        {
            var groups = new List<BeatGroup>();
            foreach (var note in notes.OrderBy(note => note.Beat))
            {
                double beat = RoundBeat(note.Beat);
                if (groups.Count == 0 || beat - groups[groups.Count - 1].Beat > Tolerance)
                    groups.Add(new BeatGroup(beat, new List<Note>()));
                groups[groups.Count - 1].Notes.Add(note);
            }
            return groups;
        }

        private static double RoundBeat(double beat)
        {
            const double scale = 100000;
            double scaled = beat * scale;
            double lower = Math.Floor(scaled);
            // A binary multiply can round a value just off a decimal midpoint to a tie.
            if (scaled - lower == .5)
            {
                double remainder = EngineMath.FusedMultiplyAdd(beat, scale, -scaled);
                if (remainder != 0) return (remainder < 0 ? lower : lower + 1) / scale;
            }
            return Math.Round(scaled) / scale;
        }

        internal static int Nearest(double beat, double[] sorted, double tolerance = Tolerance)
        {
            int index = Array.BinarySearch(sorted, beat);
            if (index >= 0) return index;
            index = ~index;
            double left = index == 0 ? double.PositiveInfinity : Math.Abs(beat - sorted[index - 1]);
            double right = index == sorted.Length ? double.PositiveInfinity : Math.Abs(sorted[index] - beat);
            if (Math.Min(left, right) > tolerance) return -1;
            return left <= right ? index - 1 : index;
        }

        internal sealed record BeatGroup(double Beat, List<Note> Notes);
    }
}
