# Release checklist

Copy this checklist into the release record and fill in the evidence. Unchecked items are pending, not passing.

## Decisions and identity

- [x] Project license: MIT, Copyright (c) 2026 Skyler; full text in root `LICENSE`.
- [ ] Choose the release version; pass it explicitly as `-Version major.minor.patch`.
- [ ] Record the source revision and ensure the intended source files are committed.
- [ ] Record the exact public ChroMapper version and download URL, Windows version, and Unity version used for validation.

## Build and automated checks

- [ ] Run the Python and Node commands in [contributing](contributing.md), or inspect the Checks workflow result.
- [ ] Build the layout bundle and plugin against the selected editor's assemblies.
- [ ] Record QuickJS-NG, libsndfile, esbuild, .NET SDK and model versions/provenance; retain the generated input hashes in `release-manifest.json`.
- [ ] Check the DLL assembly version matches the release and verify its SHA-256 against the manifest.
- [ ] Review readable dependency notices and include any additional notices/source materials required by the exact binaries distributed.
- [ ] Attach both platform DLLs to the same release and verify the README's latest-release download links after publishing.

## Clean installation and editor checks

Use a disposable copy of a map from `maps/test-candidates.json`. Keep original maps unchanged.

- [ ] Install only the release DLL in a clean public ChroMapper installation; launch without development Python, Node, or checkout dependencies.
- [ ] Open settings and timing editor; check layouts, waveform/audio, seeking, note ticks, and metronome.
- [ ] Exercise remove/restore, linked repeats, undo/redo, and Apply timings.
- [ ] Generate a full Standard spread and compare saved timing nesting and approved beat/hand/multiplicity signatures.
- [ ] Generate Easy first in a separate copy and compare automatic timing signatures under identical inputs/settings.
- [ ] Verify replacement confirmation, backups, and report paths, including `build-info.json`.
- [ ] Verify a saved-chart timing conflict prevents publication; inspect any draft/parity warnings and exported note counts.
- [ ] Close and reopen the song and generated charts. Confirm audio, metadata, walls, bombs, links, and jump settings survive export.
- [ ] If shipping custom-model support, exercise one API v2 package and a failing package.

## Evaluation and playtest record

- [ ] Record map keys, target difficulties, counts, extra hits, export failures, known regressions, and whether evaluation songs informed the rules. Keep reports in `maps/reports`.
- [ ] Playtest representative generated difficulties, especially transitions, crossovers, walls, bombs, and sparse passages. Record exact beats for remaining issues.
- [ ] Publish release notes listing tested compatibility, known limitations, and report/backup locations.

Automated checks do not establish editor compatibility or in-game playability.
