using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    internal sealed class TimingTimeline : MaskableGraphic, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler
    {
        private readonly List<(Rect rect, Color color)> quads = new List<(Rect, Color)>();
        private readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
        private readonly List<Image> pulses = new List<Image>();
        private TimingWindow owner;
        private TMP_FontAsset font;
        private RectTransform playhead;
        private Camera pointerCamera;
        private bool pointerInside;
        private int labelCount, pulseCount;
        private Vector2 size, pointer;
        private double viewStart = -1;
        private float viewSpan;
        internal int WaveformColumns => Mathf.Clamp(Mathf.RoundToInt(rectTransform.rect.width *
            (canvas != null ? canvas.scaleFactor : 1)), 1, 2048);
        private Rect Bounds => new Rect(Vector2.zero, size);

        internal void Initialize(TimingWindow window, TMP_FontAsset labelFont)
        {
            owner = window;
            font = labelFont;
            var head = Child<Image>("Playhead");
            head.color = new Color(.97f, .37f, .43f);
            head.raycastTarget = false;
            playhead = head.rectTransform;
        }

        internal void Refresh(bool dirty, double start, float span, double cursor)
        {
            var dimensions = rectTransform.rect.size;
            var mouse = pointerInside && Mouse.current != null ? Local(Mouse.current.position.ReadValue(), pointerCamera)
                : new Vector2(-1, -1);
            if (dirty || start != viewStart || span != viewSpan || dimensions != size || mouse != pointer)
            {
                bool rebuild = dirty || start != viewStart || span != viewSpan || dimensions != size;
                viewStart = start;
                viewSpan = span;
                size = dimensions;
                pointer = mouse;
                bool hoverChanged = owner.UpdateTimelineHover(Bounds, pointer);
                if (size.x > 0 && size.y > 0 && (rebuild || hoverChanged))
                {
                    quads.Clear();
                    labelCount = 0;
                    owner.PaintTimeline(this, Bounds);
                    for (int i = labelCount; i < labels.Count; i++)
                        labels[i].gameObject.SetActive(false);
                    SetVerticesDirty();
                }
            }
            float headHeight = size.y - TimingWindow.SectionHeader - owner.MergeLaneHeight;
            bool visible = cursor >= start && cursor <= start + span && size.x > 0 && headHeight > 0;
            if (playhead.gameObject.activeSelf != visible) playhead.gameObject.SetActive(visible);
            if (visible)
            {
                var x = Mathf.Clamp((float)((cursor - start) / span) * size.x, 0, Mathf.Max(0, size.x - 2));
                playhead.anchoredPosition = new Vector2(x, -TimingWindow.SectionHeader);
                var headSize = new Vector2(2, headHeight);
                if (playhead.sizeDelta != headSize) playhead.sizeDelta = headSize;
            }
            int previousPulses = pulseCount;
            pulseCount = 0;
            if (visible) owner.PaintMarkerPulses(this, Bounds);
            for (int i = pulseCount; i < previousPulses; i++) pulses[i].gameObject.SetActive(false);
        }

        internal void Pulse(Rect rect, Color color)
        {
            if (pulseCount == pulses.Count)
            {
                var image = Child<Image>("Timing pulse");
                image.raycastTarget = false;
                image.transform.SetSiblingIndex(1 + pulses.Count);
                pulses.Add(image);
            }
            var pulse = pulses[pulseCount++];
            if (!pulse.gameObject.activeSelf) pulse.gameObject.SetActive(true);
            pulse.color = color;
            pulse.rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            pulse.rectTransform.sizeDelta = rect.size;
        }

        internal void Paint(Rect rect, Color color)
        {
            float x = Mathf.Max(0, rect.xMin), y = Mathf.Max(0, rect.yMin);
            float width = Mathf.Min(size.x, rect.xMax) - x, height = Mathf.Min(size.y, rect.yMax) - y;
            if (width > 0 && height > 0) quads.Add((new Rect(x, y, width, height), color));
        }

        internal void Label(Rect rect, string value)
        {
            if (labelCount == labels.Count)
            {
                var label = Child<TextMeshProUGUI>("Timeline label");
                label.font = font;
                label.raycastTarget = false;
                label.fontSize = 15;
                label.enableWordWrapping = false;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                labels.Add(label);
            }
            var next = labels[labelCount++];
            next.gameObject.SetActive(true);
            next.text = value;
            next.rectTransform.anchoredPosition = new Vector2(rect.x, -rect.y);
            next.rectTransform.sizeDelta = rect.size;
        }

        private T Child<T>(string name) where T : Graphic
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(transform, false);
            var graphic = child.AddComponent<T>();
            var rect = graphic.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            return graphic;
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var panel = rectTransform.rect;
            foreach (var quad in quads)
            {
                var rect = quad.rect;
                float left = panel.xMin + rect.xMin, right = panel.xMin + rect.xMax;
                float top = panel.yMax - rect.yMin, bottom = panel.yMax - rect.yMax;
                AddQuad(mesh, left, right, bottom, top, quad.color);
            }
        }

        internal static void AddQuad(VertexHelper mesh, float left, float right, float bottom, float top, Color color)
        {
            int vertex = mesh.currentVertCount;
            mesh.AddVert(new Vector3(left, bottom), color, Vector2.zero);
            mesh.AddVert(new Vector3(left, top), color, Vector2.zero);
            mesh.AddVert(new Vector3(right, top), color, Vector2.zero);
            mesh.AddVert(new Vector3(right, bottom), color, Vector2.zero);
            mesh.AddTriangle(vertex, vertex + 1, vertex + 2);
            mesh.AddTriangle(vertex, vertex + 2, vertex + 3);
        }

        private Vector2 Local(Vector2 screen, Camera camera)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, screen, camera, out var local))
                return new Vector2(-1, -1);
            var rect = rectTransform.rect;
            return new Vector2(local.x - rect.xMin, rect.yMax - local.y);
        }

        public void OnPointerEnter(PointerEventData e)
        {
            pointerInside = true;
            pointerCamera = e.enterEventCamera;
        }

        public void OnPointerExit(PointerEventData e) => pointerInside = false;

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            var point = Local(e.position, e.pressEventCamera);
            if (Bounds.Contains(point)) owner.ClickTimeline(Bounds, point);
        }
    }
}
