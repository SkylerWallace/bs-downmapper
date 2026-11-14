using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace BsDownmap.Engine
{
    public sealed record PreparedTiming(
        StandardLevel Level, RepeatTimingContext Context, TimingLinkPlan Links, ISet<int> ResetIds,
        IReadOnlyList<DensitySection> Sections, IReadOnlyList<AlternatingStream> Streams);

    public sealed record NativeReferencePlan(
        StandardLevel Level, NestedTimingPlan Plan, ISet<int> Mandatory);

    public static class TimingPreparation
    {
        public static readonly IReadOnlyList<string> Standard = Array.AsReadOnly(
            new[] { "ExpertPlus", "Expert", "Hard", "Normal", "Easy" });

        public static PreparedTiming Prepare(
            StandardLevel level, JsonObject profile, IReadOnlyDictionary<string, double[]> higher = null,
            bool automatic = true, bool preserveSourceCues = false)
        {
            var gameplay = level.Gameplay;
            var full = gameplay.Notes;
            var notes = higher == null || higher.Count == 0 ? full : full.Where(note =>
                higher.Values.All(beats => NestedTimings.Nearest(note.Beat, beats) >= 0)).ToArray();
            if (notes.Count == 0)
                throw new InvalidDataException("The higher Standard difficulties have no common hit timings.");
            var eligibleIds = notes.Select(note => note.Id).ToHashSet();
            var reset = MappingIntent.Detect(preserveSourceCues ? full : notes, gameplay.Bombs, level.Clock,
                new HashSet<int>()).ResetNotes();
            reset.IntersectWith(eligibleIds);

            var released = gameplay.ReadLinks().Where(link => !link.AnchorIds.IsSubsetOf(eligibleIds))
                .Select(link => link.Id).ToHashSet();
            bool easy = profile["automaticTimingMinimumGapSeconds"] != null;
            if (easy && automatic)
                released.UnionWith(gameplay.EasyStaggeredLinks(level.Clock,
                    Value(profile, "automaticTimingMinimumGapSeconds"), reset, eligibleIds));
            double? maxReach = profile["maximumLinkedReach"] == null ? null : Value(profile, "maximumLinkedReach");
            var links = gameplay.PrepareTimingLinks(reset, maxReach, eligibleIds: eligibleIds, alreadyReleased: released);
            var locked = reset.Concat(links.LockedAnchors).ToHashSet();
            var (sections, streams) = PatternFoundation.Sections(notes,
                (int)Value(profile, "densityWindowBeats"), (int)Value(profile, "regularStreamMinimumNotes"),
                Value(profile, "regularStreamMaximumGapBeats"), level.Clock.Gap);
            var (windows, accents) = easy ? SectionTiming.EasySparseCues(notes, gameplay.Walls, gameplay.Bombs, level.Clock.Seconds)
                : (Array.Empty<RepeatWindow>(), new HashSet<double>());
            return new PreparedTiming(level, new RepeatTimingContext(notes, locked, accents, windows),
                links, reset, sections, streams);
        }

        public static NativeReferencePlan Rank(StandardLevel reference, IReadOnlyDictionary<string, JsonObject> profiles,
            TreeEnsemble ranker, MixAudio mix, bool matchRepeats, CancellationToken cancellation = default)
        {
            var prepared = Standard.Skip(1).Reverse().ToDictionary(tier => tier, tier => Prepare(reference, profiles[tier]));
            var contexts = prepared.ToDictionary(pair => pair.Key, pair => pair.Value.Context);
            var seeds = prepared.ToDictionary(pair => pair.Key, pair => Seed(pair.Value, profiles[pair.Key], reference.Gameplay.Notes));
            var accents = prepared.ToDictionary(pair => pair.Key, pair => pair.Value.Links.AccentBeats);
            var easy = prepared["Easy"];
            var plan = NestedTimingPlanner.Create(reference, ranker, mix, contexts, seeds, accents,
                easy.Links.Endpoints.Select(endpoint => endpoint.Beat).ToArray(), easy.Sections, easy.Streams,
                EasySettings(profiles["Easy"]), matchRepeats, cancellation);
            var mandatory = contexts.Values.SelectMany(context => context.ProtectedIds).ToHashSet();
            return new NativeReferencePlan(reference, plan, mandatory);
        }

        public static IReadOnlyList<Note> Seed(PreparedTiming prepared, JsonObject profile, IReadOnlyList<Note> budgetReference = null)
        {
            var context = prepared.Context;
            var level = prepared.Level;
            if (profile["automaticTimingPulseSeconds"] != null)
            {
                var easy = EasySettings(profile);
                return EasyPulse.Select(context.Notes, level.Gameplay.Bombs, level.Gameplay.Walls, context.ProtectedIds,
                    prepared.Links.AccentBeats, level.Clock.Seconds, easy.MinimumGap, easy.Pulse, easy.PhraseAccents,
                    level.ToGrid, easy.PhraseSwaps, easy.PhraseBudget).Notes;
            }

            return SectionTiming.Prune(context.Notes, prepared.Sections, prepared.Streams, context.ProtectedIds,
                Value(profile, "minimumSourceNoteRatio"), Value(profile, "maximumSourceNoteRatio"), level.Clock.Seconds,
                new Dictionary<double, double>(), prepared.Links.AccentBeats, budgetReference);
        }

        public static IReadOnlyList<Note> Select(PreparedTiming source, NativeReferencePlan reference,
            string target, IEnumerable<double> required, double? fraction = null)
        {
            bool referenceSource = string.Equals(source.Level.Filename, reference.Level.Filename, StringComparison.Ordinal);
            int budget = reference.Plan.Budgets[target];
            if (fraction.HasValue)
                budget = BudgetFromFraction(reference.Level.Gameplay.Notes.Count, fraction.Value);
            var mandatory = source.Context.ProtectedIds.ToHashSet();
            if (referenceSource)
                mandatory.UnionWith(source.Context.Notes.Where(note => reference.Mandatory.Contains(note.Id)).Select(note => note.Id));
            return NestedTimings.Select(reference.Level.Gameplay.Notes, reference.Plan.Priority, source.Context.Notes,
                budget, mandatory, required, referenceSource);
        }

        internal static int BudgetFromFraction(int referenceCount, double fraction)
        {
            if (!(fraction > 0 && fraction <= 1))
                throw new InvalidDataException("Timing budget fraction must be above zero and at most one.");
            return Math.Max(1, (int)Math.Floor(referenceCount * fraction + .5));
        }

        private static EasyDraftSettings EasySettings(JsonObject profile) => new(
            Value(profile, "automaticTimingMinimumGapSeconds"), Value(profile, "automaticTimingPulseSeconds"),
            Flag(profile, "automaticTimingPhraseAccents"), Flag(profile, "automaticTimingPhraseSwaps"),
            Flag(profile, "automaticTimingPhraseBudget"));

        private static bool Flag(JsonObject profile, string key) =>
            profile[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;

        private static double Value(JsonObject profile, string key)
        {
            double value = StandardSong.Number(profile[key]);
            return double.IsFinite(value) ? value : throw new InvalidDataException($"Invalid profile field {key}.");
        }
    }
}
