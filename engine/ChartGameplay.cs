using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    public readonly record struct Note(int Id, double Beat, int Hand, double X, double Y, double Direction, double Angle);
    public readonly record struct Bomb(double Beat, double X, double Y);
    public readonly record struct Wall(double Beat, double X, double Y, double Duration, double Width, double Height,
        bool LegacyCrouch = false)
    {
        internal double CollisionBottom => LegacyCrouch ? 1 : Y;
    }

    public sealed partial class ChartGameplay
    {
        private readonly JsonObject source;
        private readonly int version;
        public IReadOnlyList<Note> Notes { get; }
        public IReadOnlyList<Bomb> Bombs { get; }
        public IReadOnlyList<Wall> Walls { get; }

        private ChartGameplay(JsonObject source, int version, List<Note> notes, List<Bomb> bombs, List<Wall> walls)
        {
            this.source = source;
            this.version = version;
            Notes = notes.OrderBy(note => note.Beat).ThenBy(note => note.Hand)
                .ThenBy(note => note.X).ThenBy(note => note.Y).ToArray();
            Bombs = bombs.AsReadOnly();
            Walls = walls.AsReadOnly();
        }

        public static ChartGameplay Read(JsonObject data)
        {
            string versionText = (data["version"] ?? data["_version"])?.GetValue<string>() ?? "";
            if (versionText.Length < 2 || versionText[1] != '.' || versionText[0] < '2' || versionText[0] > '4')
                throw new InvalidDataException($"Unsupported beatmap version {versionText}.");
            int version = versionText[0] - '0';
            if (Present(data["spawnRotations"]) || Present(data["rotationEvents"]) || Present(data["_waypoints"]) ||
                Rows(data, "_events").Any(row => Number(row, "_type") is 14 or 15 ||
                    version != 2 && Number(row, "_type") == 100))
                throw new InvalidDataException("Rotation events and waypoints are unsupported.");
            if (version != 2 && (Present(data["_BPMChanges"]) || Present((data["_customData"] as JsonObject)?["_BPMChanges"])))
                throw new InvalidDataException("Legacy BPM metadata requires a version 2 chart.");

            var notes = new List<Note>();
            var bombs = new List<Bomb>();
            var walls = new List<Wall>();
            if (version == 2)
            {
                int id = 0;
                foreach (var row in Rows(data, "_notes"))
                {
                    RejectCustom(row, "_customData");
                    double type = Number(row, "_type");
                    double beat = Number(row, "_time"), x = Number(row, "_lineIndex"), y = Number(row, "_lineLayer");
                    if (type == 3) bombs.Add(new Bomb(beat, x, y));
                    else if (type is 0 or 1)
                        notes.Add(new Note(id, beat, (int)type, x, y, Number(row, "_cutDirection"), 0));
                    else throw new InvalidDataException("Unknown note type.");
                    id++;
                }
                foreach (var row in Rows(data, "_obstacles"))
                {
                    RejectCustom(row, "_customData");
                    double type = Number(row, "_type");
                    if (type is not (0 or 1)) throw new InvalidDataException("Unsupported obstacle geometry.");
                    walls.Add(new Wall(Number(row, "_time"), Number(row, "_lineIndex"), type == 0 ? 0 : 2,
                        Number(row, "_duration"), Number(row, "_width"), type == 0 ? 5 : 3, LegacyCrouch: type == 1));
                }
            }
            else
            {
                if (version == 4)
                {
                    foreach (var row in Rows(data, "colorNotesData")) RejectCustom(row, "customData");
                    if (Rows(data, "chains").Any(row => Number(row, "hr") != 0 || Number(row, "tr") != 0))
                        throw new InvalidDataException("Object rotation lanes are unsupported.");
                }
                foreach (var row in Rows(data, "colorNotes"))
                {
                    RejectCustom(row, "customData");
                    var fields = Fields(data, row, version, "colorNotesData");
                    double hand = Number(fields, "c");
                    if (hand is not (0 or 1)) throw new InvalidDataException("Unsupported note hand.");
                    notes.Add(new Note(notes.Count, Number(row, "b"), (int)hand, Number(fields, "x"),
                        Number(fields, "y"), Number(fields, "d"), Number(fields, "a")));
                }
                foreach (var row in Rows(data, "bombNotes"))
                {
                    var fields = Fields(data, row, version, "bombNotesData");
                    bombs.Add(new Bomb(Number(row, "b"), Number(fields, "x"), Number(fields, "y")));
                }
                foreach (var row in Rows(data, "obstacles"))
                {
                    var fields = Fields(data, row, version, "obstaclesData");
                    walls.Add(new Wall(Number(row, "b"), Number(fields, "x"), Number(fields, "y"),
                        Number(fields, "d"), Number(fields, "w"), Number(fields, "h")));
                }
            }
            if (notes.Count == 0) throw new InvalidDataException("No colored notes.");
            if (notes.Any(note => note.X < 0 || note.X > 3 || note.Y < 0 || note.Y > 2 ||
                note.Direction < 0 || note.Direction > 8))
                throw new InvalidDataException("Nonstandard note grid or direction.");
            return new ChartGameplay((JsonObject)data.DeepClone(), version, notes, bombs, walls);
        }

        public JsonObject WriteNotes(IEnumerable<Note> notes)
        {
            var output = (JsonObject)source.DeepClone();
            if (version == 2)
            {
                var entries = Rows(source, "_notes").Where(row => Number(row, "_type") == 3)
                    .Select(row => (JsonObject)row.DeepClone())
                    .Concat(notes.Select(note => new JsonObject
                    {
                        ["_time"] = note.Beat, ["_lineIndex"] = note.X, ["_lineLayer"] = note.Y,
                        ["_type"] = note.Hand, ["_cutDirection"] = note.Direction,
                    }));
                output["_notes"] = new JsonArray(entries.OrderBy(row => Number(row, "_time"))
                    .ThenBy(row => Number(row, "_type")).ToArray());
                return output;
            }

            var events = new JsonArray();
            if (version == 3)
            {
                foreach (var note in notes)
                {
                    var fields = WriteFields(note);
                    fields["b"] = note.Beat;
                    events.Add(fields);
                }
            }
            else
            {
                var table = (JsonArray)output["colorNotesData"];
                var original = (JsonArray)source["colorNotes"];
                var indices = new Dictionary<(double, double, double, double, double), int>();
                for (int i = 0; i < table.Count; i++) indices[Signature((JsonObject)table[i])] = i;
                foreach (var note in notes)
                {
                    var signature = (note.X, note.Y, (double)note.Hand, note.Direction, note.Angle);
                    int index = note.Id >= 0 && note.Id < original.Count
                        ? Index((JsonObject)original[note.Id], table.Count) : -1;
                    if ((index < 0 || signature != Signature((JsonObject)table[index])) && !indices.TryGetValue(signature, out index))
                    {
                        index = table.Count;
                        indices[signature] = index;
                        table.Add(WriteFields(note));
                    }
                    events.Add(new JsonObject { ["b"] = note.Beat, ["i"] = index });
                }
            }
            output["colorNotes"] = events;
            return output;
        }

        private static JsonObject WriteFields(Note note) => new JsonObject
        {
            ["x"] = note.X, ["y"] = note.Y, ["c"] = note.Hand, ["d"] = note.Direction, ["a"] = note.Angle,
        };

        private static (double, double, double, double, double) Signature(JsonObject row) =>
            (Number(row, "x"), Number(row, "y"), Number(row, "c"), Number(row, "d"), Number(row, "a"));

        private static JsonObject Fields(JsonObject data, JsonObject row, int version, string tableName)
        {
            if (version == 3) return row;
            if (Number(row, "r") != 0) throw new InvalidDataException("Object rotation lanes are unsupported.");
            var table = data[tableName] as JsonArray ?? throw new InvalidDataException($"Missing {tableName}.");
            return (JsonObject)table[Index(row, table.Count)];
        }

        private static int Index(JsonObject row, int count)
        {
            double index = Number(row, "i");
            if (index < 0 || index >= count || index != Math.Truncate(index))
                throw new InvalidDataException("Beatmap data index is out of range.");
            return (int)index;
        }

        private static IEnumerable<JsonObject> Rows(JsonObject data, string name) =>
            data[name] is JsonArray array ? array.Cast<JsonObject>() : Enumerable.Empty<JsonObject>();

        private static double Number(JsonObject data, string name)
        {
            if (data[name] == null) return 0;
            if (data[name] is JsonValue number)
            {
                if (number.TryGetValue<double>(out double value) && double.IsFinite(value)) return value;
                if (number.TryGetValue<int>(out int integer)) return integer;
                if (number.TryGetValue<long>(out long wide)) return wide;
            }
            throw new InvalidDataException($"Invalid numeric beatmap field {name}.");
        }

        private static bool Present(JsonNode value) => value switch
        {
            null => false,
            JsonArray array => array.Count != 0,
            JsonObject obj => obj.Count != 0,
            _ => true,
        };

        private static void RejectCustom(JsonObject row, string key)
        {
            if (Present(row[key])) throw new InvalidDataException("Per-object custom data is unsupported.");
        }
    }
}
