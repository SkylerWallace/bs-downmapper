# How downmapping works

## 1. Choose pattern and rhythm inputs

The pattern chart supplies the starting motion vocabulary. A separate rhythm input supplies beat times, hands, simultaneous note counts, and the accompanying walls, bombs, and lighting. Both inputs use Standard difficulties and the same audio and tempo map. The timing source can be an authored chart, an automatic preview, or a reviewed timeline.

The default **Nested timing ranker** builds a single priority order from Expert+, or from the highest saved Standard chart when Expert+ is absent. Each lower budget takes a shorter prefix of that order. A full spread can still be generated Expert through Easy, and generating Easy first gives the same automatic timings when the same model, source, and settings generate the other charts.

Saved charts constrain that order. A timing must exist in every saved higher Standard chart to be eligible. If a saved lower chart already uses a timing, an upper chart must retain it. These constraints can change the proposal on a mixed authored and generated spread, and protected timings can raise the effective note budget. The engine validates both directions before writing with a 0.0001-beat tolerance. An explicit starting chart supplies its own hands and double counts within the eligible timings.

## 2. Select hits and review the rhythm

The timing rankers consider elapsed-time gaps, source rests, local density, recurring accents and handoffs, and linked or reset-critical notes. Higher difficulty profiles combine their existing section budgets and grid preferences with model confidence. On Easy, a separate pulse search favors a playable attack cadence, plans substitutions, and can transfer a second hit from a double to a stronger eligible accent. It can retain useful triplet and half-beat attacks at slow tempos and preserve expressive microtiming.

Normal and Easy also look at the pulse and accent scores on the Expert+ budget reference. They can replace a selected attack with a nearby eligible hit or turn the second note of a double into a spaced hit in the same eight-beat window. These trades preserve the selected note count and hands and protect linked notes, bomb-reset cues, and the inputs to previewed merges. Easy may fit a different number of notes when the saved Normal rhythm changes.


**Nested timing ranker** uses the bundled shared ranker’s scores and independent difficulty proposals on Expert+ to construct the fixed order. Repeat matching is on by default. The ranker verifies repeated sections using the meter-aware detector, then exchanges close-scoring, same-hand timings within a phrase. Each exchange crosses one standard budget cutoff, so the other standard difficulties keep their selections and every budget still takes a prefix of the same order. Unmatched fills and protected notes keep their priorities.

The timing editor lets you remove or restore existing markers and split automatic merges. Automatic merges move two nearby singles onto an existing eligible beat. The preview tests bookmark boundaries and locally inferred downbeats as candidate section boundaries, then verifies the rhythms in the chart's beat grid. Meter hypotheses use recurring accents, so the phase can move across a song and need not align with beat zero. A one-beat ending extension can be left outside the shared pattern. Bookmark names do not affect matching. Rhythm-only detection covers unmarked charts and uncovered ranges. Only aligned hits with matching note counts share edits. Within-section linking tests shorter repeated phrases within each bookmarked or detected section and keeps those edit groups separate from song-wide links. When several targets are checked, the plugin prepares them from highest to lowest. Each selected lower chart can only keep timings that survived in the selected charts above it.

Bookmark guidance combines embedded modern and legacy markers with Official Editor bookmark sets for the source Standard difficulty. The timing editor also verifies phrase-length candidates inferred from the opening beat phase and recurring offsets. It favors verified windows with a density change at the edge. When a long phrase and its following phrase each repeat together in at least three occurrences, the editor keeps their links separate and checks for short verified lead-ins before searching uncovered ranges.

## 3. Seed shapes and plan transitions

The native host's experimental `repair-hand-assignments` request option lets high-adherence automatic timing proposals complete a single, move it to the opposite hand, or restore a short run of authored doubles before approval. These edits retain the same distinct beats and skip reset cues and linked anchors. Candidates must clear the targeted error without adding another in the timing audit. The finished placement still gets its own parity check. The option is disabled by default.

Map the accepted rhythm to source shapes, preferring the same beat and hand. Group density windows and streams in elapsed time. Detect movement cues such as repeated alternating hand pairs and bomb-guided parity resets. Use section-specific reach, height, crossover, diagonal and readability preferences. Evaluate preceding and following swings across section boundaries.

The direction planner searches per-hand paths using elapsed recovery time and supported placement domains. The geometry pass checks same-beat swings, arm order, sequential visibility, walls, bombs and controller-clearance proxies. A short same-hand repeat may be acceptable for beginner drumming. Faster repeats and unprepared resets require stronger evidence.

## 4. Apply jump settings, validate, and save

At high parity adherence, the editor checks nearby cut-direction alternatives from a saved swing state after the first legal placement. Trials run through a bounded number of swings and must rejoin the original state without adding an error. The engine checks geometry at the existing cells and audits the finished chart before accepting edits. If errors remain, the editor reports them with the draft and continues the batch.

For an unreviewed built-in automatic rhythm at 95% parity adherence or higher, the engine can also try turning singles into doubles, thinning doubles, mirroring singles to the opposite hand, and combining up to three edits on nearby beats. It can also test restoring four to six closely spaced authored doubles as a unit. It prefers fewer errors, smaller edits, and authored hand assignments. All candidates keep the distinct beats. The search can adjust the lead-in to an arc or chain while keeping the anchor fixed. Bombs and protected resets bound the repair window. The engine accepts a candidate only after placement clears the targeted error, preserves protected notes and inherited findings, and introduces no new errors.

Use the base Expert+ NJS from the song. Easy and Normal use 10. Hard uses one third of the difference between 10 and Expert+ NJS, added to 10. Expert uses two thirds. Round Hard and Expert down to the nearest 0.5. For an absent, invalid, or sub-10 Expert+ NJS, use 10, 10, 12, 14, and 16 from Easy through Expert+. The engine reads Expert+ as an input and generates only the lower four tiers.

Half jump duration (HJD) is measured in beats. Jump distance is twice NJS times HJD in seconds. The offset changes HJD in quarter-beat steps. The engine targets 22–28 m for Expert and Expert+ and 24–30 m for Easy through Hard, with an allowed range of 20–32 m. It chooses the feasible distance nearest the midpoint of the preferred band and reports an error when no quarter-beat setting fits. It uses the base BPM and constant target NJS for both placement checks and export. [BSMG jump settings](https://bsmg.wiki/mapping/intermediate-mapping.html#jump-settings)

The output keeps the approved rhythm signature and checks gameplay metadata, parity, and geometry. The plugin confirms which files it will replace, stages the selected charts, then checks the complete spread under the save lock before publishing. It backs up replaced chart data and refreshes Song Info. Keep the final editor review and playtest separate from automated validation.
