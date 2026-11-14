using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BsDownmap.ChroMapper
{
    public sealed partial class TimingWindow
    {
        internal const float SectionHeader = 24;
        private const float MarkerHeight = 18;
        internal float MergeLaneHeight => data.merges.Length > 0 ? 23 : 0;
        private const float WaveformTop = SectionHeader + 22;
        private float WaveformBottom(Rect grid) => grid.height - MergeLaneHeight - 5;
        internal float MarkerCenter(Rect grid) => (WaveformTop + WaveformBottom(grid)) / 2;
        private Vector2 timelinePointer;
        private float BeatX(Rect grid, double beat) => grid.width * (float)((beat - start) / span);

        private IEnumerable<TimingSection> VisibleSections(TimingSection[] sections) =>
            sections.Where(section => section.Start < start + span && section.End > start);

        private IEnumerable<(TimingSection section, Rect badge)> SectionBadges(Rect grid)
        {
            var sections = VisibleSections(data.sections);
            if (linkWithinSection) sections = sections.Concat(VisibleSections(data.subSections));
            float occupied = 0;
            foreach (var section in sections.OrderBy(section => section.Start))
            {
                float x = Mathf.Max(occupied, BeatX(grid, section.Start)) + 2;
                float width = 11 + 8 * section.Label.Length;
                if (x + width > Mathf.Min(grid.width, BeatX(grid, section.End))) continue;
                occupied = x + width;
                yield return (section, new Rect(x, 2, width, SectionHeader - 4));
            }
        }

        private IEnumerable<TimingMerge> VisibleMerges() =>
            merges.Where(merge => MergeAvailable(merge) && Math.Max(merge.from, merge.to) >= start &&
                Math.Min(merge.from, merge.to) <= start + span);

        private TimingSection PointedSection(Rect grid, Vector2 point) =>
            point.y >= 0 && point.y < SectionHeader
                ? SectionBadges(grid).FirstOrDefault(item => item.badge.Contains(point)).section
                : null;

        internal bool UpdateTimelineHover(Rect grid, Vector2 point)
        {
            var previous = hoveredSection;
            var previousPointer = timelinePointer;
            timelinePointer = point;
            hoveredSection = PointedSection(grid, point) ??
                (previous != null && RepeatPopup(grid, previous, out _, out _).Contains(point) ? previous : null);
            return previous != hoveredSection || (hoveredSection != null && previousPointer != point);
        }

        private Rect RepeatPopup(Rect grid, TimingSection active, out TimingSection[] peers, out int columns)
        {
            var sections = active.Scope < 0 ? data.sections : data.subSections;
            peers = sections.Where(section => section.Scope == active.Scope &&
                section.Group == active.Group && section != active).ToArray();
            columns = 0;
            if (peers.Length == 0 || active.Start >= start + span || active.End <= start) return Rect.zero;
            columns = Math.Max(1, Math.Min(Math.Min(7, peers.Length), (int)(grid.width - 12) / 80));
            float width = Math.Min(grid.width, columns * 80 + 12);
            var badge = SectionBadges(grid).FirstOrDefault(item => item.section == active).badge;
            return new Rect(Mathf.Clamp(badge.x, 0, grid.width - width),
                SectionHeader - 2, width, 8 + 25 * Mathf.CeilToInt(peers.Length / (float)columns));
        }

        private static Rect RepeatLink(Rect popup, int columns, int index) =>
            new Rect(popup.x + 6 + index % columns * 80, popup.y + 4 + index / columns * 25, 76, 22);

        internal void PaintTimeline(TimingTimeline draw, Rect grid)
        {
            var end = start + span;
            float Pixel(double beat) => BeatX(grid, beat);
            float noteY = MarkerCenter(grid) - MarkerHeight / 2;
            float mergeY = grid.yMax - 11;
            var popup = hoveredSection == null ? Rect.zero : RepeatPopup(grid, hoveredSection, out _, out _);
            draw.Paint(grid, new Color(.105f, .111f, .125f));
            foreach (var section in VisibleSections(data.sections))
            {
                var color = SectionColors[section.Group % SectionColors.Length];
                var left = Mathf.Max(0, Pixel(section.Start));
                var right = Mathf.Min(grid.width, Pixel(section.End));
                color.a = linkSections && !linkWithinSection ? .20f : .09f;
                draw.Paint(new Rect(left, 0, right - left, grid.height), color);
            }
            if (waveform?.Available == true) PaintWaveform(draw, grid);
            if (MergeLaneHeight > 0)
                draw.Paint(new Rect(0, grid.height - MergeLaneHeight, grid.width, 1), new Color(.40f, .44f, .49f));

            if (linkWithinSection)
                foreach (var phrase in VisibleSections(data.subSections))
                {
                    float left = Mathf.Max(0, Pixel(phrase.Start));
                    float right = Mathf.Min(grid.width, Pixel(phrase.End));
                    var color = SectionColors[phrase.Group % SectionColors.Length];
                    draw.Paint(new Rect(left, SectionHeader - 3, right - left, 3), color);
                }

            int divisions = grid.width / span / subdivision >= 4 ? subdivision : 1;
            for (long i = (long)Math.Ceiling((double)start * divisions); i <= (double)end * divisions; i++)
            {
                var x = Pixel(i / (double)divisions);
                bool major = i % divisions == 0;
                draw.Paint(new Rect(x, SectionHeader, 1, grid.height - SectionHeader),
                    major ? new Color(.52f, .55f, .60f, .65f) : new Color(.28f, .30f, .34f, .65f));
                if (major && x < grid.xMax - 25 && (span <= 16 || i % (4 * divisions) == 0))
                {
                    var label = new Rect(x + 2, SectionHeader + 1, 42, 20);
                    if (!label.Overlaps(popup)) draw.Label(label, (i / divisions).ToString());
                }
            }
            foreach (var region in data.regions.Where(region => region.beat >= start && region.beat < end))
                draw.Paint(new Rect(Pixel(region.beat), SectionHeader + 22, 1, 12), new Color(.55f, .57f, .95f));

            foreach (var marker in VisibleTimings(end))
            {
                float x = Pixel(marker.beat);
                draw.Paint(new Rect(x - 2, noteY, 4, MarkerHeight),
                    IsMarkerEnabled(marker) ? Color.white : DisabledMarkerColor);
            }
            foreach (var merge in VisibleMerges())
            {
                float from = Mathf.Clamp(Pixel(merge.from), 3, grid.width - 3);
                float to = Mathf.Clamp(Pixel(merge.to), 3, grid.width - 3);
                draw.Paint(new Rect(Mathf.Min(from, to), mergeY, Mathf.Abs(from - to), 2), MergeColor);
                draw.Paint(new Rect(from - 2, mergeY - 4, 4, 10), MergeColor);
                draw.Paint(new Rect(to - 3, mergeY - 6, 6, 14), MergeColor);
            }
            foreach (var (section, badge) in SectionBadges(grid))
            {
                var color = SectionColors[section.Group % SectionColors.Length] * .52f;
                color.a = 1;
                draw.Paint(badge, color);
                draw.Label(new Rect(badge.x + 4, badge.y, badge.width - 4, badge.height), section.Label);
            }
            if (hoveredSection == null || popup.width <= 0) return;
            RepeatPopup(grid, hoveredSection, out var peers, out var columns);
            draw.Paint(popup, new Color(.13f, .17f, .23f));
            for (int i = 0; i < peers.Length; i++)
            {
                var link = RepeatLink(popup, columns, i);
                draw.Paint(link, link.Contains(timelinePointer) ?
                    new Color(.29f, .40f, .52f) : new Color(.21f, .29f, .38f));
                draw.Label(new Rect(link.x + 5, link.y, link.width - 5, link.height), peers[i].Label);
            }
        }

        private void PaintWaveform(TimingTimeline draw, Rect grid)
        {
            double beatWidth = span / (double)draw.WaveformColumns;
            long first = (long)Math.Floor(start / beatWidth + 1e-8);
            long end = (long)Math.Ceiling((start + span) / beatWidth - 1e-8);
            float top = WaveformTop, bottom = WaveformBottom(grid);
            if (bottom <= top) return;
            float center = (top + bottom) / 2, amplitude = (bottom - top) / 2;
            var color = new Color(.35f, .76f, .86f, .66f);
            double from = Seconds(first * beatWidth);
            for (long column = first; column < end; column++)
            {
                // Columns are tied to absolute beats. Scrolling reuses the same audio intervals.
                double to = Seconds((column + 1) * beatWidth);
                var peak = waveform.Peaks(from, to);
                float yMin = center - Mathf.Clamp(peak.y, -1, 1) * amplitude;
                float yMax = center - Mathf.Clamp(peak.x, -1, 1) * amplitude;
                float x = BeatX(grid, column * beatWidth);
                float right = BeatX(grid, (column + 1) * beatWidth);
                draw.Paint(new Rect(x, yMin, right - x, Mathf.Max(1, yMax - yMin)), color);
                from = to;
            }
        }

        internal void ClickTimeline(Rect grid, Vector2 point)
        {
            if (PointedSection(grid, point) != null) return;
            if (hoveredSection != null)
            {
                var popup = RepeatPopup(grid, hoveredSection, out var peers, out var columns);
                if (popup.Contains(point))
                {
                    for (int i = 0; i < peers.Length; i++)
                    {
                        if (!RepeatLink(popup, columns, i).Contains(point)) continue;
                        HaltPlayback();
                        cursor = peers[i].Start;
                        start = ClampStart(cursor);
                        hoveredSection = null;
                        timelineDirty = true;
                        break;
                    }
                    return;
                }
            }

            HaltPlayback();
            cursor = SnapBeat(start + point.x * span / grid.width);
            if (data.merges.Length > 0 && point.y >= grid.yMax - 20)
            {
                var link = VisibleMerges().FirstOrDefault(merge => point.x >= BeatX(grid, Math.Min(merge.from, merge.to)) - 5 &&
                    point.x <= BeatX(grid, Math.Max(merge.from, merge.to)) + 5);
                if (link != null) { UndoMerge(link); return; }
            }
            if (Mathf.Abs(point.y - MarkerCenter(grid)) > MarkerHeight / 2 + 5) return;
            var marker = VisibleTimings(start + span).OrderBy(item => Math.Abs(BeatX(grid, item.beat) - point.x)).FirstOrDefault();
            if (marker == null || Math.Abs(BeatX(grid, marker.beat) - point.x) > 6 ||
                inherited.Contains(marker.noteIds[0])) return;
            var activeMerge = ActiveMerge(marker);
            if (activeMerge != null) UndoMerge(activeMerge);
            else
            {
                bool restore = !IsMarkerEnabled(marker);
                if (!marker.locked || restore) EditMarker(marker, restore);
            }
        }

        private bool IsMarkerEnabled(TimingMarker marker)
        {
            if (inherited.Contains(marker.noteIds[0])) return false;
            var merge = ActiveMerge(marker);
            return merge == null ? marker.noteIds.Any(id => !removed.Contains(id)) : merge.targetId == marker.noteIds[0];
        }

        internal void PaintMarkerPulses(TimingTimeline draw, Rect grid)
        {
            const double duration = .16;
            if (!playing || grid.width <= 0) return;
            double seconds = Seconds(cursor);
            double first = Math.Max(start, Beat(seconds - duration));
            foreach (var marker in VisibleTimings(Math.Min(start + span, cursor), first))
            {
                if (!IsMarkerEnabled(marker)) continue;
                float strength = Mathf.Clamp01(1 - (float)((seconds - Seconds(marker.beat)) / duration));
                float width = 4 + 10 * strength;
                float height = MarkerHeight + 14 * strength;
                draw.Pulse(new Rect(BeatX(grid, marker.beat) - width / 2, MarkerCenter(grid) - height / 2,
                    width, height), new Color(.53f, .95f, 1, strength));
            }
        }
    }
}
