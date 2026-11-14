using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    internal sealed class ModelSettings
    {
        private readonly Dictionary<string, List<Action<bool, JSONObject>>> rows =
            new Dictionary<string, List<Action<bool, JSONObject>>>();

        internal readonly List<Transform> Slots = new List<Transform>();

        internal static JSONNode[] Discover(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<JSONNode>();
            var models = new List<JSONNode>();
            foreach (var folder in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
            {
                var manifest = Path.Combine(folder, "model.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    var model = JSON.Parse(File.ReadAllText(manifest));
                    var id = model["id"].Value;
                    var kind = model["kind"].Value;
                    var entry = Path.GetFullPath(Path.Combine(folder, model["entry"].Value));
                    var apiVersion = model["apiVersion"].AsDouble;
                    if (apiVersion != 2) continue;
                    if (!Regex.IsMatch(id, "^custom-[a-z0-9][a-z0-9-]*$") ||
                        string.IsNullOrWhiteSpace(model["label"].Value) || (kind != "timing" && kind != "pattern") ||
                        models.Any(other => other["id"].Value == id && other["kind"].Value == kind) ||
                        !entry.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetExtension(entry), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
                        throw new FormatException("Expected a unique supported timing or pattern manifest with a local model entry.");
                    if (model.HasKey("settings") && !model["settings"].IsObject)
                        throw new FormatException("Model settings must be a JSON object.");
                    foreach (var pair in model["settings"].AsObject ?? new JSONObject())
                    {
                        var spec = pair.Value;
                        var value = spec["default"];
                        bool valid;
                        switch (spec["type"].Value)
                        {
                            case "boolean": valid = value.IsBoolean; break;
                            case "choice":
                                valid = value.IsString && spec["options"].IsArray &&
                                    spec["options"].Children.All(option => option.IsString) &&
                                    spec["options"].Children.Any(option => option.Value == value.Value);
                                break;
                            case "number":
                                valid = value.IsNumber && spec["min"].IsNumber && spec["max"].IsNumber &&
                                    spec["min"].AsDouble < spec["max"].AsDouble && value.AsDouble >= spec["min"].AsDouble &&
                                    value.AsDouble <= spec["max"].AsDouble &&
                                    (!spec.HasKey("step") || (spec["step"].IsNumber && spec["step"].AsDouble > 0));
                                break;
                            default: valid = false; break;
                        }
                        if (!valid) throw new FormatException($"Invalid settings definition for {pair.Key}.");
                    }
                    models.Add(model);
                }
                catch (Exception error)
                {
                    Debug.LogWarning($"[BS Downmapper] Skipping {manifest}: {error.Message}");
                }
            }
            return models.ToArray();
        }

        internal ModelSettings(DialogBox dialog, RectTransform before,
            IEnumerable<JSONNode> models, Func<string, JSONObject> values, Action changed)
        {
            foreach (var model in models)
            {
                var id = model["id"].Value;
                var controls = new List<Action<bool, JSONObject>>();
                rows.Add(id, controls);
                foreach (var pair in model["settings"].AsObject ?? new JSONObject())
                {
                    var key = pair.Key;
                    var spec = pair.Value;
                    var label = spec.HasKey("label") ? spec["label"].Value : key;
                    var slot = AddRow(before, label);
                    Slots.Add(slot);
                    var defaultValue = spec["default"];
                    Action<JSONNode> save = value =>
                    {
                        values(id)[key] = value;
                        changed?.Invoke();
                    };
                    Selectable selectable;
                    Action<JSONNode> refresh;
                    switch (spec["type"].Value)
                    {
                        case "number":
                            var slider = DialogLayout.Mount(dialog.AddComponent<SliderComponent>(), slot);
                            slider.WithLabel(label).WithCustomDisplayFormatter(value => value.ToString("0.###"));
                            slider.WithSliderParams(spec["min"].AsFloat, spec["max"].AsFloat,
                                spec.HasKey("step") ? spec["step"].AsFloat : .01f);
                            GeneratorSettings.AlignSliderColumns(slider);
                            slider.OnChanged<SliderComponent, float>(value =>
                                save(new JSONNumber(double.Parse(value.ToString("G7", CultureInfo.InvariantCulture),
                                    CultureInfo.InvariantCulture))));
                            selectable = slider.Selectable;
                            refresh = value => slider.SetValueWithoutNotify(value.AsFloat);
                            break;
                        case "boolean":
                            var toggle = DialogLayout.Mount(dialog.AddComponent<ToggleComponent>(), slot);
                            toggle.WithLabel(label).OnChanged<ToggleComponent, bool>(value => save(new JSONBool(value)));
                            selectable = toggle.Selectable;
                            refresh = value => toggle.SetValueWithoutNotify(value.AsBool);
                            break;
                        default:
                            var options = spec["options"].Children.Select(option => option.Value).ToArray();
                            var dropdown = DialogLayout.Mount(dialog.AddComponent<DropdownComponent>(), slot);
                            dropdown.WithLabel(label).WithOptions(options);
                            dropdown.OnChanged<DropdownComponent, int>(index => save(new JSONString(options[index])));
                            selectable = dropdown.Selectable;
                            refresh = value => dropdown.SetValueWithoutNotify(Array.IndexOf(options, value.Value));
                            break;
                    }
                    if (!string.IsNullOrWhiteSpace(spec["description"].Value))
                    {
                        var hover = spec["type"].Value == "number"
                            ? SettingHint.ForSlider(slot.GetComponentInChildren<SliderComponent>())
                            : selectable.gameObject.AddComponent<SettingHint>();
                        hover.Hint = spec["description"].Value;
                    }
                    controls.Add((visible, settings) =>
                    {
                        slot.gameObject.SetActive(visible);
                        if (visible) refresh(settings.HasKey(key) ? settings[key] : defaultValue);
                    });
                }
            }
        }

        internal void Show(string id, JSONObject values)
        {
            foreach (var model in rows)
                foreach (var control in model.Value)
                    control(model.Key == id, values);
        }

        internal static RectTransform AddRow(RectTransform before, string name)
        {
            var row = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
            row.transform.SetParent(before.parent, false);
            row.transform.SetSiblingIndex(before.GetSiblingIndex());
            var size = row.GetComponent<LayoutElement>();
            size.minHeight = size.preferredHeight = before.GetComponent<LayoutElement>().preferredHeight;
            return (RectTransform)row.transform;
        }
    }
}
