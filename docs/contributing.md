# Contributing and building

## Local setup

Use Python 3.12 and Node.js with npm. A .NET SDK and a ChroMapper installation are required to build the Windows plugin. Run these setup commands from the project root.

```powershell
py -3.12 -m venv .tools/onset-env
& .tools/onset-env/Scripts/python.exe -m pip install -r requirements.txt
New-Item -ItemType Directory -Path .tools/parity-runtime -Force | Out-Null
Copy-Item tools/parity-package.json .tools/parity-runtime/package.json
Copy-Item tools/parity-package-lock.json .tools/parity-runtime/package-lock.json
npm ci --prefix .tools/parity-runtime
```

## Test a change

```powershell
& .tools/onset-env/Scripts/python.exe -m unittest discover -s tools -p 'test_*.py'
Get-ChildItem tools/test_*.cjs | ForEach-Object { node $_.FullName; if ($LASTEXITCODE) { throw $_.Name } }
```

Run the editor host integration checks with the .NET SDK:

```powershell
dotnet build engine-host -c Release --artifacts-path .tools/editor-host-checks
& .tools/onset-env/Scripts/python.exe tools/check_editor_host.py .tools/editor-host-checks/bin/BsDownmap.Engine.Host/release/BsDownmap.Engine.Host.dll
```

Check the [evaluation guide](evaluation.md) for a local compliance run. When changing generation, also generate a Standard spread in a test copy, verify timing nesting, and inspect placement or parity failures. Include exact beat examples and what you observed in the editor or during playtesting. Keep song-specific observations in test metadata or fixtures and use general predicates in the engine.

The C# tree evaluator can be checked against the bundled rankers with the .NET 8 SDK and the Python development environment. The comparison uses the current feature extraction and timing selector for both runs. It checks the full nested priority order on the curated pool.

```powershell
dotnet build tools/RankerProbe.csproj -c Release --artifacts-path .tools/ranker-probe
$probe = ".tools/ranker-probe/bin/RankerProbe/release/RankerProbe.dll"
& .tools/onset-env/Scripts/python.exe tools/verify_csharp_rankers.py --maps maps/source --probe $probe --output maps/reports/csharp-ranker
```

Check the C# mix scorer on the same mono PCM input as the Python scorer. The report records onset score differences, priority order changes, and timing compliance for each default difficulty.

```powershell
& .tools/onset-env/Scripts/python.exe tools/verify_csharp_mix.py --maps maps/source --probe $probe --output maps/reports/csharp-mix/comparison.json
```

## Code map

| Area | Entry points |
| --- | --- |
| ChroMapper | `plugins/chromapper/DownmapPlugin.cs`, `EditorWindows.cs`, `TimingAudio.cs` |
| Editor preview and generation | `engine-host/EditorPreview.cs`, `engine-host/EditorGeneration.cs` |
| Timing selection | `engine/NestedTimings.cs`, `NestedTimingPlan.cs`, `SharedTimingSelection.cs`, `RankedEasy.cs` |
| Pattern planning and placement | `engine/NativeChartGenerator.cs`, `PatternPreparation.cs`, `PlacementSearch.cs` |
| Direction and parity helpers (QuickJS) | `tools/quickjs_dispatch.mjs`, `plan_directions.cjs`, `check_parity.cjs` |
| Save and validation | `engine/SpreadGeneration.cs`, `SpreadPublisher.cs`, `StandardSong.cs` |
| Python CLI and benchmarks | `tools/chromapper_export.py`, `rhythm_inputs.py`, `downmap.py`, `check_timing_compliance.py` |

Use Ruff format with a 110-character line length for Python and Prettier for the Node scripts.

## Package for GitHub Releases

Build the Windows layout bundle from the [plugin's Unity project](../plugins/chromapper/README.md#edit-the-window-layouts-in-unity). Run `plugins/chromapper/build.ps1` with these inputs.

- `-Version` is the release version in `major.minor.patch` form; it stamps the DLL and run reports.
- `-ChroMapperPath` points to the editor build you will test.
- `-NativeRanker` points to the shared percentage ranker's exported BDT file.
- `-Decoder` points to Windows x64 libsndfile 1.2.2, with its `COPYING` beside it.
- `-QuickJsExe` and `-QuickJsLicense` point to the Windows x64 QuickJS-NG executable and license.
- `-Esbuild` points to esbuild or its Windows npm shim.
- `-LayoutBundle` selects a Windows layout bundle built with the target editor's Unity version. For Stable 0.13.892, build it with Unity 2021.3.45f1. Use a copy of the layout project with uGUI 1.0.0 and regenerate its package lock.
- `-Output` overrides the default `dist/BsDownmapper.ChroMapper.dll` output.

The build compiles the engine and plugin against ChroMapper's managed assemblies. Test the DLL by itself in the editor's Plugins folder before publishing. Attach `BsDownmapper.ChroMapper.dll` to the GitHub release. The standalone host, Python, and Node remain available for development and benchmark workflows. [Custom model package format](custom-models.md)

The build also writes `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, and `release-manifest.json` beside the DLL. Attach all three alongside the DLL. The manifest records the DLL hash, build tool versions, and input hashes without local paths. Complete the [release checklist](release-checklist.md) before publishing.
