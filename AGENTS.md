# Agent quick-start

- Read [the pipeline](docs/pipeline.md), then [mapping review](docs/mapping-guide.md) or [evaluation](docs/evaluation.md) for the task at hand.
- The in-process ChroMapper build calls `engine-host/EditorPreview.cs` and `EditorGeneration.cs` from `plugins/chromapper/DownmapPlugin.cs`. Timing and placement code lives in `engine/`. QuickJS runs the direction and parity helpers. The Python tools remain available for CLI workflows and benchmarks.
- Keep engine rules independent of song names, mapper names, map IDs, or absolute beat ranges. Use `maps/test-candidates.json` to choose regression cases. Keep original maps in `maps/source/<key>`, generated copies in `maps/generated/<key>`, and reports in `maps/reports`.
- Keep timing candidates within all higher Standard charts and validate against saved lower charts before replacing a target. After rhythm approval, preserve the beat, hand and multiplicity signature through geometry and export.
- Test a structural change with a synthetic positive and negative case when existing coverage misses it. For timing changes, save a before/after compliance report on the same inputs. For model evaluation, hold out the entire song, including sibling difficulties.
- Run the Python and Node checks in [contributing](docs/contributing.md). For plugin changes, build against the installed ChroMapper assemblies. State which editor or in-game checks were performed.
- Treat timing compliance and placement checks as evidence about specific behaviors. Include extra hits, note counts, export failures, known regressions, and whether the evaluation songs informed the rule.
