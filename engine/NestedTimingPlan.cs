using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BsDownmap.Engine
{
    public sealed record EasyDraftSettings(double MinimumGap, double Pulse, bool PhraseAccents,
        bool PhraseSwaps, bool PhraseBudget);

    public sealed record NestedTimingPlan(int[] Priority, IReadOnlyDictionary<string, int> Budgets);

    public static class NestedTimingPlanner
    {
        public static NestedTimingPlan Create(StandardLevel reference, TreeEnsemble model, MixAudio audio,
            IReadOnlyDictionary<string, RepeatTimingContext> contexts,
            IReadOnlyDictionary<string, IReadOnlyList<Note>> tierSeeds,
            IReadOnlyDictionary<string, ISet<double>> linkedAccents,
            IReadOnlyList<double> easyEndpointBeats, IReadOnlyList<DensitySection> easySections,
            IReadOnlyList<AlternatingStream> easyStreams, EasyDraftSettings settings,
            bool matchRepeats, CancellationToken cancellation = default)
        {
            var easy = contexts["Easy"];
            var easyAccents = linkedAccents["Easy"];
            var chart = reference.Gameplay;
            var clock = reference.Clock;
            var mix = audio.Score(reference.AudioPath, easy.Notes, clock.Seconds, reference.SongOffset, cancellation);
            var baseline = EasyPulse.Select(easy.Notes, chart.Bombs, chart.Walls, easy.ProtectedIds, easyAccents,
                clock.Seconds, settings.MinimumGap, settings.Pulse, settings.PhraseAccents, reference.ToGrid,
                settings.PhraseSwaps, settings.PhraseBudget).Notes;
            var features = NestedRankerFeatures.Build(easy.Notes, baseline, easy.ProtectedIds, mix,
                clock.Seconds, reference.ToGrid, reference.GridSeconds, settings.Pulse, cancellation);
            var scores = NestedTimings.ScoreSharedBudgets(model, features, chart.Notes.Count,
                tierSeeds.ToDictionary(pair => pair.Key, pair => pair.Value.Count));
            var draft = BuildDraft(reference, easy, easyAccents, easyEndpointBeats,
                easySections, easyStreams, settings, scores["Easy"], mix);
            var (priority, budgets) = NestedTimings.RankSharedProposals(chart.Notes, contexts, tierSeeds,
                linkedAccents, scores, mix, draft, reference.Bookmarks(), reference.EndBeat,
                reference.ToGrid, reference.FromGrid, clock.Seconds, matchRepeats);
            return new NestedTimingPlan(priority, budgets);
        }

        public static double[] BuildDraft(StandardLevel reference, RepeatTimingContext easy, ISet<double> easyAccents,
            IReadOnlyList<double> endpointBeats, IReadOnlyList<DensitySection> sections,
            IReadOnlyList<AlternatingStream> streams, EasyDraftSettings settings,
            IReadOnlyDictionary<double, double> rank, IReadOnlyDictionary<double, double> mix)
        {
            var chart = reference.Gameplay;
            var clock = reference.Clock;
            var ranked = EasyPulse.Select(easy.Notes, chart.Bombs, chart.Walls, easy.ProtectedIds, easyAccents,
                clock.Seconds, settings.MinimumGap, settings.Pulse, settings.PhraseAccents, reference.ToGrid,
                settings.PhraseSwaps, settings.PhraseBudget, rank, mix).Notes;
            var merges = TimingMerges.Propose(easy.Notes, ranked, sections, streams, easy.ProtectedIds,
                chart.Bombs, chart.Walls, endpointBeats, clock.Seconds);
            var refined = EasyDraftRefinement.Refine(easy.Notes, ranked, merges, easy.ProtectedIds,
                chart.Bombs, chart.Walls, rank, mix, clock.Seconds, reference.ToGrid,
                (reference.LegacyGrid ?? clock).Gap, settings.MinimumGap, settings.Pulse);
            return NestedTimings.DistinctBeats(TimingMerges.Apply(easy.Notes, refined, merges, easy.ProtectedIds));
        }
    }
}
