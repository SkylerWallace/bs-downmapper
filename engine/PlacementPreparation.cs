using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace BsDownmap.Engine
{
    internal sealed record PlacementNoteCue(PlacementCue Cue, CellRepair Repair = CellRepair.None,
        bool FlattenOutward = false, bool PreservePosition = false, bool RetainOpeningDot = false,
        ISet<(double X, double Y)> AllowedCells = null, double? ReachLimit = null);

    internal static class PlacementPreparation
    {
        private static readonly Dictionary<string, double> Defaults = new()
        {
            ["height"] = .35, ["width"] = .75, ["crossover"] = .55, ["sparseSectionAllowance"] = 0,
            ["sideWallRecoverySeconds"] = 0, ["ownSideOutward"] = 0, ["bottomRowDownstrokes"] = 0,
            ["bottomRowOuterInward"] = 0, ["crouchMiddleRecoverySeconds"] = 0,
            ["bombSwingClearanceMeters"] = .6, ["oppositeInlineSeconds"] = 0, ["sameHandInlineSeconds"] = 0,
            ["topRowSpacingSeconds"] = .55, ["topPalmSpacingSeconds"] = 1, ["topDotSpacingSeconds"] = .9,
            ["crouchRecoverySeconds"] = .85, ["centerVisionWindowSeconds"] = .75,
            ["noteBombHeadClearanceMeters"] = .45, ["reachSlackMeters"] = .2, ["sourcePositionCost"] = 1,
            ["recoveryResetSeconds"] = .7, ["recoveryResetCost"] = 6, ["resetCueMinimumCrouchSeconds"] = .2,
        };

        internal static PlacementPlan Build(
            IReadOnlyList<Note> notes, IReadOnlyDictionary<int, Note> source,
            IReadOnlyList<Wall> walls, IReadOnlyList<Bomb> bombs, ISet<int> linked,
            IReadOnlyList<ChartEndpoint> endpoints, IReadOnlyDictionary<int, PlacementNoteCue> cues,
            IReadOnlyList<DensitySection> sections, ChartClock clock, double bpm, double njs, double approach,
            JsonObject profile, IReadOnlyList<double> repairCenters)
        {
            if (notes.Count == 0 || njs <= 0 || approach <= 0)
                throw new InvalidDataException("Placement needs a rhythm and valid jump settings.");
            var controls = new Dictionary<string, double>(Defaults);
            if (profile["geometry"] is JsonObject geometry)
                foreach (var (key, value) in geometry)
                {
                    double number = value is JsonValue flag && flag.TryGetValue<bool>(out bool enabled) ?
                        (enabled ? 1 : 0) : StandardSong.Number(value);
                    if (!double.IsFinite(number) || number < 0)
                        throw new InvalidDataException($"Invalid geometry control {key}.");
                    controls[key] = number;
                }
            foreach (string key in new[] { "height", "width", "crossover", "sparseSectionAllowance" })
                if (controls[key] > 1)
                    throw new InvalidDataException($"Geometry control {key} must lie in [0, 1].");

            var groups = NestedTimings.Group(notes);
            var gaps = new Dictionary<int, double>();
            foreach (int hand in new[] { 0, 1 })
            {
                var swings = NestedTimings.Group(notes.Where(note => note.Hand == hand));
                for (int i = 0; i < swings.Count; i++)
                {
                    var (before, after) = Spacing(swings, i, clock);
                    foreach (var note in swings[i].Notes)
                        gaps.Add(note.Id, Math.Min(before, after));
                }
            }
            var windows = PlacementPolicy.RepairWindows(notes, repairCenters, clock.Gap);
            var domains = new Dictionary<int, CellDomain>();
            var attachments = endpoints.Select(point => new LinkedEndpoint(point.Beat, point.Hand, point.X, point.Y)).ToArray();
            var ramp = profile["difficultyRamp"];
            bool rampEnabled = Flag(ramp, "enabled");
            double rampStart = Read(ramp, "startFraction", .75), rampAmount = Read(ramp, "amount", .25);
            if (rampStart < 0 || rampStart >= 1 || rampAmount < 0 || rampAmount > 1)
                throw new InvalidDataException("Invalid difficulty ramp.");
            double firstBeat = groups[0].Notes[0].Beat, lastBeat = groups[^1].Notes[0].Beat;
            double duration = clock.Gap(firstBeat, lastBeat);
            double repairScale = 1 + Math.Max(0, .8 - Read(profile, "minimumSourceNoteRatio", .8));
            double window = Math.Max(approach, Math.Max(controls["bombSwingClearanceMeters"] / njs,
                Math.Max(controls["noteBombHeadClearanceMeters"] / njs, .375 * 60 / bpm))) + 1e-9;

            for (int i = 0; i < groups.Count; i++)
            {
                var (before, after) = Spacing(groups, i, clock);
                foreach (var note in groups[i].Notes)
                {
                    double beat = note.Beat, time = clock.Seconds(beat);
                    double low = Math.Min(time - window, clock.Seconds(beat - .0001)) - 1e-9;
                    double high = Math.Max(time + window, clock.Seconds(beat + .0001)) + 1e-9;
                    var nearby = bombs.Where(bomb => { double t = clock.Seconds(bomb.Beat); return low <= t && t <= high; }).ToArray();
                    bool clear = !walls.Any(wall => clock.Gap(beat, wall.Beat) <= .5 &&
                        clock.Gap(wall.Beat + wall.Duration, beat) <= .5);
                    double sparse = 0;
                    if (clear && Math.Min(before, after) >= .35 && gaps[note.Id] >= .8 &&
                        sections.Any(section => section.Start <= beat && beat < section.End && section.NotesPerSecond <= 1.7))
                        sparse = controls["sparseSectionAllowance"];
                    double progress = duration > 0 ? clock.Gap(firstBeat, beat) / duration : 0;
                    double relaxation = rampEnabled ? 1 - rampAmount * Math.Max(0, (progress - rampStart) / (1 - rampStart)) : 1;
                    var context = new CellContext(new PlacementContext(controls, gaps[note.Id], relaxation, sparse),
                        walls, nearby, clock.Gap, after, njs, MotionVisibility.CellMeters);
                    var cue = cues.GetValueOrDefault(note.Id);
                    domains.Add(note.Id, new CellDomain(context, bpm, MotionVisibility.RequiredClearLead,
                        cue?.ReachLimit ?? MotionVisibility.Reach(source[note.Id]), linked.Contains(note.Id),
                        windows.ContainsKey(note.Id), repairScale, cue?.Repair ?? CellRepair.None, cue?.FlattenOutward ?? false,
                        cue?.PreservePosition ?? false, cue?.RetainOpeningDot ?? false, cue?.AllowedCells, attachments));
                }
            }

            return new PlacementPlan(source, domains, cues.Where(pair => pair.Value.Cue != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Cue), windows, walls, clock.Seconds,
                note => MotionVisibility.BombVisibilityCost(note, domains[note.Id].Context.NearbyBombs, clock, approach, njs),
                approach, njs, controls.GetValueOrDefault("compactPairedCells") != 0, Flag(profile, "repairEmptyGeometryDomains"),
                Flag(profile, "comfortableDoubles"), Read(profile, "doubleWindupClearanceWeight", 0));
        }

        private static (double Before, double After) Spacing(IReadOnlyList<NestedTimings.BeatGroup> groups, int i, ChartClock clock)
        {
            double beat = groups[i].Notes[0].Beat;
            return (i > 0 ? clock.Gap(groups[i - 1].Notes[0].Beat, beat) : double.PositiveInfinity,
                i + 1 < groups.Count ? clock.Gap(beat, groups[i + 1].Notes[0].Beat) : double.PositiveInfinity);
        }

        private static bool Flag(JsonNode row, string key) =>
            row?[key] is JsonValue value && value.TryGetValue<bool>(out bool enabled) && enabled;

        private static double Read(JsonNode row, string key, double fallback)
        {
            if (row?[key] == null)
                return fallback;
            double value = StandardSong.Number(row[key]);
            return double.IsFinite(value) ? value : throw new InvalidDataException($"Invalid profile field {key}.");
        }
    }
}
