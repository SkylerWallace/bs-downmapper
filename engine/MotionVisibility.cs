using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal readonly record struct TimedNote(Note Note, double Time);
    internal readonly record struct TimedWall(Wall Wall, double Start, double End);

    internal static class MotionVisibility
    {
        internal const double CellMeters = .6;
        internal const double ArmReachMeters = .85;
        internal const double RequiredClearLead = .30;
        internal const double CutHalfDuration = .12;
        private const double ControllerRadius = .10;
        private const double ArmState = .75;
        private const double ArmRecovery = .45;
        private const double BottomRow = .8;
        private const double HitPlane = 1.3;
        private static readonly Vector Eye = new(0, 1.65, 0);

        private readonly record struct Vector(double X, double Y, double Z)
        {
            public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vector operator *(Vector a, double f) => new(a.X * f, a.Y * f, a.Z * f);
            public double Dot(Vector other) => X * other.X + Y * other.Y + Z * other.Z;
            public double Length => Math.Sqrt(Dot(this));
        }

        private readonly record struct GripPath(Vector Start, Vector End)
        {
            public Vector At(double fraction) => Start + (End - Start) * fraction;
        }

        private static Vector Shoulder(Note note) => new(note.Hand == 0 ? -.2 : .2, 1.45, 0);
        private static Vector Position(Note note, double depth) => new((note.X - 1.5) * CellMeters,
            BottomRow + note.Y * CellMeters, depth);

        private static GripPath? Trajectory(Note note)
        {
            if (note.Direction == 8)
                return null;
            var cut = SwingClearance.Cut(note, 1);
            var axis = cut.End - cut.Start;
            var displacement = new Vector(axis.X, axis.Y, 0) * .11;
            var center = Position(note, HitPlane);
            var shoulder = Shoulder(note);
            return new GripPath(Grip(center - displacement), Grip(center + displacement));

            Vector Grip(Vector tip)
            {
                var reach = tip - shoulder;
                double length = reach.Length;
                return shoulder + reach * (length == 0 ? 0 : Math.Max(0, (length - .95) / length));
            }
        }

        internal static double Reach(Note note)
        {
            var path = Trajectory(note);
            if (!path.HasValue)
                return 0;
            var shoulder = Shoulder(note);
            return Math.Max((path.Value.Start - shoulder).Length, (path.Value.End - shoulder).Length);
        }

        internal static bool ControllerOverlap(TimedNote first, TimedNote second)
        {
            double start = Math.Max(first.Time, second.Time) - CutHalfDuration;
            double end = Math.Min(first.Time, second.Time) + CutHalfDuration;
            if (start > end || Trajectory(first.Note) is not GripPath a || Trajectory(second.Note) is not GripPath b)
                return false;
            Vector initial = PositionAt(first, a, start) - PositionAt(second, b, start);
            Vector final = PositionAt(first, a, end) - PositionAt(second, b, end);
            Vector relative = final - initial;
            double lengthSquared = relative.Dot(relative);
            double closest = lengthSquared == 0 ? 0 : Math.Clamp(-initial.Dot(relative) / lengthSquared, 0, 1);
            return (initial + relative * closest).Length < 2 * ControllerRadius;

            static Vector PositionAt(TimedNote note, GripPath path, double time) =>
                path.At((time - note.Time + CutHalfDuration) / (2 * CutHalfDuration));
        }

        internal static bool BlocksLinkedSwing(TimedNote candidate, TimedNote anchor)
        {
            if (Math.Abs(candidate.Note.Beat - anchor.Note.Beat) <= .0001 &&
                SwingClearance.GroupRules(candidate.Note, anchor.Note).Any(rule => rule != "G04"))
                return true;
            return candidate.Note.Hand != anchor.Note.Hand && ControllerOverlap(candidate, anchor);
        }

        internal static bool ValidSequence(
            IReadOnlyList<TimedNote> prior, IReadOnlyList<Note> trial, double time, ISet<int> anchors)
        {
            double horizon = Math.Max(ArmState + ArmRecovery, 2 * CutHalfDuration);
            var context = prior.Where(note => time - note.Time <= horizon)
                .Concat(trial.Select(note => new TimedNote(note, time))).ToArray();
            var times = context.ToDictionary(note => note.Note.Id, note => note.Time);
            double targetBeat = trial.Min(note => note.Beat);
            var previous = new TimedNote?[2];
            (int Upper, double Time, bool UpperOnly)? crossing = null;
            foreach (var group in NestedTimings.Group(context.Select(item => item.Note)))
            {
                var notes = group.Notes;
                double now = times[notes[0].Id];
                int hands = notes.Aggregate(0, (mask, note) => mask | 1 << note.Hand);
                if (crossing is { } held && now - held.Time <= ArmRecovery && hands == 1 << (1 - held.Upper) &&
                    group.Beat >= targetBeat - .0001 && !held.UpperOnly)
                {
                    int lower = 1 - held.Upper;
                    var old = previous[lower].Value.Note;
                    var upper = previous[held.Upper].Value.Note;
                    foreach (var note in notes)
                    {
                        if (old.Direction == 8 || upper.Direction == 8 || note.Direction == 8 ||
                            anchors.Contains(old.Id) && anchors.Contains(upper.Id) && anchors.Contains(note.Id))
                            continue;
                        var move = new SwingClearance.Segment(GridPoint(old), GridPoint(note));
                        bool remainsCrossed = lower == 0 ? note.X > upper.X : note.X < upper.X;
                        if (remainsCrossed || SwingClearance.Distance(GridPoint(upper), move) <= .5)
                            return false;
                    }
                }

                foreach (var note in notes)
                    previous[note.Hand] = new TimedNote(note, times[note.Id]);
                if (previous[0] is { } left && previous[1] is { } right &&
                    now - Math.Min(left.Time, right.Time) <= ArmState &&
                    left.Note.X > right.Note.X && Math.Abs(left.Note.Y - right.Note.Y) >= .5)
                {
                    int upper = left.Note.Y > right.Note.Y ? 0 : 1;
                    crossing = (upper, now, hands == 1 << upper);
                }
                else
                    crossing = null;
            }

            for (int i = 0; i < context.Length; i++)
            for (int j = i + 1; j < context.Length; j++)
            {
                var a = context[i];
                var b = context[j];
                if (b.Time - a.Time > 2 * CutHalfDuration)
                    break;
                if (a.Note.Hand != b.Note.Hand && Math.Max(a.Note.Beat, b.Note.Beat) >= targetBeat - .0001 &&
                    !(anchors.Contains(a.Note.Id) && anchors.Contains(b.Note.Id)) && ControllerOverlap(a, b))
                    return false;
            }
            return true;
        }

        private static SwingClearance.Point GridPoint(Note note) => new(note.X, note.Y);

        internal static double BombVisibilityCost(Note note, IReadOnlyList<Bomb> bombs,
            ChartClock clock, double approach, double jumpSpeed)
        {
            const double half = .2;
            var box = new Vector(half, half, half);
            int hits = 0;
            foreach (var bomb in bombs)
            {
                double gap = clock.Gap(note.Beat, bomb.Beat);
                if (Math.Abs(gap) >= approach)
                    continue;
                for (int step = 1; step <= 8; step++)
                {
                    double lead = approach * step / 9, noteLead = lead - gap;
                    if (noteLead <= 0 || noteLead >= approach)
                        continue;
                    var target = new Vector((bomb.X - 1.5) * CellMeters, BottomRow + bomb.Y * CellMeters,
                        HitPlane + jumpSpeed * lead);
                    var center = Position(note, HitPlane + jumpSpeed * noteLead);
                    if (RayBox(target, center - box, center + box))
                        hits++;
                }
            }
            return .5 * hits;
        }

        internal static IEnumerable<int> LateVisibility(
            TimedNote target, IReadOnlyList<TimedNote> prior, IReadOnlyList<TimedWall> walls,
            double jumpSpeed, double approachSeconds)
        {
            const int samples = 20;
            const double half = .2;
            double start = target.Time - approachSeconds;
            double end = target.Time - .10;
            if (end <= start)
                yield break;
            var blockers = prior.Where(note => note.Time >= start && Math.Abs(note.Time - target.Time) >= 1e-5)
                .ToArray();
            double step = (end - start) / samples;
            var hits = new Dictionary<int, (int Count, double Last)>();
            for (int index = 0; index < samples; index++)
            {
                double time = start + (index + .5) * step;
                var position = Position(target.Note, HitPlane + jumpSpeed * (target.Time - time));
                foreach (var note in blockers)
                {
                    if (time < note.Time - approachSeconds || time >= note.Time)
                        continue;
                    var center = Position(note.Note, HitPlane + jumpSpeed * (note.Time - time));
                    var box = new Vector(half, half, half);
                    if (RayBox(position, center - box, center + box))
                        Record(note.Note.Id, time);
                }
                for (int wallIndex = 0; wallIndex < walls.Count; wallIndex++)
                {
                    var timed = walls[wallIndex];
                    if (time > timed.End || time < timed.Start - approachSeconds)
                        continue;
                    var wall = timed.Wall;
                    var low = new Vector((wall.X - 2) * CellMeters, BottomRow + (wall.Y - .5) * CellMeters,
                        Math.Max(.01, HitPlane + jumpSpeed * (timed.Start - time)));
                    var high = new Vector((wall.X + wall.Width - 2) * CellMeters,
                        BottomRow + (wall.Y + wall.Height - .5) * CellMeters,
                        HitPlane + jumpSpeed * (timed.End - time));
                    if (high.Z > 0 && RayBox(position, low, high))
                        Record(-1 - wallIndex, time);
                }
            }

            foreach (var (id, hit) in hits)
                if (hit.Count * step >= .12 && Math.Max(0, target.Time - (hit.Last + step / 2)) < RequiredClearLead)
                    yield return id;

            void Record(int id, double time) => hits[id] = (hits.GetValueOrDefault(id).Count + 1, time);
        }

        private static bool RayBox(Vector target, Vector low, Vector high)
        {
            double near = 0, far = 1;
            return Intersect(Eye.X, target.X, low.X, high.X) &&
                Intersect(Eye.Y, target.Y, low.Y, high.Y) &&
                Intersect(Eye.Z, target.Z, low.Z, high.Z) && near < 1 - 1e-6 && far > 0;

            bool Intersect(double origin, double destination, double lower, double upper)
            {
                double direction = destination - origin;
                if (Math.Abs(direction) < 1e-9)
                    return lower <= origin && origin <= upper;
                double a = (lower - origin) / direction;
                double b = (upper - origin) / direction;
                near = Math.Max(near, Math.Min(a, b));
                far = Math.Min(far, Math.Max(a, b));
                return near <= far;
            }
        }
    }
}
