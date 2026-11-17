# Timing compliance and model evaluation

## Local map layout

`maps/test-candidates.json` contains titles, song authors, mappers, base BPM, IDs, and mapper notes. Put unmodified maps in `maps/source/<key>/`, with `Info.dat` directly in each folder. Keep working copies and generated spreads in `maps/generated/<key>/`. Keep reports in `maps/reports/`. Create `maps/test-candidates.local.json` with the same `{"schemaVersion": 1, "maps": [...]}` shape to add private catalog entries. Original and generated maps, reports, and the local catalog are excluded from version control.

## Quick checks

The editor host's `editor-parity-preview` operation accepts a saved placement draft. Pass the map folder as `source`, the runtime root as `engine`, and a Standard `difficulty`. An optional `focus` prioritizes an error and a double restoration beat. Hand values are 0 for left and 1 for right.

```powershell
$request = @{
    engine = (Get-Location).Path
    source = (Resolve-Path 'maps/generated/move-parity-ab-100/new/1e1e5').Path
    difficulty = 'Expert'
    focus = @{ errorBeat = 264.75; hand = 1; restoreBeat = 263 }
}
$request | ConvertTo-Json | dotnet .tools/editor-host-checks/bin/BsDownmap.Engine.Host/release/BsDownmap.Engine.Host.dll editor-parity-preview
```

The response includes final proposed notes, tried patches and their costs, linked occurrences, and newly introduced errors. The preview uses 100% adherence. It can add a hand to an existing beat or restore an eligible beat from the higher charts.

Pass `target-note-fraction` to use the intended timing budget. The preview applies the fraction to the highest saved Standard chart above the target. Without an override, it uses the midpoint of the target profile's note-ratio range. The two ratio costs reward edits that move the note count toward this budget or move the left-hand share toward the immediate higher chart's share. For linked edits, both costs use the full patch, including mirrored copies.

After nearby beats-preserving repairs, the preview checks distant opposite-hand singles back to the previous swing of the error hand. Timing restoration is the last pass. It uses the immediate higher chart's verified marker links, restores missing eligible occurrences together, and keeps a proposal only if its follow-up repairs clear any new errors. Set `restore-linked-timings` to `false` to disable this last pass.

Run these commands from the project root with the Python environment described in [contributing](contributing.md).

```powershell
$python = ".tools/onset-env/Scripts/python.exe"
& $python tools/check_timing_compliance.py --map 40a54 --output maps/reports/before.json
& $python tools/check_timing_compliance.py --map 40a54 --baseline maps/reports/before.json --output maps/reports/after.json
& $python tools/check_timing_compliance.py --map 40a54 --candidate-root maps/generated --output maps/reports/iris-spread.json
```

The quick proposal runs apply saved-chart constraints. The nested ranker orders the highest saved chart. The saved-spread run measures what survived sequential generation and checks the actual spread's hierarchy. Repeat `--map` or `--difficulty` to narrow a test. Compare the same mode and references when measuring a change. The report includes counts and baseline deltas. Keep the note budget fixed when isolating model quality, since more generated hits can inflate recall. Check reference fingerprints, extra hits, and changes per difficulty.

For a controlled cascade comparison, the automatic Easy CLI accepts `--easy-note-count` to request the baseline count on the same nested inputs. The engine reports an error if that count cannot fit. Run the unadjusted defaults separately when measuring the behavior a mapper sees.

Beat compliance is matched distinct colored-hit timings divided by authored timings. Beat precision divides by generated timings. Note compliance counts the smaller number of notes on each matching beat and divides by the authored note count. An extra hit is a generated timing with no authored match. Geometry quality and playability require separate evaluation.

To check the default nested order at a custom budget, preview a source folder:
```powershell
& $python tools/chromapper_export.py --source maps/source/40a54 --source-difficulty ExpertPlus --difficulty Expert --rhythm-mode best-guess --target-note-fraction 0.90 --preview
``` Use the same reference and saved-chart constraints when comparing fractions. A smaller fraction must only keep timings present at the larger fraction. For a workflow check, generate a full spread in one test copy and generate Easy first in another, then compare the saved timing and hand signatures.

## Train the scoring model used by the Nested ranker

From the source checkout, pass a local source-map folder and the per-map JSON details from a fixed-count spread run.

```powershell
& $python tools/train_shared_timing_model.py --source-root maps/source --budget-details maps/reports/fixed-count/details
```

The trainer uses the generated note budgets to condition its scores and the authored Standard charts as labels. The resulting asset records the feature schema, dependency version, and reference fingerprints. Keep held-out evaluation separate from the packaged model, which is fitted on the full curated training pool.

## Repeat preference benchmark

Compare the Nested ranker with its default repeat matching on and off:

```powershell
& $python tools/benchmark_nested_repeats.py --source-root maps/source --budget-details maps/reports/fixed-count/details --output maps/reports/nested-repeat-matching
```

The benchmark fits once per held-out song. Both modes use the same meter-aware links to score authored variations. It checks that every position in the two priority orders uses the same phrase and hand, which keeps those counts fixed at every budget.

Run a matched leave-one-song-out comparison of an experimental repeat pass with local maps and fixed-count training details.

```powershell
& $python tools/benchmark_rhythm_links.py --source-root maps/source --budget-details maps/reports/fixed-count/details --output maps/reports/repeat-preference
```

Each fold fits once on the other songs. It creates unlinked nested timing proposals, then tests the legacy detector, the editor's rhythm-only and bookmark-guided detectors, and both editor detectors with longer rhythm searches. The rhythm-only arms ignore bookmarks on every song. Each arm keeps the same 48-beat phrase and hand counts. Existing lower-tier proposals are protected during upper-tier swaps to keep the paired comparison feasible.

All arms share the same variation reference. It scores exact keep/omit agreement where authored repeats differ, recall of kept hits, preservation of omissions, and unmatched hits within repeat ranges. The report also includes scores against the legacy repeat groups to compare with earlier runs. The comparison evaluates timing proposals before placement and export.
