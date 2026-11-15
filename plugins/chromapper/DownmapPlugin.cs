using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beatmap.Info;
using SimpleJSON;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper;

[Plugin("BS Downmapper")]
public sealed class DownmapPlugin
{
    private sealed class Request
    {
        public string Source;

        public string Target;

        public string[] Charts;

        public JSONNode[] TimingModels;

        public JSONNode[] PatternModels;


        public readonly HashSet<string> Selected = new HashSet<string>();

        public readonly Dictionary<string, TargetSettings> PerTarget = new Dictionary<string, TargetSettings>();

        public TargetSettings Current => PerTarget[Target];

        public string[] SelectedRanks => Targets.Where(Selected.Contains).ToArray();
    }

    private sealed class TargetSettings
    {
        public string Foundation;

        public string Timing;


        public string TimingModel;

        public string Settings;

        public string PatternModel = "built-in";

        private readonly Dictionary<string, JSONObject> timingOptions = new Dictionary<string, JSONObject>();

        private readonly Dictionary<string, JSONObject> patternOptions = new Dictionary<string, JSONObject>();

        public bool Expanded;

        public int? TargetNotePercent;
        public float? HandAlternation;

        public TimingDraft AutomaticDraft = new TimingDraft();

        public readonly Dictionary<string, TimingDraft> AuthoredDrafts = new Dictionary<string, TimingDraft>();

        public TimingDraft Draft
        {
            get
            {
                if (!(Timing == "Auto simplify"))
                {
                    return AuthoredDrafts[Timing];
                }
                return AutomaticDraft;
            }
        }

        public JSONObject TimingOptions(string id)
        {
            return Options(timingOptions, id);
        }

        public JSONObject PatternOptions(string id)
        {
            return Options(patternOptions, id);
        }

        public void RestoreModelOptions() => patternOptions.Remove(PatternModel);

        public void RestoreTimingOptions()
        {
            TargetNotePercent = null;
            HandAlternation = null;
            timingOptions.Remove(TimingModel);
            AutomaticDraft = new TimingDraft();
        }

        private static JSONObject Options(Dictionary<string, JSONObject> models, string id)
        {
            if (!models.TryGetValue(id, out var value))
            {
                models.Add(id, value = new JSONObject());
            }
            return value;
        }

        public void SelectTiming(string timing)
        {
            Timing = timing;
            if (timing != "Auto simplify" && !AuthoredDrafts.ContainsKey(timing))
            {
                AuthoredDrafts.Add(timing, new TimingDraft());
            }
        }
    }

    private sealed class TimingDraft
    {
        public string Rhythm;

        public TimingData Preview;

        public TimingEdits Edits;
    }

    private static readonly string[] Ranked = new string[5] { "ExpertPlus", "Expert", "Hard", "Normal", "Easy" };

    private static readonly string[] Targets = Ranked.Skip(1).ToArray();

    private static readonly Color SaveColor = new Color(0.313025f, 0.3572f, 0.475f);

    private const string Guess = "Auto simplify";

    private const string TimingModelPreference = "bs-downmap.easy-timing-model";


    private const string NestedTimingModel = "nested-priority-v1";

    private const string BuiltInPattern = "built-in";

    private ExtensionButton button;

    private Sprite icon;

    private Texture2D texture;

    private bool running;

    [Init]
    private void Init()
    {
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
        if (!texture.LoadImage(BundledAssets.ReadBytes("downmapper.png"), markNonReadable: true))
        {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidDataException("Could not load the BS Downmapper icon.");
        }
        icon = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        button = ExtensionButtons.AddButton(icon, "BS Downmapper", Open);
        Debug.Log("[BS Downmapper] Editor extension registered.");
    }

