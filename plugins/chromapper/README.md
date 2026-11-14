# BS Downmapper for ChroMapper

Open a saved song in ChroMapper, press **Tab** to open the editor's extension panel, and click the **BS Downmapper** arrow. Save any chart edits you want to use before generating. The plugin reads saved Standard difficulties and writes the result to the open song.

- **Generate downmaps for** lists the Standard target difficulties on the left. Check one or more to include them, then click a checked difficulty's name to configure it. The highlighted row owns the settings on the right.
- **Use patterns from** selects the Standard chart that supplies the initial arrows and placements for the highlighted difficulty independently of its timing source.
- **Timing model** lists **Auto simplify**, installed automatic timing models, and saved Standard difficulties. **Edit** previews the selected rhythm and **Apply timings** saves your edits for the batch.
- Automatic timing chooses its starting chart. Any controls supplied by a custom timing model appear directly below the note budget. Hover over settings for floating descriptions.
- **Target note percentage** is available with automatic timing models. The presets show approximately 90% for Expert, 65% for Hard, 45% for Normal, and 25% for Easy. At the preset position, **Auto** uses the model's default budget. Moving away requests a percentage of the highest saved Standard chart. The built-in model honors the requested budget subject to eligible and protected timings. Custom models receive it as a suggested count.
- **Pattern model** selects the placement planner. **Advanced pattern settings** exposes the selected model's controls and the shared placement settings. [Add a model package](../../docs/custom-models.md)
- **Horizontal spread** favors each hand’s own outer lane. Normal starts at 0.70 and Easy at 0.60. **Crossover** controls reaches across the center. **Vertical spread** adjusts top-row use.
- **Edit** opens a song overview and detailed waveform. Click or drag in the overview to seek. White markers on the waveform are enabled and pulse when played. Gray markers are disabled. Click an editable marker to disable or restore its timing, or click a blue merge to split it back into separate hits. To restore a timing removed above, edit the higher chart first. Use **Undo** and **Redo** or ChroMapper's shortcuts. **Beat subdivision** controls snapping, and **Zoom** controls the visible range. Turn on **Follow playhead** to keep playback centered while the timeline scrolls. With it off, the playhead moves across the current page and the timeline advances at the edge. Pause snaps to the grid. **Note ticks** and **Metronome** start at ChroMapper's volume settings.
- **Link repeats** is on by default. Matching passages share a color and have labels such as **A1** and **A2**. Hover over a label and click a repeat to jump to its start. Removing or restoring a matched marker applies the same edit to its peers. Bookmark positions and inferred local downbeats help identify candidate sections, and the rhythms must still agree. A one-beat phrase extension can stay independent. Charts without bookmarks use rhythm-only detection. A fill or a different note count at an aligned beat stays independent. Inherited hits and active merges are skipped, as are locked hits during removal. Undo reverses the entire linked edit. Turn linking off for local edits or to review the ranges without sharing changes.
- **Link sub-sections** starts off. Enable it to edit corresponding hits in shorter phrases within the current section. Matching sub-sections have labels such as **A1-A1** and **A1-A2** on the same row as the section labels. The overview shows colored ranges for sections and, when enabled, sub-sections. This mode temporarily pauses song-wide linking, so a local edit stays within its section. Turn it off to return to the previous **Link repeats** setting.
- Repeat matching across the spread is always enabled with the **Nested timing ranker**. It prefers consistent rhythms in verified repeating sections by adjusting the shared timing order. This applies to all automatic Nested proposals in the spread. To make a manual exception after previewing, turn off **Link repeats** in the timing editor and edit the individual timing.

On Easy, automatic timings follow an elapsed-time pulse through active passages and leave space for sustained wall sequences and recurring bomb fills. Automatic pruning gives linked attacks extra weight across all tiers. Removing an attachment drops the link and leaves surviving hits as ordinary notes. Normal and Easy also drop links that exceed the tier's reach limit. Links on bomb-cued resets retain their protection.

Click **Generate** and confirm any replacements. The plugin prepares the checked charts from highest to lowest. Each selected lower chart uses only timings that survived in the selected charts above it. Unchecked charts stay in place, and any timings they still need are checked before the batch is saved. After validation, the plugin updates the song and returns to **Song Info**. Each generation run includes `build-info.json` identifying the assembly version. Run reports and backups are stored under `%LOCALAPPDATA%\bs-downmap\runs`.

When placement or direction search runs out of options, the plugin saves a draft for manual review. The plugin preserves the approved beats, hands, and note counts. It reports the failed check and known parity findings, and flags the whole difficulty if it had to use the initial pattern. Timing conflicts and file validation still apply. The result dialog lets you copy the review report path.

## Installation

Requires ChroMapper on Windows.

Close ChroMapper and move any previous BS Downmapper installation outside `Plugins`. Copy `BsDownmapper.ChroMapper.dll` directly into `Plugins`, then restart the editor. To keep custom API v2 models, copy their package folders into `Plugins/bs-downmap/models`.

The DLL uses ChroMapper's Unity runtime and embeds its built-in model, layouts, QuickJS and audio decoder. The plugin uses temporary runtime files during preview or generation and deletes them when the operation ends. Compatibility with a publicly downloadable ChroMapper build still needs release validation; see the [release checklist](../../docs/release-checklist.md).

Release downloads include a readable `THIRD-PARTY-NOTICES.txt`. The DLL also includes third-party notices as `BsDownmap.Licenses.*` resources. The bundled decoder is [libsndfile 1.2.2](https://github.com/libsndfile/libsndfile/tree/1.2.2). To use another compatible Windows x64 libsndfile build, set `BS_DOWNMAP_AUDIO_DECODER` to its absolute path before launching ChroMapper.

Generated difficulties use the [automatic jump policy](../../docs/pipeline.md#4-apply-jump-settings-validate-and-save).

## Edit the window layouts in Unity

Open `plugins/chromapper/layouts` as a separate Unity 6000.3.13f1 project. In the Project panel, open either prefab in `Assets/Layouts` in Prefab Mode.

- **MainSettings** contains the difficulty list, timing options, advanced scroll area, and action buttons. The prefab previews the expanded panel.
- **EditNoteTimings** contains the sidebar, overview, timeline, playback controls, and action buttons.

Adjust each group's spacing and padding, reorder controls within the body or footer, or resize a `Slot …` using its Layout Element. Keep the slot names and the advanced scroll area's viewport and content nesting. The **Frame** RectTransform sets the timing editor's bounds and the expanded menu's bounds.

Preview labels and backgrounds show the layout in Unity. ChroMapper supplies the modal frame, fonts, and live controls at runtime. The plugin still draws the waveform and note markers inside the timeline slot.

Save the prefabs, then choose **BS Downmapper → Build Layout Bundle**. Rebuild and replace the DLL with the new layout bundle embedded, then restart ChroMapper.
