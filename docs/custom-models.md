# Add a timing or pattern model

Put each model in its own folder under `ChroMapper/Plugins/bs-downmap/models/`. Reopen BS Downmapper to refresh its **Timing model** and **Pattern model** lists. Select a timing and a pattern model independently for each target difficulty. Custom timing models are available in the **Timing model** dropdown. Settings are kept per target and model while the dialog is open. A source checkout also discovers packages in `models/custom/` by default. Set `BS_DOWNMAP_MODEL_DIR` to select another directory when using the CLI.

Install model packages you trust. They run with the same permissions as ChroMapper. Keep loose DLLs out of the model folder because the editor scans plugin subfolders for assemblies.

## ChroMapper executable packages

Use `"apiVersion": 2` and set `"entry"` to a Windows executable inside the model folder. The plugin launches the executable with its package folder as the working directory and sends a UTF-8 JSON object on standard input with `kind`, `context`, and `settings`. It closes standard input after sending the request. Write a single JSON result to standard output and exit with code zero. Write diagnostics to standard error. The manifest settings and result shapes below also apply to executable packages.

## Legacy Python packages for CLI development

API version 1 uses the Python adapter from the source checkout. The single-DLL ChroMapper plugin discovers executable packages.

## Manifest

Create `model.json` beside your entry script and any model data. Use `__file__` in the script to locate data in the same package.

```json
{
  "apiVersion": 1,
  "id": "custom-accent-pruner",
  "kind": "timing",
  "label": "Accent pruner",
  "entry": "model.py",
  "settings": {
    "fraction": {
      "type": "number",
      "label": "Keep fraction",
      "description": "Fraction of eligible source notes to retain.",
      "default": 0.6,
      "min": 0.1,
      "max": 1.0,
      "step": 0.01
    },
    "preferWholeBeats": {
      "type": "boolean",
      "label": "Prefer whole beats",
      "default": true
    },
    "tiebreak": {
      "type": "choice",
      "label": "Equal-score notes",
      "default": "earliest",
      "options": ["earliest", "latest"]
    }
  }
}
```

Use a unique lowercase ID starting with `custom-` and set `kind` to `timing` or `pattern`. `settings` is optional. Each field becomes a slider, checkbox, or dropdown. The key identifies the value in Python. `description` supplies hover help and `step` defaults to 0.01.

## Timing callback

`select_timings(context, settings)` returns a nonempty list of distinct eligible note IDs. The context includes the target `difficulty`, `level` containing the eligible notes, `reference` containing the highest available chart when provided, a `suggested_count` (derived from the chart or the target percentage), `protected_ids` and `required_beats`. A note includes `id`, beat `b`, hand `c`, position `x` and `y`, cut direction `d`, and angle offset `a`. The chart contains `bpm` and a tempo `clock` with `seconds(beat)`.

```python
def select_timings(context, settings):
    notes = context["level"]["notes"]
    prefer_whole = settings["preferWholeBeats"]
    reverse_time = settings["tiebreak"] == "latest"
    ranked = sorted(notes, key=lambda note: (
        prefer_whole and abs(note["b"] - round(note["b"])) > 0.0001,
        -note["b"] if reverse_time else note["b"],
    ))
    count = max(1, round(len(notes) * settings["fraction"]))
    return [note["id"] for note in ranked[:count]]
```

The engine retains eligible bomb-cued reset notes and required beats from saved lower charts. It limits the input against higher charts and validates the spread before saving. The usual timing editor can review and edit the proposal. Generating separately in different orders can yield different selections if a custom model uses different rankings across tiers or the available higher charts change.

## Pattern callback

For a package with `kind: "pattern"`, implement `seed_patterns(context, settings)`. It receives `seeds` containing the engine's initial positions and directions for the accepted rhythm, the full `foundation` and `rhythm` charts, the target `difficulty`, and `protected_ids`. Return a dictionary keyed by note ID with any of `x` (0–3), `y` (0–2), `d` (0–8) or `a` (−180 to 180) as integer overrides. Returning an empty dictionary leaves all seeds unchanged.

```python
def seed_patterns(context, settings):
    return {
        note["id"]: {"y": 0}
        for note in context["seeds"]
        if note["id"] not in context["protected_ids"] and note["y"] > 0
    }
```

The engine keeps note beats, hands, and multiplicity fixed, preserves linked and bomb-reset seeds, and continues through its usual direction search, placement checks, and repair. The generated geometry may differ from a proposal when the planner finds a better or safer placement.

For CLI use, pass `--timing-model custom-accent-pruner` with `--rhythm-mode best-guess` and/or `--pattern-model custom-your-pattern`. Optional `--timing-model-settings` and `--pattern-model-settings` each take a JSON file containing values keyed by setting name.