    [Exit]
    private void Exit()
    {
        DialogLayout.Unload();
        if (button != null)
        {
            ExtensionButtons.RemoveButton(button);
        }
        if (icon != null)
        {
            UnityEngine.Object.Destroy(icon);
        }
        if (texture != null)
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private void Open()
    {
        if (running)
        {
            Message("A downmap is already running.");
            return;
        }
        try
        {
            BeatSaberSongContainer instance = BeatSaberSongContainer.Instance;
            if (instance?.Info == null || instance.MultiMapperConnection != null)
            {
                throw new InvalidOperationException("Open a local song in the editor before downmapping.");
            }
            AutoSaveController autoSaveController = UnityEngine.Object.FindObjectOfType<AutoSaveController>();
            if ((object)autoSaveController != null && autoSaveController.IsSaving)
            {
                throw new InvalidOperationException("Wait for ChroMapper to finish saving, then reopen BS Downmapper.");
            }
            string source = ExistingDirectory(instance.Info.Directory, "Open song");
            InfoDifficultySet standard = BeatSaberSongUtils.GetInfoFromFolder(source)?.DifficultySets.FirstOrDefault((InfoDifficultySet set) => set.Characteristic == "Standard");
            string[] charts = Ranked.Where((string rank) => standard != null && standard.Difficulties.Any((InfoDifficulty d) => Normalize(d.Difficulty) == rank && HasTimings(Path.Combine(source, d.BeatmapFileName)))).ToArray();
            if (charts.Length == 0)
            {
                throw new InvalidOperationException("The open song needs a saved Standard difficulty with note timings.");
            }
            string text = Normalize(instance.MapDifficultyInfo?.Difficulty);
            string[] array = Targets.Where((string rank) => Array.IndexOf(Ranked, rank) > Array.IndexOf(Ranked, charts[0])).ToArray();
            if (array.Length == 0)
            {
                throw new InvalidOperationException("There is no Standard difficulty below the saved chart.");
            }
            string text2 = (array.Contains(text) ? text : array[0]);
            var nested = new JSONObject();
            nested["id"] = NestedTimingModel;
            nested["label"] = "Nested timing ranker";
            nested["uses_audio"] = true;
            JSONNode[] source2 = ModelSettings.Discover(ModelRoot());
            JSONNode[] array2 = new JSONNode[] { nested }.Concat(source2.Where(model => model["kind"].Value == "timing")).ToArray();
            string preferred = PlayerPrefs.GetString("bs-downmap.easy-timing-model", NestedTimingModel);
            if (!array2.Any((JSONNode model) => model["id"].Value == preferred))
            {
                preferred = NestedTimingModel;
            }
            Request request = new Request
            {
                Source = source,
                Target = text2,
                Charts = charts,
                TimingModels = array2,
                PatternModels = source2.Where((JSONNode model) => model["kind"].Value == "pattern").ToArray(),
                
            };
            string[] array3 = array;
            foreach (string key in array3)
            {
                request.PerTarget.Add(key, new TargetSettings
                {
                    Foundation = charts[0],
                    TimingModel = preferred,
                    Timing = "Auto simplify"
                });
            }
            request.Selected.Add(text2);
            ShowForm(request);
        }
        catch (Exception ex)
        {
            Message(ex.Message);
        }
    }

    private static string Normalize(string value)
    {
        if (!(value == "Expert+"))
        {
            return value;
        }
        return "ExpertPlus";
    }

    private static string Label(string value)
    {
        if (!(value == "ExpertPlus"))
        {
            return value;
        }
        return "Expert+";
    }

    private static string[] HigherCharts(Request request)
    {
        return Ranked.TakeWhile((string rank) => rank != request.Target).Where(request.Charts.Contains).ToArray();
    }

    private static bool HasTimings(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        JSONNode jSONNode = JSON.Parse(File.ReadAllText(path));
        if (jSONNode != null)
        {
            if (jSONNode["colorNotes"].Count <= 0)
            {
                return jSONNode["_notes"].Children.Any((JSONNode note) => note["_type"].AsInt == 0 || note["_type"].AsInt == 1);
            }
            return true;
        }
        return false;
    }

    private void ShowForm(Request request)
    {
        DialogLayout layout = DialogLayout.Load("MainSettings");
        DialogBox dialogBox = PersistentUI.Instance.CreateNewDialogBox().WithTitle("BS Downmapper");
        RenderForm(dialogBox, request, layout);
        dialogBox.Open();
        RefreshNavigation(dialogBox);
    }

    private void RenderForm(DialogBox dialog, Request request, DialogLayout layout)
    {
        var heading = dialog.AddComponent<TextComponent>();
        var body = layout.AttachBody(heading.transform.parent, true);
        layout.Mount(heading, "Heading");
        heading.Value = "Generate downmaps for";
        heading.GetComponentInChildren<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;

        var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
        var outline = sprites.FirstOrDefault(sprite => sprite.name == "inputOutline");
        var checkmark = sprites.FirstOrDefault(sprite => sprite.name == "Checkmark");
        var rows = new Dictionary<string, Action<bool, bool>>();
        Action<string> selectTarget = null;
        Action refresh = null;
        Action saveSliders = null;
        foreach (string rank in Targets)
        {
            var toggle = layout.Mount(dialog.AddComponent<ToggleComponent>(), rank);
            toggle.WithLabel(rank);
            rows.Add(rank, ConfigureDifficultyRow(toggle, request.PerTarget.ContainsKey(rank),
                outline, checkmark, () => selectTarget(rank)));
            toggle.OnChanged<ToggleComponent, bool>(enabled =>
            {
                if (enabled)
                {
                    request.Selected.Add(rank);
                    selectTarget(rank);
                }
                else
                {
                    request.Selected.Remove(rank);
                    selectTarget(request.Target == rank ? request.SelectedRanks.FirstOrDefault() : request.Target);
                }
            });
        }

        AddSettingLabel(dialog, layout, "PatternLabel", "Use patterns from");
        var foundation = AddDropdown(dialog, layout, "Pattern", "", request.Charts);
        foundation.OnChanged<DropdownComponent, int>(index => request.Current.Foundation = request.Charts[index]);

        var modelIds = request.TimingModels.Select(item => item["id"].Value).ToArray();
        AddSettingLabel(dialog, layout, "TimingLabel", "Timing model");
        var timing = AddDropdown(dialog, layout, "Timing", "", new[] { Guess });
        timing.OnChanged<DropdownComponent, int>(index =>
        {
            var settings = request.Current;
            if (index < modelIds.Length)
            {
                string id = modelIds[index];
                if (settings.TimingModel != id)
                {
                    settings.TimingModel = id;
                    PlayerPrefs.SetString(TimingModelPreference, id);
                    settings.AutomaticDraft = new TimingDraft();
                }
                settings.SelectTiming(Guess);
            }
            else
                settings.SelectTiming(request.Charts[index - modelIds.Length]);
            refresh();
        });
        var edit = layout.Mount(dialog.AddComponent<ButtonComponent>().WithLabel("Edit").OnClick(() =>
        {
            saveSliders();
            dialog.Close();
            AfterDialog(() => EditTimings(request));
        }), "Edit");
        DialogLayout.FillControl(edit);

        var content = layout.Slot("Content");
        var foundationRow = layout.Slot("Pattern").parent;
        var timingRow = layout.Slot("Timing").parent;
        var advancedSlot = layout.Slot("AdvancedButton");
        foreach (var row in new[] { foundationRow, timingRow, advancedSlot }) row.SetParent(content, false);
        layout.Slot("Help").SetAsLastSibling();
        var timingDivider = AddDivider(content);
        var patternDivider = AddDivider(content, bottomPadding: 8);

        // Retain legacy bundle slots as hidden anchors for dynamically added rows.
        layout.Slot("Help").gameObject.SetActive(false);
        layout.Slot("StartingChart").gameObject.SetActive(false);
        layout.Slot("RepeatMatching").gameObject.SetActive(false);
        var timingOptions = new ModelSettings(dialog, layout.Slot("RepeatMatching"),
            request.TimingModels, id => request.Current.TimingOptions(id),
            () => request.Current.AutomaticDraft = new TimingDraft());
        var budgetSlot = ModelSettings.AddRow(layout.Slot("RepeatMatching"), "Target note percentage");
        int presetPercent = 90;
        var budget = DialogLayout.Mount(dialog.AddComponent<SliderComponent>(), budgetSlot);
        budget.WithLabel("Target note percentage").WithSliderParams(5f, 100f, 1f)
            .WithCustomDisplayFormatter(value => !budget.Selectable.interactable ? "—" :
                Mathf.RoundToInt(value) == presetPercent ? $"Auto ({presetPercent}%)" : $"{Mathf.RoundToInt(value)}%");
        GeneratorSettings.AlignSliderColumns(budget, 86);
        var budgetHint = SettingHint.ForSlider(budget);
        budget.OnChanged<SliderComponent, float>(value =>
        {
            var settings = request.Current;
            if (settings.Timing != Guess) return;
            int percent = Mathf.RoundToInt(value);
            int? next = percent == presetPercent ? null : percent;
            if (settings.TargetNotePercent == next) return;
            settings.TargetNotePercent = next;
            settings.AutomaticDraft = new TimingDraft();
        });

        var alternationSlot = ModelSettings.AddRow(layout.Slot("RepeatMatching"), "Hand alternation");
        var alternation = DialogLayout.Mount(dialog.AddComponent<SliderComponent>(), alternationSlot);
        alternation.WithLabel("Hand alternation").WithSliderParams(0f, 1.5f, .05f)
            .WithCustomDisplayFormatter(value => value.ToString("0.0#") + " s");
        GeneratorSettings.AlignSliderColumns(alternation);
        SettingHint.ForSlider(alternation).Hint =
            "Maximum gap between same-hand singles eligible for alternation. Zero turns it off.";
        alternation.OnChanged<SliderComponent, float>(value =>
        {
            if (request.Current.AutomaticDraft.Edits != null)
                return;
            request.Current.HandAlternation = value;
            request.Current.AutomaticDraft = new TimingDraft();
        });
        var timingDefaultsSlot = ModelSettings.AddRow(layout.Slot("RepeatMatching"), "Restore timing defaults");
        DialogLayout.Mount(dialog.AddComponent<ButtonComponent>().WithLabel("Restore timing defaults").OnClick(() =>
        {
            request.Current.RestoreTimingOptions();
            refresh();
        }), timingDefaultsSlot);

        var patternIds = new[] { BuiltInPattern }.Concat(request.PatternModels.Select(item => item["id"].Value)).ToArray();
        var pattern = AddDropdown(dialog, layout, "Model", "Pattern model",
            new[] { "Balanced" }.Concat(request.PatternModels.Select(item => item["label"].Value)));
        pattern.OnChanged<DropdownComponent, int>(index =>
        {
            request.Current.PatternModel = patternIds[index];
            refresh();
        });
        var advancedButton = layout.Mount(dialog.AddComponent<ButtonComponent>().OnClick(() =>
        {
            request.Current.Expanded = !request.Current.Expanded;
            refresh();
        }), "AdvancedButton", compact: false).WithLabel("Advanced pattern settings");
        var advancedLabel = advancedButton.GetComponentInChildren<TMP_Text>();
        advancedLabel.alignment = TextAlignmentOptions.MidlineLeft;
        advancedLabel.margin = new Vector4(8, 0, 24, 0);
        var arrowObject = new GameObject("Disclosure arrow", typeof(RectTransform), typeof(DisclosureArrow));
        var arrowRect = (RectTransform)arrowObject.transform;
        arrowRect.SetParent(advancedButton.Selectable.transform, false);
        arrowRect.anchorMin = arrowRect.anchorMax = new Vector2(1, .5f);
        arrowRect.anchoredPosition = new Vector2(-15, 0);
        arrowRect.sizeDelta = new Vector2(10, 8);
        var advancedArrow = arrowObject.GetComponent<DisclosureArrow>();
        advancedArrow.color = advancedLabel.color;
        advancedArrow.raycastTarget = false;
        var patternOptions = new ModelSettings(dialog, layout.Slot("height"),
            request.PatternModels, id => request.Current.PatternOptions(id), null);
        var generator = new GeneratorSettings(dialog, layout, () =>
        {
            request.Current.RestoreModelOptions();
            refresh();
        });
        var profileError = layout.Mount(dialog.AddComponent<TextComponent>(), "ProfileError");
        TargetSettings loadedProfile = null;
        bool profileFailed = false;
        JSONNode profile = null;

        var patternRows = new[] { "height", "width", "crossover", "diagonalPreference", "parityAdherence",
            "rampAmount", "RestoreDefaults", "ProfileError" };
        var ordered = new Transform[] { foundationRow, timingDivider, timingRow, budgetSlot }
            .Concat(timingOptions.Slots)
            .Concat(new Transform[] { layout.Slot("RepeatMatching"), timingDefaultsSlot, patternDivider, layout.Slot("Model"), advancedSlot })
            .Concat(patternOptions.Slots)
            .Concat(patternRows.Select(name => (Transform)layout.Slot(name)));
        foreach (var row in ordered) row.SetAsLastSibling();
        alternationSlot.SetParent(layout.Slot("parityAdherence").parent, false);
        alternationSlot.SetSiblingIndex(layout.Slot("parityAdherence").GetSiblingIndex() + 1);

        SettingsScroll.AddTo(layout, foundation.Selectable.GetComponentInChildren<Scrollbar>(true));
        layout.Mount(dialog.AddComponent<TextComponent>(), "Empty").Value = "Check a difficulty to configure its downmap.";
        var generate = dialog.AddFooterButton(() =>
        {
            SaveSliders();
            AfterDialog(() => ConfirmGeneration(request));
        }, "Generate").WithBackgroundColor(SaveColor);
        layout.AttachFooter(generate.transform.parent);
        layout.Mount(generate, "Generate");
        layout.Mount(dialog.AddFooterButton(null, "Cancel"), "Cancel");
        selectTarget = SelectTarget;
        refresh = Refresh;
        saveSliders = SaveSliders;
        Refresh();

        void SelectTarget(string rank)
        {
            if (request.Target != rank)
            {
                SaveSliders();
                request.Target = rank;
            }
            Refresh();
        }

        void SaveSliders()
        {
            if (request.Target != null && request.Current == loadedProfile)
                loadedProfile.Settings = generator.Capture();
        }

        void Refresh()
        {
            var settings = request.Target == null ? null : request.Current;
            foreach (var row in rows)
                row.Value(request.Selected.Contains(row.Key), request.Target == row.Key);
            layout.SetBodyHeight(body, settings != null);
            layout.Slot("Options").gameObject.SetActive(settings != null);
            layout.Slot("Empty").gameObject.SetActive(settings == null);
            generate.Selectable.interactable = settings != null;
            if (settings != null)
            {
                foundation.SetValueWithoutNotify(Array.IndexOf(request.Charts, settings.Foundation));
                timing.WithOptions(request.TimingModels.Select(item =>
                {
                    string label = item["id"].Value == NestedTimingModel ? Guess : $"Auto: {item["label"].Value}";
                    return settings.AutomaticDraft.Edits != null && item["id"].Value == settings.TimingModel
                        ? label + " (edited)" : label;
                }).Concat(request.Charts.Select(chart =>
                    settings.AuthoredDrafts.TryGetValue(chart, out var draft) && draft.Edits != null
                        ? Label(chart) + " (edited)" : Label(chart))));
                int selection = settings.Timing == Guess ? Array.IndexOf(modelIds, settings.TimingModel)
                    : modelIds.Length + Array.IndexOf(request.Charts, settings.Timing);
                timing.SetValueWithoutNotify(selection);
                RefreshSettings(settings);
            }
            if (dialog.gameObject.activeInHierarchy) RefreshNavigation(dialog);
        }

        void RefreshSettings(TargetSettings settings)
        {
            if (loadedProfile != settings)
            {
                profileFailed = false;
                profile = null;
                try
                {
                    string text = BundledAssets.ReadText(request.Target.ToLowerInvariant() + ".json");
                    generator.Load(text, settings.Settings);
                    profile = JSON.Parse(text);
                }
                catch (Exception error)
                {
                    generator.Load(null, null);
                    profileFailed = true;
                    profileError.Value = error.Message;
                }
                loadedProfile = settings;
            }

            bool automatic = settings.Timing == Guess;
            bool nested = settings.TimingModel == NestedTimingModel;
            alternationSlot.gameObject.SetActive(settings.Expanded);
            alternation.Selectable.interactable = automatic && nested && settings.AutomaticDraft.Edits == null;
            alternation.SetValueWithoutNotify(settings.HandAlternation ?? (profile == null ? 0 : profile["handAlternation"].AsFloat));
            timingDefaultsSlot.gameObject.SetActive(automatic);
            timingOptions.Show(automatic ? settings.TimingModel : null, settings.TimingOptions(settings.TimingModel));

            budget.Selectable.interactable = automatic && profile != null;
            budgetHint.Hint = nested ? "Share of the highest saved chart. Auto uses the adaptive preset."
                : "Share of the highest saved chart, passed to the timing model as a suggested count.";
            if (profile != null)
                presetPercent = Mathf.RoundToInt(50 * (profile["minimumSourceNoteRatio"].AsFloat +
                    profile["maximumSourceNoteRatio"].AsFloat));
            budget.SetValueWithoutNotify(settings.TargetNotePercent ?? presetPercent);

            pattern.SetValueWithoutNotify(Array.IndexOf(patternIds, settings.PatternModel));
            patternOptions.Show(settings.Expanded ? settings.PatternModel : null, settings.PatternOptions(settings.PatternModel));
            arrowRect.localRotation = Quaternion.Euler(0, 0, settings.Expanded ? 0 : 90);
            generator.SetVisible(settings.Expanded);
            layout.Slot("ProfileError").gameObject.SetActive(settings.Expanded && profileFailed);
        }
    }

    private static RectTransform AddDivider(RectTransform parent, float bottomPadding = 0)
    {
        var row = new GameObject("Section divider", typeof(RectTransform), typeof(LayoutElement));
        row.transform.SetParent(parent, false);
        var size = row.GetComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = 1 + bottomPadding;
        var line = new GameObject("Line", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)line.transform;
        DialogLayout.Fill(rect, (RectTransform)row.transform);
        rect.offsetMin = new Vector2(0, bottomPadding);
        var image = line.GetComponent<Image>();
        image.color = new Color(.38f, .38f, .38f);
        image.raycastTarget = false;
        return (RectTransform)row.transform;
    }

    private static void RefreshNavigation(DialogBox dialog)
    {
        Selectable[] array = (from field in dialog.GetComponentsInChildren<CMUIComponentBase>(includeInactive: true).OfType<INavigable>()
            select field.Selectable).ToArray();
        Selectable selectOnUp = null;
        Selectable[] array2 = array;
        foreach (Selectable selectable in array2)
        {
            Navigation navigation = selectable.navigation;
            navigation.selectOnUp = selectOnUp;
            selectable.navigation = navigation;
            if (selectable.isActiveAndEnabled && selectable.IsInteractable())
            {
                selectOnUp = selectable;
            }
        }
        Selectable selectOnDown = null;
        for (int num2 = array.Length - 1; num2 >= 0; num2--)
        {
            Selectable selectable2 = array[num2];
            Navigation navigation2 = selectable2.navigation;
            navigation2.selectOnDown = selectOnDown;
            selectable2.navigation = navigation2;
            if (selectable2.isActiveAndEnabled && selectable2.IsInteractable())
            {
                selectOnDown = selectable2;
            }
        }
    }

    private static Action<bool, bool> ConfigureDifficultyRow(ToggleComponent field, bool available, Sprite outline, Sprite checkmark, Action select)
    {
        HorizontalLayoutGroup component = field.GetComponent<HorizontalLayoutGroup>();
        component.padding = new RectOffset(8, 8, 0, 0);
        component.spacing = 6f;
        component.childForceExpandWidth = false;
        Toggle toggle = (Toggle)field.Selectable;
        toggle.interactable = available;
        RectTransform rectTransform = (RectTransform)toggle.transform;
        rectTransform.sizeDelta = new Vector2(16f, 16f);
        Image image = (Image)toggle.targetGraphic;
        image.rectTransform.sizeDelta = rectTransform.sizeDelta;
        if (outline != null)
        {
            image.sprite = outline;
            image.pixelsPerUnitMultiplier = 3f;
            ColorBlock colors = toggle.colors;
            colors.normalColor = new Color(0.85f, 0.85f, 0.85f);
            toggle.colors = colors;
        }
        Image image2 = (Image)toggle.graphic;
        image2.rectTransform.sizeDelta = new Vector2(12f, 12f);
        if (checkmark != null)
        {
            image2.sprite = checkmark;
        }
        Transform parent = rectTransform.parent;
        parent.SetAsFirstSibling();
        LayoutElement component2 = parent.GetComponent<LayoutElement>();
        float minWidth = (component2.preferredWidth = 18f);
        component2.minWidth = minWidth;
        minWidth = (component2.preferredHeight = 18f);
        component2.minHeight = minWidth;
        component2.flexibleWidth = 0f;
        TMP_Text componentInChildren = field.GetComponentInChildren<TMP_Text>();
        componentInChildren.alignment = TextAlignmentOptions.Left;
        componentInChildren.color = (available ? new Color(0.93f, 0.93f, 0.93f) : new Color(0.53f, 0.53f, 0.53f));
        componentInChildren.transform.parent.GetComponent<LayoutElement>().flexibleWidth = 1f;
        Image background = field.gameObject.AddComponent<Image>();
        Button button = field.gameObject.AddComponent<Button>();
        button.targetGraphic = background;
        button.navigation = new Navigation
        {
            mode = Navigation.Mode.None
        };
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() =>
        {
            select();
        });
        return (bool enabled, bool active) =>
        {
            if (field.Value != enabled)
            {
                field.SetValueWithoutNotify(enabled);
            }
            button.interactable = enabled;
            float num3 = (active ? 0.27f : 0.16f);
            background.color = new Color(num3, num3, num3);
        };
    }

