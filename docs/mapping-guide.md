# Mapping mechanics and review

## Objects, timing, and movement

A colored note specifies a hand, lane, row, and cut direction. A dot gives direction choice. A double has both hands on the same beat. A stack is multiple same-hand notes cut together. A window is a separated stack. Inlines reuse the same cell at nearby timings and can hide the next note. Bombs occupy saber space and can cue a reset. Walls change head and body position. Arcs connect or extend gestures, and chains divide a gesture into a head and slices. Preserve linked note timing and geometry unless a deliberate simplification releases that link. [BSMG glossary](https://bsmg.wiki/mapping/glossary.html) · [Beatmap format](https://bsmg.wiki/mapping/map-format/beatmap.html)

Parity tracks the forehand or backhand state of each hand across swings. Arrow angle alone cannot determine wrist rotation. The time gap, reach, prior motion, and bomb or wall cues all affect whether a transition reads naturally. Judge streams by effective swing rate and recovery time alongside colored-note density. A typical fast alternating stream can start right down, left down, right up, left up. Keep a clear lead-in when that is the source gesture. [BSMG basic mapping](https://bsmg.wiki/mapping/basic-mapping.html) · [BSMG intermediate mapping](https://bsmg.wiki/mapping/intermediate-mapping.html)

Full swings and centered cuts affect score. A pattern can be technically reachable while demanding short swings or awkward preparation. Include expected scoring motion in review. [BSMG scoring guide](https://bsmg.wiki/ranking-guide.html#scoring)

## Difficulty and musical intent

- Preserve sparse passages that already suit the target. In a dense phrase, keep a coherent subdivision when deleting hits would break the rhythm, and simplify height, reach, arrows, or simultaneous motion instead.
- Use density changes to propose sections, then check musical entrances, handoffs and exits. Do not treat a section boundary as a new parity state. Look before and after it when repairing a transition.
- Parity adherence controls the tradeoff between alternating swing flow and spaced downstroke repeats. Expert defaults to 100%, Hard to 85%, Normal to 42%, and Easy to 30%. The placement check compares the finished chart with the full pattern foundation to distinguish authored errors from errors introduced by pruning.
- At higher adherence, the built-in automatic ranker can restore the missing hand of an authored double on a surviving beat before timing review. At 95% or higher, it can also restore a pruned hit already present in all saved higher charts. It prefers completing doubles, weighs recovery time, section boundaries, and timing accents, and skips protected resets and linked anchors. These restorations can raise the note count above the timing budget.
- Treat source counts as a mapper's heuristic. Expert often retains 80–100% of Expert+ notes, Hard 60–70%, Normal 40–50%, and Easy 20–30%. These are broad guides. Sequential pruning and protected links can produce different totals.
- On lower difficulties, favor clear drumming, recovery time, and reachable lanes. Use outward horizontals when they read naturally. Avoid excessive repeated up cuts, middle- or top-row downstrokes, deep crossovers, and quick inlines. Top-row dots and occasional height can make sense with time and preparation.
- Leave more space between converging cuts in doubles. Hard, Normal, and Easy score close windups on both axes, including cuts that move apart after the hit. If a double still scores poorly, the engine can try one more direction pass and keep it if it improves the score after accounting for changed notes and passes the same placement and parity checks. For parallel horizontal doubles, favor the leading hand above the trailing hand. On Hard, keep a shallow version of a deep crossover when it fits the surrounding swings. A demanding linked anchor can become a regular note at the same timing when the link has no bomb-reset role.
- **Horizontal spread** adjusts own-side outer-lane use, and **Crossover** adjusts cross-center placement. **Vertical spread** adjusts top-row use.
- Preserve intentional hand imbalance or a bomb-guided reset when it serves the phrase. Retain some expressive diagonals, horizontals and top-row hits as the difficulty permits. Simplifying a chart to hit an aggregate direction ratio can remove its character.
- Compare rhythmic choices by ear. Pickups, held notes and changes of instrument can matter more than transient loudness. Whole-beat interpretations can be appropriate when a human reference follows syncopation. Keep performed timestamps for isolated early or late attacks and use tempo regions when the underlying pulse changes. [BSMG downmapping](https://bsmg.wiki/mapping/downmapping.html) · [Open Music Theory](https://openmusictheory.github.io/syncopation.html)

## Placement review

| Family | What to check |
| --- | --- |
| Handclaps and opposite-hand collisions | Include preparation and follow-through. Outward arrows can still collide when swing paths overlap. |
| Stacks, windows, and dots | Same-hand groups need a shared cut path. Dots require surrounding swing context. Check simultaneous occupancy separately. |
| Sequential arm tangles | Carry arm order through crossovers and keep a reachable recovery path. |
| Visibility and inlines | Check central middle-row notes, upcoming bombs, crouch walls and immediately following notes at the actual NJS and offset. |
| Bombs and wall clearance | Review the full swing arc, body dodge and exit. A note can be outside an obstacle while still hidden. |
| Readability and reach | Check sudden height, wide moves, inward diagonals and short-gap direction changes in context. |

The checker uses geometric proxies and BSMap parity screening. Posture, arm motion, occlusion, and controller trajectories vary by player. Review flags and playtest in context, especially for arcs, chains, unusual wall paths, and intentional parity breaks. [Map Check](https://github.com/KivalEvan/BeatSaber-MapCheck) · [BSMap](https://github.com/KivalEvan/BeatSaber-JSMap)

## Regression examples

- **Hailstorm.** The opening can stay close to Expert+. At beats 32–36, preserving the stream rhythm with simpler vertical and diagonal movement worked better than converting it into a horizontal stream. Review the 30.5–31 transition, the 47.5 setup, and visibility after 48 and 61. Existing simultaneous-placement tests include the rejected handclap and misaligned stack patterns.
- **Move.** Keep the call-response around 73–74.5 and the bomb-led reset around 266–268. Compare wall recovery, diagonals, reach, and beginner drumming on the authored lower rhythms.
- **Iris.** Mixed meter and variable tempo make elapsed timing important. Check cross-section reach around 140.5–141.25, dense inlines, arc/chain survival, and transitions across tempo and meter changes. Treat short triplet hits differently in slow and fast passages.
