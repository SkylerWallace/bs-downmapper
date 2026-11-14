using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BsDownmap.ChroMapper
{
    internal sealed class TimingOverview : MaskableGraphic, IPointerDownHandler, IBeginDragHandler, IDragHandler
    {
        private Action<double> seek;
        private TimingWaveform waveform;
        private double duration;
        private (double from, double to, Color color)[] sectionRanges;
        private (double from, double to, Color color)[] phraseRanges;
        private bool showPhrases;
        private RectTransform window, playhead;

        internal void Initialize(Action<double> seekTo, (double, double, Color)[] sections,
            (double, double, Color)[] phrases)
        {
            seek = seekTo;
            sectionRanges = sections;
            phraseRanges = phrases;
            window = AddOverlay("Visible range", new Color(.54f, .78f, .90f, .19f));
            playhead = AddOverlay("Overview playhead", new Color(.97f, .37f, .43f), 2);
        }

        internal void ShowPhrases(bool visible)
        {
            if (showPhrases == visible) return;
            showPhrases = visible;
            SetVerticesDirty();
        }

        internal void Refresh(TimingWaveform peaks, double length, double start, double end, double cursor)
        {
            if (waveform != peaks || duration != length)
            {
                waveform = peaks;
                duration = length;
                SetVerticesDirty();
            }
            if (length <= 0) return;
            var left = (float)Math.Max(0, Math.Min(1, start / length));
            var right = (float)Math.Max(left, Math.Min(1, end / length));
            var position = (float)Math.Max(0, Math.Min(1, cursor / length));
            Stretch(window, left, right);
            Stretch(playhead, position, position);
        }

        private RectTransform AddOverlay(string name, Color color, float width = 0)
        {
            var child = new GameObject(name, typeof(RectTransform), typeof(Image));
            child.transform.SetParent(transform, false);
            var image = child.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.up;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.sizeDelta = new Vector2(width, 0);
            return rect;
        }

        private static void Stretch(RectTransform rect, float left, float right)
        {
            var min = new Vector2(left, 0);
            var max = new Vector2(right, 1);
            if (rect.anchorMin != min) rect.anchorMin = min;
            if (rect.anchorMax != max) rect.anchorMax = max;
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var rect = rectTransform.rect;
            TimingTimeline.AddQuad(mesh, rect.xMin, rect.xMax, rect.yMin, rect.yMax, new Color(.105f, .111f, .125f));
            if (duration <= 0 || rect.width <= 0) return;
            PaintRanges(sectionRanges, rect.yMin, rect.yMax, .24f);
            if (showPhrases) PaintRanges(phraseRanges, rect.yMax - 5, rect.yMax, .85f);
            if (waveform?.Available != true) return;
            int columns = Mathf.Clamp(Mathf.CeilToInt(rect.width), 1, 1024);
            float width = rect.width / columns;
            float center = rect.center.y, amplitude = rect.height * .42f;
            var color = new Color(.35f, .76f, .86f, .75f);
            for (int i = 0; i < columns; i++)
            {
                var peak = waveform.Peaks(i * duration / columns, (i + 1) * duration / columns);
                float left = rect.xMin + i * width;
                float bottom = center + Mathf.Clamp(peak.x, -1, 1) * amplitude;
                float top = center + Mathf.Clamp(peak.y, -1, 1) * amplitude;
                TimingTimeline.AddQuad(mesh, left, left + width, bottom, Mathf.Max(bottom + 1, top), color);
            }

            void PaintRanges((double from, double to, Color color)[] ranges, float bottom, float top, float opacity)
            {
                if (ranges == null) return;
                foreach (var range in ranges)
                {
                    float left = rect.xMin + Mathf.Clamp01((float)(range.from / duration)) * rect.width;
                    float right = rect.xMin + Mathf.Clamp01((float)(range.to / duration)) * rect.width;
                    if (right <= left) continue;
                    var tint = range.color;
                    tint.a = opacity;
                    TimingTimeline.AddQuad(mesh, left, right, bottom, top, tint);
                }
            }
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left) Seek(e);
        }

        public void OnBeginDrag(PointerEventData e) { }

        public void OnDrag(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left) Seek(e);
        }

        private void Seek(PointerEventData e)
        {
            if (duration <= 0 || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, e.position, e.pressEventCamera, out var point)) return;
            var rect = rectTransform.rect;
            if (rect.width > 0) seek(Mathf.Clamp01((point.x - rect.xMin) / rect.width));
        }
    }
}