    private static void AddSettingLabel(DialogBox dialog, DialogLayout layout, string slot, string label)
    {
        TextComponent textComponent = layout.Mount(dialog.AddComponent<TextComponent>(), slot);
        textComponent.Value = label;
        textComponent.GetComponentInChildren<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;
    }

    private static DropdownComponent AddDropdown(DialogBox dialog, DialogLayout layout, string slot, string label, IEnumerable<string> options)
    {
        DropdownComponent dropdownComponent = layout.Mount(dialog.AddComponent<DropdownComponent>().WithOptions(options.Select(Label)), slot);
        dropdownComponent.WithLabel(label);
        HorizontalLayoutGroup component = dropdownComponent.GetComponent<HorizontalLayoutGroup>();
        component.padding = new RectOffset();
        component.childAlignment = TextAnchor.MiddleLeft;
        if (string.IsNullOrEmpty(label))
        {
            Transform parent = dropdownComponent.Selectable.transform.parent;
            foreach (Transform item in dropdownComponent.transform)
            {
                item.gameObject.SetActive(item == parent);
            }
            DialogLayout.FillControl(dropdownComponent);
        }
        else
        {
            var root = (RectTransform)dropdownComponent.transform;
            var field = (RectTransform)dropdownComponent.Selectable.transform;
            var container = field;
            while (container.parent != root)
                container = (RectTransform)container.parent;

            // Keep the native label in the row, but size the selectable from
            // the authored slot rather than Stable's larger native prefab.
            float height = layout.Slot(slot).GetComponent<LayoutElement>().preferredHeight;
            var size = container.GetComponent<LayoutElement>() ?? container.gameObject.AddComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = height;
            size.flexibleHeight = 0;
            component.childControlHeight = true;
            component.childForceExpandHeight = false;
            for (var rect = field; rect != container; rect = (RectTransform)rect.parent)
            {
                var parent = (RectTransform)rect.parent;
                if (parent.TryGetComponent<LayoutGroup>(out var group)) group.enabled = false;
                DialogLayout.Fill(rect, parent);
            }
        }
        return dropdownComponent;
    }

