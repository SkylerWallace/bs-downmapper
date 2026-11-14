using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    public sealed partial class TimingWindow
    {
        private TextBoxComponent precision;
        private TMP_InputField precisionInput;
        private ButtonComponent playButton, stopButton, undoButton, redoButton;
        private TMP_Text playButtonText;
        private TextComponent positionText;
        private TimingOverview overview;
        private double nextPositionReadout;
        private TimingTimeline timeline;
        private TimingWaveform waveform;
        private TimingEdits appliedEdits;
        private bool timelineDirty = true;

        private void BuildDialog(DialogBox dialog, DialogLayout layout)
        {
            // Add through the dialog so its native navigation and focus management own the controls.
            playButton = dialog.AddComponent<ButtonComponent>().WithLabel("Play").OnClick(() =>
            {
                heldPlayback = false;
                TogglePlayback();
            });
            layout.AttachBody(playButton.transform.parent, true, clearFramePadding: true);
            Place(layout, playButton, "Play");

            undoButton = AddButton(dialog, layout, "Undo", () => StepHistory(true));
            redoButton = AddButton(dialog, layout, "Redo", () => StepHistory(false));
            var linkToggle = AddSidebarToggle(dialog, layout, "LinkRepeats", "Link repeats", linkSections, enabled =>
            {
                linkSections = enabled;
                timelineDirty = true;
                PlayerPrefs.SetInt(LinkSectionsPreference, enabled ? 1 : 0);
            });
            linkToggle.Selectable.interactable = data.linkedMarkers.Count > 0;
            var localToggle = AddSidebarToggle(dialog, layout, "LinkSubSections", "Link sub-sections", false, enabled =>
            {
                linkWithinSection = enabled;
                linkToggle.Selectable.interactable = !enabled && data.linkedMarkers.Count > 0;
                hoveredSection = null;
                overview.ShowPhrases(enabled);
                timelineDirty = true;
            });
            localToggle.Selectable.interactable = data.subLinkedMarkers.Count > 0;
            AddSidebarToggle(dialog, layout, "FollowPlayhead", "Follow playhead", followPlayhead, enabled =>
            {
                followPlayhead = enabled;
                PlayerPrefs.SetInt(FollowPlayheadPreference, enabled ? 1 : 0);
            });

            var overviewSlot = layout.Slot("Overview").gameObject;
            overviewSlot.AddComponent<RectMask2D>();
            overview = overviewSlot.AddComponent<TimingOverview>();
            (double, double, Color) Range(TimingSection section) =>
                (Seconds(section.Start), Seconds(section.End), SectionColors[section.Group % SectionColors.Length]);
            overview.Initialize(SeekOverview, data.sections.Select(Range).ToArray(), data.subSections.Select(Range).ToArray());

            playButtonText = playButton.GetComponentInChildren<TMP_Text>();
            stopButton = AddButton(dialog, layout, "Stop", StopAtPageStart);
            positionText = AddText(dialog, layout, "Position", "");

            var grid = layout.Slot("Timeline").gameObject;
            grid.AddComponent<RectMask2D>();
            timeline = grid.AddComponent<TimingTimeline>();
            timeline.Initialize(this, positionText.GetComponentInChildren<TMP_Text>().font);

            AddText(dialog, layout, "SubdivisionLabel", "Beat subdivision");
            precision = dialog.AddComponent<TextBoxComponent>();
            precision.WithContentType(TMP_InputField.ContentType.IntegerNumber).WithMaximumLength(10)
                .OnEndEdit(value => SetSubdivision(int.TryParse(value, out int parsed) ? parsed : subdivision));
            precisionInput = (TMP_InputField)precision.Selectable;
            Place(layout, precision, "Subdivision");
            var inputContainer = precisionInput.transform.parent;
            if (inputContainer.parent == precision.transform)
                foreach (Transform child in precision.transform)
                    if (child != inputContainer) child.gameObject.SetActive(false);
            precision.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset();
            Size(inputContainer, 0, 22);
            Size(precisionInput.transform, 0, 22);
            AddSlider(dialog, layout, "Zoom", "Zoom", 0, 4, 6 - zoom, value =>
            {
                zoom = 6 - value;
                span = Mathf.Pow(2, zoom);
                FollowCursor();
            }, null);

            var metronome = AddSlider(dialog, layout, "Metronome", "Metronome", 0, 1, metronomeVolume, value =>
            {
                if (metronomeVolume > 0 && value == 0) audio.StopMetronome();
                metronomeVolume = value;
            }, value => value.ToString("P0"));
            metronome.Selectable.interactable = audio.HasMetronome;
            var ticks = AddSlider(dialog, layout, "NoteTicks", "Note ticks", 0, 1, noteTickVolume, value =>
            {
                if (noteTickVolume > 0 && value == 0) audio.StopNoteTicks();
                noteTickVolume = value;
            }, value => value.ToString("P0"));
            ticks.Selectable.interactable = audio.HasNoteTicks;
            var apply = dialog.AddFooterButton(() => appliedEdits = new TimingEdits
            {
                sourceSha256 = data.sourceSha256, removedNoteIds = removed.OrderBy(id => id).ToArray(),
                proposedHandIds = data.proposedHandIds,
                merges = merges.OrderBy(merge => merge.from).ToArray()
            }, "Apply timings").WithBackgroundColor(new Color(.313025f, .3572f, .475f));
            layout.AttachFooter(apply.transform.parent);
            layout.Mount(apply, "Apply", compact: false);
            layout.Mount(dialog.AddFooterButton(null, "Cancel"), "Cancel", compact: false);
        }

        private void LateUpdate()
        {
            var playLabel = playing ? "Pause" : "Play";
            if (playButtonText.text != playLabel) playButtonText.text = playLabel;
            playButton.Selectable.interactable = stopButton.Selectable.interactable = audio.Ready;
            undoButton.Selectable.interactable = undo.Count > 0;
            redoButton.Selectable.interactable = redo.Count > 0;
            double seconds = Seconds(cursor);
            if (!playing || Time.unscaledTimeAsDouble >= nextPositionReadout)
            {
                nextPositionReadout = Time.unscaledTimeAsDouble + .05;
                var elapsed = TimeSpan.FromSeconds(Math.Max(0, seconds));
                SetText(positionText, $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}  •  " +
                    $"Beat {cursor:0.###}  •  {60 / SecondsPerBeat(cursor):0.##} BPM");
            }
            overview.Refresh(waveform, OverviewDuration, Seconds(start), Seconds(start + span), seconds);
            timeline.Refresh(timelineDirty, start, span, cursor);
            timelineDirty = false;
        }

        private static void SetText(TextComponent field, string value)
        {
            if (field.Value != value) field.Value = value;
        }

        private static LayoutElement Size(Transform child, float width, float height)
        {
            var size = child.GetComponent<LayoutElement>() ?? child.gameObject.AddComponent<LayoutElement>();
            size.layoutPriority = 2;
            size.minWidth = size.preferredWidth = width;
            size.minHeight = size.preferredHeight = height;
            size.flexibleWidth = width == 0 ? 1 : 0;
            size.flexibleHeight = 0;
            return size;
        }

        private static void Place(DialogLayout layout, CMUIComponentBase component, string slot)
        {
            layout.Mount(component, slot, compact: false);
            foreach (var label in component.GetComponentsInChildren<TMP_Text>(true))
            {
                label.enableAutoSizing = false;
                label.fontSize = 15;
                label.enableWordWrapping = false;
            }
        }

        private static void ConfigureText(TextComponent field)
        {
            var label = field.GetComponentInChildren<TMP_Text>(true);
            label.enableAutoSizing = false;
            label.fontSize = 15;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
        }

        private static TextComponent AddText(DialogBox dialog, DialogLayout layout, string slot, string value)
        {
            var field = dialog.AddComponent<TextComponent>();
            field.Value = value;
            Place(layout, field, slot);
            ConfigureText(field);
            return field;
        }

        private static ToggleComponent AddSidebarToggle(DialogBox dialog, DialogLayout slots, string slot, string label,
            bool value, Action<bool> changed)
        {
            var toggle = dialog.AddComponent<ToggleComponent>();
            toggle.WithLabel(label);
            toggle.Value = value;
            toggle.OnChanged<ToggleComponent, bool>(changed);
            Place(slots, toggle, slot);
            var layout = toggle.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(3, 3, 0, 0);
            layout.childForceExpandWidth = false;
            var checkbox = toggle.Selectable.transform.parent;
            if (checkbox.parent == toggle.transform)
                foreach (Transform child in toggle.transform)
                    Size(child, child == checkbox ? 22 : 0, -1);
            return toggle;
        }

        private static ButtonComponent AddButton(DialogBox dialog, DialogLayout layout, string label, Action action)
        {
            var button = dialog.AddComponent<ButtonComponent>().WithLabel(label).OnClick(action);
            Place(layout, button, label);
            return button;
        }

        private static SliderComponent AddSlider(DialogBox dialog, DialogLayout layout, string slot, string label,
            float min, float max, float initial, Action<float> changed, Func<float, string> format)
        {
            var slider = dialog.AddComponent<SliderComponent>().WithSliderParams(min, max)
                .WithDisplay(format != null).WithCustomDisplayFormatter(format);
            slider.WithLabel(label);
            slider.Value = initial;
            Place(layout, slider, slot);
            var root = slider.GetComponent<HorizontalLayoutGroup>();
            root.padding = new RectOffset(0, 2, 0, 0);
            root.spacing = 6;
            root.childAlignment = TextAnchor.MiddleLeft;
            root.childForceExpandWidth = false;
            var track = slider.Selectable.transform;
            var controls = track.parent;
            var column = controls.parent == slider.transform ? controls : controls.parent;
            if (column.parent == slider.transform && controls.GetComponent<HorizontalLayoutGroup>() is HorizontalLayoutGroup row)
            {
                foreach (Transform child in slider.transform)
                    Size(child, child == column ? 0 : label == "Zoom" ? 38 : 58, -1);
                row.childForceExpandWidth = false;
                row.childAlignment = TextAnchor.MiddleLeft;
                foreach (Transform child in controls)
                {
                    if (child == track) Size(child, 0, -1);
                    else if (format == null) child.gameObject.SetActive(false);
                    else Size(child, 32, -1);
                }
            }
            slider.OnChanged<SliderComponent, float>(changed);
            return slider;
        }

    }
}
