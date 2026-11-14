using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    public readonly record struct ChartEndpoint(double Beat, int Hand, double X, double Y);
    public readonly record struct ChartLinkId(string Collection, int Index);
    public sealed record ChartLink(ChartLinkId Id, IReadOnlyList<ChartEndpoint> Endpoints, ISet<int> AnchorIds);
    public sealed record TimingLinkPlan(ISet<ChartLinkId> Optional, ISet<ChartLinkId> Released,
        ISet<int> LockedAnchors, ISet<double> AccentBeats, IReadOnlyList<ChartEndpoint> Endpoints);

    public sealed partial class ChartGameplay
    {
        public TimingLinkPlan PrepareTimingLinks(ISet<int> protectedIds, double? maximumReach = null,
            Func<Note, bool> anchorAllowed = null, ISet<int> eligibleIds = null,
            ISet<ChartLinkId> alreadyReleased = null)
        {
            var optional = new HashSet<ChartLinkId>();
            var released = new HashSet<ChartLinkId>();
            var locked = new HashSet<int>();
            var accents = new HashSet<double>();
            var endpoints = new List<ChartEndpoint>();
            var notes = Notes.ToDictionary(note => note.Id);
            foreach (var link in ReadLinks())
            {
                if (alreadyReleased?.Contains(link.Id) == true)
                {
                    released.Add(link.Id);
                    continue;
                }
                var attached = link.AnchorIds.Where(id => eligibleIds == null || eligibleIds.Contains(id))
                    .Select(id => notes[id]).ToArray();
                accents.UnionWith(attached.Select(note => Math.Round(note.Beat, 5)));
                bool cue = attached.Any(note => protectedIds.Contains(note.Id));
                if (!cue && (anchorAllowed != null && attached.Any(note => !anchorAllowed(note)) ||
                    maximumReach.HasValue && LinkReach(link) > maximumReach.Value))
                {
                    released.Add(link.Id);
                    continue;
                }
                if (cue)
                    locked.UnionWith(attached.Select(note => note.Id));
                else
                    optional.Add(link.Id);
                endpoints.AddRange(link.Endpoints);
            }
            return new TimingLinkPlan(optional, released, locked, accents, endpoints);
        }

        public ISet<ChartLinkId> EasyStaggeredLinks(ChartClock clock, double minimumGap,
            ISet<int> protectedIds, ISet<int> eligibleIds)
        {
            var candidates = new List<(ChartLink Link, double Head, double Tail, int Hand, bool Chain)>();
            foreach (var link in ReadLinks())
            {
                if (link.Endpoints.Count == 0 || link.Id.Collection == "arcs" && link.Endpoints.Count != 2)
                    continue;
                if (link.Endpoints.Any(point =>
                {
                    var attached = Notes.Where(note => eligibleIds.Contains(note.Id) && Matches(note, point)).ToArray();
                    return attached.Length != 1 || protectedIds.Contains(attached[0].Id);
                }))
                    continue;
                double head = link.Endpoints[0].Beat;
                bool chain = link.Endpoints.Count == 1;
                double tail = chain ? Number((JsonObject)((JsonArray)source[link.Id.Collection])[link.Id.Index], "tb") :
                    link.Endpoints[^1].Beat;
                if (chain && clock.Gap(head, tail) > .18)
                    continue;
                candidates.Add((link, head, tail, link.Endpoints[0].Hand, chain));
            }
            var kept = new List<(ChartLink Link, double Head, double Tail, int Hand, bool Chain)>();
            var removed = new HashSet<ChartLinkId>();
            foreach (var item in candidates.OrderBy(item => Weakness(item.Head))
                .ThenBy(item => item.Chain && item.Head < Math.Round(item.Head)).ThenBy(item => Weakness(item.Tail))
                .ThenBy(item => item.Head))
            {
                if (kept.Any(other => Conflict(item, other)))
                    removed.Add(item.Link.Id);
                else
                    kept.Add(item);
            }
            return removed;

            static double Weakness(double beat) => Math.Round(Math.Abs(beat - Math.Round(beat)), 4);

            bool Conflict((ChartLink Link, double Head, double Tail, int Hand, bool Chain) a,
                (ChartLink Link, double Head, double Tail, int Hand, bool Chain) b)
            {
                if (a.Link.Id.Collection != b.Link.Id.Collection)
                    return false;
                double gap = Math.Abs(clock.Gap(a.Head, b.Head));
                if (gap <= .001)
                    return false;
                if (a.Chain)
                    return a.Hand == b.Hand && Math.Round(a.Head) == Math.Round(b.Head) && gap < 1.25 * minimumGap;
                return a.Hand != b.Hand && gap < minimumGap && Math.Abs(clock.Gap(a.Tail, b.Tail)) < minimumGap;
            }
        }

        private double LinkReach(ChartLink link)
        {
            var row = (JsonObject)((JsonArray)source[link.Id.Collection])[link.Id.Index];
            double x, y, tailX, tailY;
            switch (link.Id.Collection)
            {
                case "arcs":
                    var table = (JsonArray)source["colorNotesData"];
                    var head = At(table, Number(row, "hi"));
                    var tail = At(table, Number(row, "ti"));
                    x = Number(head, "x");
                    y = Number(head, "y");
                    tailX = Number(tail, "x");
                    tailY = Number(tail, "y");
                    break;
                case "chains":
                    var chain = At((JsonArray)source["chainsData"], Number(row, "ci"));
                    (x, y) = (link.Endpoints[0].X, link.Endpoints[0].Y);
                    (tailX, tailY) = (Number(chain, "tx"), Number(chain, "ty"));
                    break;
                case "burstSliders":
                    (x, y) = (link.Endpoints[0].X, link.Endpoints[0].Y);
                    (tailX, tailY) = (Number(row, "tx"), Number(row, "ty"));
                    break;
                default:
                    (x, y) = (link.Endpoints[0].X, link.Endpoints[0].Y);
                    (tailX, tailY) = (link.Endpoints[^1].X, link.Endpoints[^1].Y);
                    break;
            }
            return new SwingClearance.Point(tailX - x, tailY - y).Length + (Math.Max(y, tailY) > 1 ? .6 : 0);

            static JsonObject At(JsonArray table, double index)
            {
                if (table == null || index < 0 || index >= table.Count || index != Math.Truncate(index))
                    throw new InvalidDataException("Linked object data index is out of range.");
                return (JsonObject)table[(int)index];
            }
        }

        public IReadOnlyList<ChartLink> ReadLinks()
        {
            var links = new List<ChartLink>();
            if (version == 4)
            {
                var table = source["colorNotesData"] as JsonArray;
                ReadCollection("arcs", row =>
                {
                    var points = new List<ChartEndpoint>();
                    if (Number(row, "hr") == 0)
                        points.Add(Indexed(row, "hb", "hi", table));
                    if (Number(row, "tr") == 0)
                        points.Add(Indexed(row, "tb", "ti", table));
                    return points.ToArray();
                });
                ReadCollection("chains", row => new[] { Indexed(row, "hb", "i", table) });
            }
            else if (version == 3)
            {
                ReadCollection("sliders", row => new[] { Endpoint(row, "b", "c", "x", "y"), Endpoint(row, "tb", "c", "tx", "ty") });
                ReadCollection("burstSliders", row => new[] { Endpoint(row, "b", "c", "x", "y") });
            }
            else
            {
                ReadCollection("_sliders", row => new[]
                {
                    Endpoint(row, "_headTime", "_colorType", "_headLineIndex", "_headLineLayer"),
                    Endpoint(row, "_tailTime", "_colorType", "_tailLineIndex", "_tailLineLayer"),
                });
            }
            return links;

            void ReadCollection(string name, Func<JsonObject, ChartEndpoint[]> read)
            {
                int index = 0;
                foreach (var row in Rows(source, name))
                {
                    var endpoints = read(row);
                    var anchors = Notes.Where(note => endpoints.Any(point => Matches(note, point)))
                        .Select(note => note.Id).ToHashSet();
                    links.Add(new ChartLink(new ChartLinkId(name, index++), endpoints, anchors));
                }
            }

            static ChartEndpoint Indexed(JsonObject row, string beatKey, string indexKey, JsonArray table)
            {
                double index = Number(row, indexKey);
                if (table == null || index < 0 || index >= table.Count || index != Math.Truncate(index))
                    throw new InvalidDataException("Linked object endpoint index is out of range.");
                var note = (JsonObject)table[(int)index];
                return new ChartEndpoint(Number(row, beatKey), (int)Number(note, "c"), Number(note, "x"), Number(note, "y"));
            }

            static ChartEndpoint Endpoint(JsonObject row, string b, string c, string x, string y) =>
                new(Number(row, b), (int)Number(row, c), Number(row, x), Number(row, y));
        }

        public (JsonObject Chart, IReadOnlyList<ChartLink> Retained, int Dropped) WriteWithLinks(
            IReadOnlyList<Note> notes, ISet<ChartLinkId> optional, ISet<ChartLinkId> released)
        {
            var selected = notes.ToDictionary(note => note.Id);
            var original = Notes.ToDictionary(note => note.Id);
            var links = ReadLinks();
            var retained = new List<ChartLink>();
            var dropped = new HashSet<ChartLinkId>();
            foreach (var link in links)
            {
                if (released.Contains(link.Id) || optional.Contains(link.Id) && link.AnchorIds.Any(id =>
                    !selected.TryGetValue(id, out var note) || note.Beat != original[id].Beat))
                {
                    dropped.Add(link.Id);
                    continue;
                }
                foreach (int anchor in link.AnchorIds)
                    if (!selected.TryGetValue(anchor, out var note) || note != original[anchor])
                        throw new InvalidDataException("A retained arc or chain lost an attached note or its original shape.");
                retained.Add(link);
            }
            var chart = WriteNotes(notes);
            foreach (string name in dropped.Select(id => id.Collection).Distinct())
                chart[name] = new JsonArray(Rows(source, name).Select((row, index) => (row, index))
                    .Where(item => !dropped.Contains(new ChartLinkId(name, item.index)))
                    .Select(item => item.row.DeepClone()).ToArray());
            return (chart, retained, dropped.Count);
        }

        private static bool Matches(Note note, ChartEndpoint point) =>
            Math.Abs(note.Beat - point.Beat) <= .0001 && note.Hand == point.Hand && note.X == point.X && note.Y == point.Y;
    }
}