    private static string ModelRoot()
    {
        string text = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.Equals(Path.GetFileName(text), "Plugins", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.Combine(text, "bs-downmap");
        }
        return Path.Combine(text, "models");
    }

    private static void AfterDialog(Action action)
    {
        PersistentUI.Instance.StartCoroutine(NextFrame(action));
    }

    private static IEnumerator NextFrame(Action action)
    {
        yield return null;
        action();
    }

    private void EditTimings(Request request)
    {
        TargetSettings current = request.Current;
        TimingDraft draft = current.Draft;
        Action show = () =>
        {
            TimingWindow.Show(request.Source, request.Target, draft.Preview, draft.Edits, RemovedAbove(request), (TimingEdits edits) =>
            {
                if (edits != null)
                {
                    bool flag2 = !new HashSet<int>(draft.Preview.initiallyRemoved).SetEquals(edits.removedNoteIds) || !new HashSet<TimingMerge>(draft.Preview.merges).SetEquals(edits.merges);
                    draft.Edits = (flag2 ? edits : null);
                }
                AfterDialog(() =>
                {
                    ShowForm(request);
                });
            });
        };
        if (draft.Preview != null)
        {
            show();
            return;
        }
        string[] source = HigherCharts(request);
        bool flag = current.Timing == "Auto simplify" && current.TimingModel == "nested-priority-v1";
        draft.Rhythm = ((current.Timing == "Auto simplify") ? (flag ? source.First() : source.Last()) : current.Timing);
        List<string> list = new List<string>
        {
            "--source",
            request.Source,
            "--source-difficulty",
            current.Foundation,
            "--preview",
            "--difficulty",
            request.Target,
            "--rhythm-difficulty",
            draft.Rhythm,
            "--rhythm-mode",
            (current.Timing == "Auto simplify") ? "best-guess" : "authored"
        };
        List<string> temporary = new List<string>();
        if (current.Timing == "Auto simplify")
        {
            list.AddRange(new string[2] { "--timing-model", current.TimingModel });
            JSONObject jSONObject = current.TimingOptions(current.TimingModel);
            if (jSONObject.Count > 0)
            {
                list.AddRange(new string[2]
                {
                    "--timing-model-settings",
                    WriteInput(jSONObject.ToString(), temporary)
                });
            }
        }
        if (current.Timing == Guess && !string.IsNullOrEmpty(current.Settings) &&
            JSON.Parse(current.Settings)["parityAdherence"] is JSONNumber parity)
        {
            list.Add("--parity-adherence");
            list.Add(parity.AsDouble.ToString(CultureInfo.InvariantCulture));
        }
        if (current.Timing == Guess && current.HandAlternation.HasValue)
        {
            list.Add("--hand-alternation");
            list.Add(current.HandAlternation.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (current.Timing == Guess && current.TargetNotePercent.HasValue)
        {
            list.AddRange(new string[2]
            {
                "--target-note-fraction",
                ((double)current.TargetNotePercent.Value / 100.0).ToString(CultureInfo.InvariantCulture)
            });
        }
        GenerationWindow progress = GenerationWindow.Show("Preparing Standard " + request.Target + " timings…");
        StartRun(list, (string output) =>
        {
            string text = output.Split('\n').Single((string s) => s.StartsWith("TIMELINE_JSON: ", StringComparison.Ordinal));
            TimingData preview = TimingData.Parse(text.Substring("TIMELINE_JSON: ".Length));
            TempoRegion[] array = BeatClock.ReadRegions(request.Source, draft.Rhythm);
            if (array.Length != preview.regions.Length || array.Where((TempoRegion region, int index) => Math.Abs(region.beat - preview.regions[index].beat) > 1E-09 || Math.Abs(region.seconds - preview.regions[index].seconds) > 1E-09 || Math.Abs(region.secondsPerBeat - preview.regions[index].secondsPerBeat) > 1E-09).Any())
            {
                throw new InvalidOperationException("The chart's tempo differs from the timing preview. Reopen it and try again.");
            }
            preview.regions = array;
            draft.Preview = preview;
            AfterDialog(show);
        }, temporary, (bool _) =>
        {
            progress.Finish();
        }, (string reason) =>
        {
            Message(reason, () =>
            {
                AfterDialog(() =>
                {
                    ShowForm(request);
                });
            });
        }, progress);
    }

    private static Func<double, bool> RemovedAbove(Request request)
    {
        List<double[]> parents = new List<double[]>();
        foreach (string item in request.SelectedRanks.TakeWhile((string rank) => rank != request.Target))
        {
            TimingDraft draft = request.PerTarget[item].Draft;
            if (draft?.Edits == null)
            {
                continue;
            }
            HashSet<int> removed = new HashSet<int>(draft.Edits.removedNoteIds);
            removed.UnionWith(from merge in draft.Edits.merges
                where !Missing(merge.@from) && !Missing(merge.to)
                select merge.noteId);
            parents.Add((from marker in draft.Preview.timings
                where !Missing(marker.beat) && marker.noteIds.Any((int id) => !removed.Contains(id))
                select marker.beat).ToArray());
        }
        return Missing;
        bool Missing(double beat)
        {
            return parents.Any((double[] beats) =>
            {
                int num = Array.BinarySearch(beats, beat - 0.0001);
                if (num < 0)
                {
                    num = ~num;
                }
                return num == beats.Length || beats[num] > beat + 0.0001;
            });
        }
    }

    private static string Fingerprint(string path)
    {
        using FileStream inputStream = File.OpenRead(path);
        using SHA256 sHA = SHA256.Create();
        return BitConverter.ToString(sHA.ComputeHash(inputStream)).Replace("-", "").ToLowerInvariant();
    }

    private static AutoSaveController CheckEditorReady()
    {
        AutoSaveController autoSaveController = UnityEngine.Object.FindObjectOfType<AutoSaveController>();
        BeatmapActionContainer beatmapActionContainer = UnityEngine.Object.FindObjectOfType<BeatmapActionContainer>();
        if (autoSaveController == null || autoSaveController.IsSaving || beatmapActionContainer == null || beatmapActionContainer.ContainsUnsavedActions)
        {
            throw new InvalidOperationException("Save your current chart and let ChroMapper finish saving before generating.");
        }
        return autoSaveController;
    }

    private void ConfirmGeneration(Request request)
    {
        try
        {
            CheckEditorReady();
            string[] selectedRanks = request.SelectedRanks;
            byte[] array = File.ReadAllBytes(Path.Combine(request.Source, "Info.dat"));
            string text;
            using (SHA256 sHA = SHA256.Create())
            {
                text = BitConverter.ToString(sHA.ComputeHash(array)).Replace("-", "").ToLowerInvariant();
            }
            JSONNode info = JSON.Parse(Encoding.UTF8.GetString(array).TrimStart('\ufeff'));
            InfoDifficultySet infoDifficultySet = BeatSaberSongUtils.GetInfoFromFolder(request.Source)?.DifficultySets.FirstOrDefault((InfoDifficultySet set) => set.Characteristic == "Standard");
            JSONArray jSONArray = new JSONArray();
            List<string> list = new List<string>();
            string[] array2 = selectedRanks;
            foreach (string rank in array2)
            {
                InfoDifficulty infoDifficulty = infoDifficultySet?.Difficulties.FirstOrDefault((InfoDifficulty d) => Normalize(d.Difficulty) == rank);
                string path = ((infoDifficulty == null) ? null : Path.Combine(request.Source, infoDifficulty.BeatmapFileName));
                TargetSettings targetSettings = request.PerTarget[rank];
                TimingDraft draft = targetSettings.Draft;
                JSONObject jSONObject = new JSONObject
                {
                    ["difficulty"] = rank,
                    ["foundation"] = targetSettings.Foundation
                };
                jSONObject["jumpSettings"] = JumpSettings.FromSongInfo(rank, info);
                jSONObject["targetSha256"] = (File.Exists(path) ? Fingerprint(path) : "missing");
                if (jSONObject["targetSha256"].Value != "missing")
                {
                    list.Add(rank);
                }
                jSONObject["mode"] = ((targetSettings.Timing == "Auto simplify") ? "best-guess" : "authored");
                string text2;
                if (targetSettings.Timing == "Auto simplify")
                {
                    text2 = ((draft.Edits == null) ? null : draft.Rhythm);
                }
                else
                {
                    text2 = targetSettings.Timing;
                }
                if (text2 != null)
                {
                    jSONObject["rhythm"] = text2;
                }
                if (draft?.Edits != null)
                {
                    jSONObject["edits"] = draft.Edits.ToJson();
                    if (targetSettings.Timing == "Auto simplify" && targetSettings.TimingModel == "nested-priority-v1")
                    {
                        jSONObject["model"] = targetSettings.TimingModel;
                    }
                }
                else if (targetSettings.Timing == "Auto simplify")
                {
                    jSONObject["model"] = targetSettings.TimingModel;
                    JSONObject jSONObject2 = targetSettings.TimingOptions(targetSettings.TimingModel);
                    if (jSONObject2.Count > 0)
                    {
                        jSONObject["timingModelSettings"] = jSONObject2;
                    }
                    if (targetSettings.TimingModel == NestedTimingModel)
                        jSONObject["rhythmLinks"] = true;
                    if (targetSettings.TargetNotePercent.HasValue)
                        jSONObject["targetNoteFraction"] = targetSettings.TargetNotePercent.Value / 100d;
                    if (targetSettings.HandAlternation.HasValue)
                        jSONObject["handAlternation"] = (double)targetSettings.HandAlternation.Value;
                }
                if (targetSettings.PatternModel != "built-in")
                {
                    jSONObject["patternModel"] = targetSettings.PatternModel;
                    jSONObject["patternModelSettings"] = targetSettings.PatternOptions(targetSettings.PatternModel);
                }
                if (!string.IsNullOrEmpty(targetSettings.Settings) && targetSettings.Settings != "{}")
                {
                    jSONObject["settings"] = JSON.Parse(targetSettings.Settings);
                }
                jSONArray.Add(jSONObject);
            }
            JSONObject plan = new JSONObject
            {
                ["infoSha256"] = text,
                ["targets"] = jSONArray
            };
            if (list.Count == 0)
            {
                Generate(request, plan);
                return;
            }
            DialogBox dialogBox = PersistentUI.Instance.CreateNewDialogBox().WithTitle("Overwrite existing difficulties?");
            dialogBox.AddComponent<TextComponent>().Value = "Generating will replace Standard " + string.Join(", ", list) + "." +
                "\nFailed placement searches may save drafts that need manual review.";
            dialogBox.AddFooterButton(() =>
            {
                AfterDialog(() =>
                {
                    ShowForm(request);
                });
            }, "Cancel");
            dialogBox.AddFooterButton(() =>
            {
                AfterDialog(() =>
                {
                    Generate(request, plan);
                });
            }, "Overwrite").WithBackgroundColor(SaveColor);
            dialogBox.Open();
        }
        catch (Exception ex)
        {
            Message(ex.Message, () =>
            {
                AfterDialog(() =>
                {
                    ShowForm(request);
                });
            });
        }
    }

    private void Generate(Request request, JSONObject plan)
    {
        List<string> temporary = new List<string>();
        try
        {
            AutoSaveController save = CheckEditorReady();
            string[] arguments = new string[4]
            {
                "--source",
                request.Source,
                "--batch-plan",
                WriteInput(plan.ToString(), temporary)
            };
            string targets = string.Join(", ", request.SelectedRanks);
            GenerationWindow progress = GenerationWindow.Show("Generating Standard " + targets + "…");
            bool saveWasEnabled = save.enabled;
            save.enabled = false;
            StartRun(arguments, output =>
            {
                var result = JSON.Parse(output);
                AfterDialog(() => ShowGenerationResult(request.Source, targets, result));
            }, temporary, (bool _) =>
            {
                if (save != null)
                {
                    save.enabled = saveWasEnabled;
                }
                progress.Finish();
            }, (string reason) =>
            {
                AfterDialog(() =>
                {
                    Message(reason, () =>
                    {
                        AfterDialog(() =>
                        {
                            ShowForm(request);
                        });
                    });
                });
            }, progress);
        }
        catch (Exception ex)
        {
            CleanUp(temporary);
            Message(ex.Message, () =>
            {
                AfterDialog(() =>
                {
                    ShowForm(request);
                });
            });
        }
    }

    private static void ShowGenerationResult(string source, string targets, JSONNode result)
    {
        Action dismiss = () => AfterDialog(() => RefreshSong(source));
        var warnings = result["warnings"].Children.ToArray();
        if (warnings.Length == 0)
        {
            Message("Generated Standard " + targets + ".", dismiss);
            return;
        }

        var dialog = PersistentUI.Instance.CreateNewDialogBox().WithTitle("Downmaps saved with review flags");
        foreach (var warning in warnings)
        {
            string review = warning["ReviewEntireChart"].AsBool ? " Review the whole difficulty." : "";
            var beats = warning["ParityFindings"].Children.Select(finding =>
                $"{finding["Beat"].AsDouble:0.####} ({finding["Hand"].Value.ToLowerInvariant()})")
                .Distinct().Take(6).ToArray();
            string parity = beats.Length == 0 ? "" : "\nParity findings: " + string.Join(", ", beats) + ".";
            dialog.AddComponent<TextComponent>().Value =
                $"Standard {warning["Difficulty"].Value}: {warning["Failure"].Value}{review}{parity}";
        }
        dialog.AddFooterButton(() =>
        {
            GUIUtility.systemCopyBuffer = result["reviewPath"].Value;
            dismiss();
        }, "Copy report path");
        dialog.AddFooterButton(dismiss, "OK");
        dialog.Open();
    }

    private static void RefreshSong(string source)
    {
        BaseInfo infoFromFolder = BeatSaberSongUtils.GetInfoFromFolder(source);
        if (infoFromFolder == null)
        {
            Message("Difficulties saved. Reopen the song to refresh its difficulty list.");
        }
        else
        {
            BeatSaberSongContainer.Instance.SelectSongForEditing(infoFromFolder);
        }
    }

    private static string WriteInput(string value, List<string> temporary)
    {
        string tempFileName = Path.GetTempFileName();
        temporary.Add(tempFileName);
        File.WriteAllText(tempFileName, value);
        return tempFileName;
    }

    private void StartRun(IEnumerable<string> arguments, Action<string> onSuccess, IEnumerable<string> temporary = null, Action<bool> completed = null, Action<string> onFailure = null, GenerationWindow progressWindow = null)
    {
        try
        {
            string[] array = arguments.ToArray();
            JSONObject jSONObject = new JSONObject();
            jSONObject["models"] = ModelRoot();
            bool preview = array.Contains("--preview");
            for (int i = 0; i < array.Length; i++)
            {
                string text = array[i];
                switch (text)
                {
                case "--no-rhythm-links":
                    jSONObject[text.Substring(2)] = true;
                    break;
                case "--target-note-fraction":
                    jSONObject["target-note-fraction"] = double.Parse(array[++i], CultureInfo.InvariantCulture);
                    break;
                case "--hand-alternation":
                    jSONObject["hand-alternation"] = double.Parse(array[++i], CultureInfo.InvariantCulture);
                    break;
                case "--parity-adherence":
                    jSONObject["parity-adherence"] = double.Parse(array[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    jSONObject[text.Substring(2)] = array[++i];
                    break;
                case "--preview":
                    break;
                }
            }
            string payload = jSONObject.ToString();
            var updates = new ProgressUpdates();
            Action<float, string> report = progressWindow == null ? null : updates.Report;
            running = true;
            var work = Task.Run(() => RunInProcess(payload, preview, report));
            PersistentUI.Instance.StartCoroutine(WaitForRun(work, onSuccess, temporary, completed, onFailure,
                progressWindow, updates, preview ? "Timing preview ready" : "Difficulties saved"));
        }
        catch (Exception error)
        {
            FailRun(error, temporary, completed, onFailure);
        }
    }

    private static async Task<string> RunInProcess(string payload, bool preview, Action<float, string> progress)
    {
        using BundledAssets.RuntimeFilesLease runtime = BundledAssets.ExtractRuntime();
        JsonObject jsonObject = JsonNode.Parse(payload).AsObject();
        jsonObject["engine"] = runtime.DirectoryPath;
        jsonObject["ranker"] = runtime.PathFor(".tools/native-engine/shared-percentage-v1-ranker.bdt");
        string environmentVariable = Environment.GetEnvironmentVariable("BS_DOWNMAP_AUDIO_DECODER");
        jsonObject["decoder"] = (string.IsNullOrWhiteSpace(environmentVariable) ? runtime.PathFor(BundledAssets.Decoder) : Path.GetFullPath(environmentVariable));
        if (!preview)
            return JsonSerializer.Serialize(await EditorGeneration.Run(jsonObject, progress).ConfigureAwait(false));

        var result = await EditorPreview.Run(jsonObject, progress).ConfigureAwait(false);
        string output = "TIMELINE_JSON: " + JsonSerializer.Serialize(result);
        progress?.Invoke(1f, "Timing preview ready");
        return output;
    }

    private sealed class ProgressUpdates
    {
        private sealed class Update
        {
            internal readonly float Fraction;
            internal readonly string Message;

            internal Update(float fraction, string message) { Fraction = fraction; Message = message; }
        }

        private Update latest;

        internal void Report(float fraction, string message) =>
            System.Threading.Interlocked.Exchange(ref latest, new Update(fraction, message));

        internal void Apply(GenerationWindow window)
        {
            var update = System.Threading.Interlocked.Exchange(ref latest, null);
            if (update != null && window != null) window.SetProgress(update.Fraction, update.Message);
        }
    }

    private IEnumerator WaitForRun(Task<string> work, Action<string> onSuccess, IEnumerable<string> temporary,
        Action<bool> completed, Action<string> onFailure, GenerationWindow progressWindow, ProgressUpdates updates,
        string completionMessage)
    {
        while (!work.IsCompleted)
        {
            updates.Apply(progressWindow);
            yield return null;
        }
        updates.Apply(progressWindow);
        if (work.IsFaulted || work.IsCanceled)
        {
            FailRun(work.Exception?.GetBaseException() ?? new OperationCanceledException(), temporary, completed, onFailure);
            yield break;
        }
        if (progressWindow != null)
        {
            progressWindow.SetProgress(1f, completionMessage);
            yield return null;
        }
        running = false;
        CleanUp(temporary);
        try
        {
            onSuccess(work.Result);
        }
        catch (Exception error)
        {
            FailRun(error, null, completed, onFailure);
            yield break;
        }
        completed?.Invoke(obj: true);
    }

    private void FailRun(Exception error, IEnumerable<string> temporary, Action<bool> completed, Action<string> onFailure)
    {
        running = false;
        Debug.LogError("[BS Downmapper] " + error);
        CleanUp(temporary);
        completed?.Invoke(obj: false);
        (onFailure ?? ((Action<string>)((string reason) =>
        {
            Message(reason);
        })))(error.Message);
    }

    private static void CleanUp(IEnumerable<string> temporary)
    {
        if (temporary == null)
        {
            return;
        }
        foreach (string item in temporary)
        {
            try
            {
                File.Delete(item);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string ExistingDirectory(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value.Trim()))
        {
            throw new IOException(label + " must be an existing folder.");
        }
        return Path.GetFullPath(value.Trim());
    }

    private static void Message(string message, Action dismissed = null)
    {
        DialogBox dialogBox = PersistentUI.Instance.CreateNewDialogBox().WithTitle("BS Downmapper");
        dialogBox.AddComponent<TextComponent>().Value = message;
        dialogBox.AddFooterButton(dismissed, "OK");
        dialogBox.Open();
    }
}
