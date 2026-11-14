using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BsDownmap.ChroMapper
{
    internal sealed class DialogLayout
    {
        internal const string BundleName = "bsdownmap-layouts";
        private const string SlotPrefix = "Slot ";
        private static AssetBundle bundle;
        private readonly GameObject prefab;
        private readonly Dictionary<string, RectTransform> slots = new Dictionary<string, RectTransform>();

        private DialogLayout(GameObject prefab) => this.prefab = prefab;

        internal static DialogLayout Load(string name)
        {
            if (bundle == null)
            {
                bundle = AssetBundle.LoadFromMemory(BundledAssets.ReadBytes(BundleName));
                if (bundle == null)
                    throw new InvalidOperationException($"Could not load {BundleName}. Rebuild it for this ChroMapper Unity version.");
            }
            var prefab = bundle.LoadAsset<GameObject>($"Assets/Layouts/{name}.prefab");
            if (prefab == null) throw new InvalidOperationException($"Missing {name} in {BundleName}.");
            return new DialogLayout(prefab);
        }

        internal static void Unload()
        {
            if (bundle == null) return;
            bundle.Unload(false);
            bundle = null;
        }

        internal RectTransform Slot(string name)
        {
            if (!slots.TryGetValue(name, out var slot))
                throw new InvalidOperationException($"{prefab.name} is missing Slot {name}. Keep slot names when editing the prefab.");
            return slot;
        }

        internal RectTransform AttachBody(Transform nativeBody, bool fillHeight, bool clearFramePadding = false)
        {
            var body = Attach("Body", nativeBody);
            SetBodyHeight(body, fillHeight, clearFramePadding);
            return body;
        }

        internal void SetBodyHeight(Transform body, bool fillHeight, bool clearFramePadding = false)
        {
            var nativeBody = body.parent;
            var nativeSize = nativeBody.GetComponent<LayoutElement>() ?? nativeBody.gameObject.AddComponent<LayoutElement>();
            var size = body.GetComponent<LayoutElement>();
            size.flexibleHeight = fillHeight ? prefab.transform.Find("Body").GetComponent<LayoutElement>().flexibleHeight : 0;
            nativeSize.flexibleHeight = size.flexibleHeight;
            var frame = (RectTransform)nativeBody.parent;
            var bounds = (RectTransform)prefab.transform.Find("Frame");
            frame.anchorMin = new Vector2(bounds.anchorMin.x, fillHeight ? bounds.anchorMin.y : 0);
            frame.anchorMax = new Vector2(bounds.anchorMax.x, fillHeight ? bounds.anchorMax.y : 1);
            frame.offsetMin = new Vector2(bounds.offsetMin.x, fillHeight ? bounds.offsetMin.y : 0);
            frame.offsetMax = new Vector2(bounds.offsetMax.x, fillHeight ? bounds.offsetMax.y : 0);
            if (clearFramePadding)
                frame.GetComponent<VerticalLayoutGroup>().padding = new RectOffset();
        }

        internal void AttachFooter(Transform nativeFooter)
        {
            var footer = Attach("Footer", nativeFooter);
            var size = footer.GetComponent<LayoutElement>();
            // Stable sizes the footer row from its children. The authored slots
            // supply the height after the native buttons have been reparented.
            float height = 0;
            foreach (Transform slot in footer)
                if (slot.TryGetComponent<LayoutElement>(out var item))
                    height = Mathf.Max(height, item.minHeight, item.preferredHeight);
            if (height > 0)
                size.minHeight = size.preferredHeight = height;
        }

        private RectTransform Attach(string name, Transform parent)
        {
            var source = (RectTransform)prefab.transform.Find(name);
            if (source == null) throw new InvalidOperationException($"{prefab.name} is missing its {name}.");
            var root = Object.Instantiate(source, parent, false);
            root.name = name;
            foreach (var rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.name.StartsWith(SlotPrefix, StringComparison.Ordinal))
                    slots.Add(rect.name.Substring(SlotPrefix.Length), rect);
                else if (rect.name == "Preview")
                    rect.gameObject.SetActive(false);
            }
            return root;
        }

        internal T Mount<T>(T control, string slot, bool compact = true) where T : CMUIComponentBase
            => Mount(control, Slot(slot), compact);

        internal static T Mount<T>(T control, RectTransform slot, bool compact = true) where T : CMUIComponentBase
        {
            Fill((RectTransform)control.transform, slot);
            if (compact)
                foreach (var text in control.GetComponentsInChildren<TMP_Text>(true))
                {
                    text.fontSize *= .82f;
                    if (!text.enableAutoSizing) continue;
                    text.fontSizeMin *= .82f;
                    text.fontSizeMax *= .82f;
                }
            if (control is ButtonComponent button)
                FillControl(button);
            else if (control is DropdownComponent && control is INavigable field)
                ((RectTransform)field.Selectable.transform).SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Vertical, slot.GetComponent<LayoutElement>().preferredHeight);
            return control;
        }

        internal static void Fill(RectTransform control, RectTransform slot)
        {
            control.SetParent(slot, false);
            control.anchorMin = Vector2.zero;
            control.anchorMax = Vector2.one;
            control.offsetMin = control.offsetMax = Vector2.zero;
        }

        // Some native controls have their own layout offsets. Use the authored slot
        // edges for controls that share a row with a separately mounted button.
        internal static void FillControl(INavigable field)
        {
            var control = (RectTransform)field.Selectable.transform;
            var root = ((CMUIComponentBase)field).transform;
            for (var rect = control; rect != root; rect = (RectTransform)rect.parent)
            {
                var parent = (RectTransform)rect.parent;
                if (parent.TryGetComponent<LayoutGroup>(out var group)) group.enabled = false;
                Fill(rect, parent);
            }
        }
    }
}
