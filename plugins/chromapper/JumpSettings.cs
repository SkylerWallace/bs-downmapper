using System;
using SimpleJSON;

namespace BsDownmap.ChroMapper
{
    internal static class JumpSettings
    {
        internal static JSONObject FromSongInfo(string difficulty, JSONNode info)
        {
            var reference = StandardChart.Entry(info, "ExpertPlus");
            var speed = reference == null ? null : reference.HasKey("noteJumpMovementSpeed")
                ? reference["noteJumpMovementSpeed"] : reference["_noteJumpMovementSpeed"];
            return Calculate(difficulty, StandardChart.Bpm(info), speed != null && speed.IsNumber ? speed.AsDouble : double.NaN);
        }

        internal static JSONObject Calculate(string difficulty, double bpm, double expertPlusNjs)
        {
            if (!Finite(bpm) || bpm <= 0)
                throw new ArgumentException("Positive finite BPM and NJS required.");
            double njs;
            switch (difficulty)
            {
                case "Easy":
                case "Normal": njs = 10; break;
                case "Hard": njs = 12; break;
                case "Expert": njs = 14; break;
                case "ExpertPlus": njs = 16; break;
                default: throw new ArgumentException($"Unknown Standard difficulty {difficulty}.");
            }
            if (Finite(expertPlusNjs) && expertPlusNjs >= 10)
            {
                if (difficulty == "ExpertPlus")
                    njs = expertPlusNjs;
                else if (difficulty == "Hard" || difficulty == "Expert")
                {
                    int share = difficulty == "Hard" ? 1 : 2;
                    double speed = 10 + (expertPlusNjs - 10) / 3 * share;
                    njs = Math.Floor(2 * speed) / 2;
                }
            }
            if (!Finite(njs)) throw new ArgumentException("Positive finite BPM and NJS required.");
            double secondsPerBeat = 60 / bpm;
            double baseDuration = 4;
            while (baseDuration > 1 && njs * secondsPerBeat * baseDuration > 17.999)
                baseDuration /= 2;
            double distancePerQuarter = .5 * njs * secondsPerBeat;
            if (!Finite(distancePerQuarter) || distancePerQuarter <= 0)
                throw new ArgumentException("Cannot calculate jump distance for this BPM and NJS.");
            double minimum = Math.Max(1, Math.Ceiling(20 / distancePerQuarter - 1e-9));
            double maximum = Math.Floor(32 / distancePerQuarter + 1e-9);
            if (minimum > maximum)
                throw new ArgumentException("No quarter-beat offset gives a jump distance of 20–32 m for this BPM and NJS.");
            double midpoint = difficulty == "Expert" || difficulty == "ExpertPlus" ? 25 : 27;
            double quarters = Math.Min(maximum, Math.Max(minimum, Math.Round(midpoint / distancePerQuarter)));
            return new JSONObject
            {
                ["njs"] = njs,
                ["offset"] = quarters / 4 - baseDuration,
                ["halfJumpDuration"] = quarters / 4,
                ["jumpDistance"] = quarters * distancePerQuarter
            };
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
