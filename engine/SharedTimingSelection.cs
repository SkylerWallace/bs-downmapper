using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BsDownmap.Engine
{
    public static class SharedTimingSelection
    {
        private sealed record Tier(double Gap, double Pulse, double Growth, (string Tier, double Weight)[] Lower);
        private static readonly Dictionary<string, Tier> Tiers = new()
        {
            ["Expert"] = new(.07, .24, 0, new[] { ("Hard", .135), ("Normal", .09), ("Easy", .045) }),
            ["Hard"] = new(.105, .30, .54, new[] { ("Normal", .50), ("Easy", .22) }),
            ["Normal"] = new(.17, .44, .71, new[] { ("Easy", .55) }),
            ["Easy"] = new(.33, .74, 1, Array.Empty<(string, double)>()),
        };

        public static IReadOnlyList<Note> Select(
            RepeatTimingContext context, IReadOnlyList<Note> seed, string difficulty,
            IReadOnlyDictionary<string, IReadOnlyDictionary<double, double>> scores,
            IReadOnlyDictionary<double, double> mix, double[] draft, double[] referenceBeats,
            ISet<double> linkedAccents, Func<double, double> seconds, Func<double, int> phraseAt)
        {
            var tier = Tiers[difficulty];
            bool easy = difficulty == "Easy";
            var source = NestedTimings.Group(context.Notes).ToDictionary(group => group.Beat, group => group.Notes);
            var seedIds = seed.Select(note => note.Id).ToHashSet();
            var kept = source.ToDictionary(pair => pair.Key, pair => pair.Value.Where(note => seedIds.Contains(note.Id)).ToList());
            kept = kept.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (kept.Count == 0 || !context.ProtectedIds.IsSubsetOf(seedIds))
                throw new InvalidDataException("The shared timing budget could not retain all protected notes.");

            var times = source.Keys.ToDictionary(beat => beat, seconds);
            var phrases = source.Keys.ToDictionary(beat => beat, phraseAt);
            var fixedBeats = new HashSet<double> { kept.Keys.Min(), kept.Keys.Max() };
            if (easy)
            {
                fixedBeats.UnionWith(context.RequiredBeats.Where(kept.ContainsKey));
                source = source.Where(pair => kept.ContainsKey(pair.Key) || context.RequiredBeats.Contains(pair.Key) ||
                    !context.SparseWindows.Any(window => window.Start <= pair.Key && pair.Key <= window.End))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            var quality = new Dictionary<double, double>();
            var tradeQuality = new Dictionary<double, double>();
            var matchedDraft = new HashSet<double>();
            foreach (double beat in source.Keys)
            {
                int index = NestedTimings.Nearest(beat, referenceBeats);
                if (index < 0)
                    throw new InvalidDataException("A shared timing candidate is missing from the budget reference.");
                double reference = referenceBeats[index];
                double onDraft = NestedTimings.Nearest(reference, draft) >= 0 ? 1 : 0;
                if (onDraft != 0)
                    matchedDraft.Add(beat);
                double lower = tier.Lower.Sum(weight => weight.Weight * scores[weight.Tier][reference]);
                double rank = scores[difficulty][reference];
                double linked = linkedAccents.Contains(beat) ? .12 : 0;
                double inSeed = kept.ContainsKey(beat) ? 1 : 0;
                quality[beat] = rank + lower + linked + (easy ? .67 : .10) * inSeed;
                if (easy)
                    quality[beat] += .90 * (.65 * rank + .40 * onDraft);
                else if (difficulty != "Expert")
                    quality[beat] += .90 * onDraft;
                tradeQuality[beat] = rank + linked + .12 * lower + .16 * onDraft + .13 * inSeed + .10 * mix[reference];
            }

            var available = source.ToLookup(pair => phrases[pair.Key]);
            var result = new Dictionary<double, List<Note>>();
            var lockedBeats = new HashSet<double>(fixedBeats);
            foreach (var phrase in kept.GroupBy(pair => phrases[pair.Key]))
            {
                double first = phrase.Min(pair => pair.Key), last = phrase.Max(pair => pair.Key);
                lockedBeats.Add(first);
                lockedBeats.Add(last);
                var original = phrase.ToDictionary(pair => pair.Key, pair => pair.Value);
                var candidates = available[phrase.Key].Where(pair => first <= pair.Key && pair.Key <= last)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(note => note.Y)
                        .ThenBy(note => Math.Abs(note.X - note.Hand - 1)).ThenBy(note => note.Id).ToList());
                var fixedNotes = original.ToDictionary(pair => pair.Key, pair =>
                    fixedBeats.Contains(pair.Key) || pair.Key == first || pair.Key == last ? pair.Value :
                    pair.Value.Where(note => context.ProtectedIds.Contains(note.Id)).ToList());
                fixedNotes = fixedNotes.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var pair in SelectPhrase(candidates, original, fixedNotes, quality, times, tier))
                    result.Add(pair.Key, pair.Value);
            }

            Trade(result, source, tradeQuality, times, phrases, context.ProtectedIds, lockedBeats, matchedDraft, tier, easy);
            var output = result.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.OrderBy(note => note.Hand)).ToArray();
            if (output.Length != seed.Count || !context.ProtectedIds.IsSubsetOf(output.Select(note => note.Id).ToHashSet()))
                throw new InvalidDataException("Shared timing selection changed the budget or lost a protected note.");
            return output;
        }

        private static Dictionary<double, List<Note>> SelectPhrase(
            Dictionary<double, List<Note>> source, Dictionary<double, List<Note>> seed,
            Dictionary<double, List<Note>> fixedNotes, Dictionary<double, double> quality,
            Dictionary<double, double> times, Tier tier)
        {
            int quota = seed.Values.Sum(group => group.Count);
            int maximumDistinct = quota - fixedNotes.Values.Sum(group => group.Count - 1);
            int wanted = Math.Max(fixedNotes.Count, Math.Min(maximumDistinct,
                seed.Count + (int)Math.Round((quota - seed.Count) * tier.Growth)));
            var chosen = fixedNotes.Keys.OrderBy(beat => beat).ToList();
            int capacity = chosen.Sum(beat => source[beat].Count);
            foreach (double beat in source.Keys.OrderByDescending(beat => quality[beat])
                .ThenByDescending(beat => source[beat].Count).ThenBy(beat => beat))
            {
                if (chosen.Count >= wanted && capacity >= quota)
                    break;
                if (fixedNotes.ContainsKey(beat) || chosen.Count >= maximumDistinct)
                    continue;
                int index = ~chosen.BinarySearch(beat);
                if (index > 0 && times[beat] - times[chosen[index - 1]] < tier.Gap - 1e-8 ||
                    index < chosen.Count && times[chosen[index]] - times[beat] < tier.Gap - 1e-8)
                    continue;
                chosen.Insert(index, beat);
                capacity += source[beat].Count;
            }
            if (capacity < quota || chosen.Count < wanted)
                return seed;
            var kept = chosen.ToDictionary(beat => beat, beat => fixedNotes.TryGetValue(beat, out var notes)
                ? notes.ToList() : new List<Note> { source[beat][0] });
            int remaining = quota - kept.Values.Sum(group => group.Count);
            foreach (double beat in chosen.OrderBy(beat => (seed.GetValueOrDefault(beat)?.Count ?? 0) < 2)
                .ThenByDescending(beat => quality[beat]).ThenBy(beat => beat))
            {
                if (remaining == 0)
                    break;
                var spares = source[beat].Where(note => !kept[beat].Contains(note)).Take(remaining).ToArray();
                kept[beat].AddRange(spares);
                remaining -= spares.Length;
            }
            return kept;
        }

        private static void Trade(
            Dictionary<double, List<Note>> kept, Dictionary<double, List<Note>> source,
            Dictionary<double, double> quality, Dictionary<double, double> times, Dictionary<double, int> phrase,
            ISet<int> fixedIds, HashSet<double> fixedBeats, HashSet<double> draft, Tier tier, bool easy)
        {
            var original = kept.GroupBy(pair => phrase[pair.Key]).ToDictionary(group => group.Key,
                group => group.Sum(pair => pair.Value.Count));
            var counts = new Dictionary<int, int>(original);
            var room = original.ToDictionary(pair => pair.Key, pair => Math.Max(1, (int)Math.Ceiling(pair.Value * .12)));
            int iterations = Math.Max(1, (int)Math.Floor(original.Values.Sum() * .04));
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var ordered = kept.Keys.OrderBy(beat => beat).ToArray();
                var additions = new List<(double Value, double Beat, Note Note)>();
                var removals = new List<(double Cost, double Beat, Note Note)>();
                foreach (var (beat, group) in source)
                {
                    int area = phrase[beat];
                    if (kept.ContainsKey(beat) || !original.ContainsKey(area) || counts[area] >= original[area] + room[area])
                        continue;
                    int index = ~Array.BinarySearch(ordered, beat);
                    if (index <= 0 || index >= ordered.Length)
                        continue;
                    double previous = ordered[index - 1], next = ordered[index];
                    double left = times[beat] - times[previous], right = times[next] - times[beat];
                    double nearest = Math.Min(left, right);
                    if (nearest < tier.Gap - 1e-8 || nearest > 1.75 * tier.Pulse)
                        continue;
                    bool paired = draft.Contains(beat) && (draft.Contains(previous) || draft.Contains(next));
                    double bridge = .12 * nearest / Math.Max(left, right) * Math.Min(1, (left + right) / (2 * tier.Pulse));
                    int hand = kept[previous][^1].Hand;
                    var note = group.OrderBy(note => note.Hand == hand).ThenBy(note => note.Y).ThenBy(note => note.Id).First();
                    additions.Add((quality[beat] + bridge + (paired ? .08 : 0), beat, note));
                }
                for (int index = 1; index + 1 < ordered.Length; index++)
                {
                    double beat = ordered[index];
                    int area = phrase[beat];
                    if (counts[area] <= Math.Max(1, original[area] - room[area]))
                        continue;
                    var notes = kept[beat];
                    var editable = notes.Where(note => !fixedIds.Contains(note.Id))
                        .OrderByDescending(note => note.Y).ThenByDescending(note => note.Id).ToArray();
                    if (editable.Length == 0)
                        continue;
                    double cost = .43 + .52 * quality[beat];
                    if (notes.Count == 1)
                    {
                        if (fixedBeats.Contains(beat) || !easy && draft.Contains(beat))
                            continue;
                        double left = times[beat] - times[ordered[index - 1]], right = times[ordered[index + 1]] - times[beat];
                        if (left + right > Math.Max(2 * tier.Pulse, 1.35 * Math.Max(left, right)) + 1e-8)
                            continue;
                        cost = quality[beat] + .13 * Math.Min(1, Math.Min(left, right) / tier.Pulse) + (draft.Contains(beat) ? .08 : 0);
                    }
                    removals.Add((cost, beat, editable[0]));
                }
                if (additions.Count == 0 || removals.Count == 0)
                    return;
                var incoming = additions.OrderByDescending(item => item.Value).ThenBy(item => item.Beat).First();
                var outgoing = removals.OrderBy(item => item.Cost).ThenBy(item => item.Beat).First();
                if (incoming.Value - outgoing.Cost < .18)
                    return;
                var groupToPrune = kept[outgoing.Beat];
                groupToPrune.Remove(outgoing.Note);
                if (groupToPrune.Count == 0)
                    kept.Remove(outgoing.Beat);
                kept.Add(incoming.Beat, new List<Note> { incoming.Note });
                counts[phrase[outgoing.Beat]]--;
                counts[phrase[incoming.Beat]]++;
            }
        }
    }
}
