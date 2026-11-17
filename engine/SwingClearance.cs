using System;
using System.Collections.Generic;

namespace BsDownmap.Engine
{
    internal static class SwingClearance
    {
        internal readonly record struct Point(double X, double Y)
        {
            public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
            public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
            public static Point operator *(Point a, double scale) => new(a.X * scale, a.Y * scale);
            public double Length => Math.Sqrt(Dot(this, this));
        }

        internal readonly record struct Segment(Point Start, Point End);

        private static readonly Point[] Vectors =
        {
            new(0, 1), new(0, -1), new(-1, 0), new(1, 0),
            new(-1, 1), new(1, 1), new(-1, -1), new(1, -1)
        };

        private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y;
        private static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;

        internal static Segment Cut(Note note, double extent)
        {
            var vector = Vectors[(int)note.Direction];
            double angle = note.Angle * Math.PI / 180;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            var displacement = new Point(
                vector.X * cosine - vector.Y * sine,
                vector.X * sine + vector.Y * cosine) * (extent / vector.Length);
            var center = new Point(note.X, note.Y);
            return new Segment(center - displacement, center + displacement);
        }

        internal static double Distance(Point point, Segment segment)
        {
            var axis = segment.End - segment.Start;
            var relative = point - segment.Start;
            double lengthSquared = Dot(axis, axis);
            double projection = lengthSquared == 0 ? 0 : Math.Clamp(Dot(relative, axis) / lengthSquared, 0, 1);
            return (relative - axis * projection).Length;
        }

        internal static double Distance(Segment first, Segment second)
        {
            var firstAxis = first.End - first.Start;
            var secondAxis = second.End - second.Start;
            double denominator = Cross(firstAxis, secondAxis);
            if (Math.Abs(denominator) > 1e-9)
            {
                var offset = second.Start - first.Start;
                double firstPosition = Cross(offset, secondAxis) / denominator;
                double secondPosition = Cross(offset, firstAxis) / denominator;
                if (firstPosition is >= 0 and <= 1 && secondPosition is >= 0 and <= 1)
                    return 0;
            }

            return Math.Min(
                Math.Min(Distance(first.Start, second), Distance(first.End, second)),
                Math.Min(Distance(second.Start, first), Distance(second.End, first)));
        }

        internal static IEnumerable<string> GroupRules(Note first, Note second)
        {
            if (first.X == second.X && first.Y == second.Y)
                yield return "G03";
            if (first.Direction == 8 || second.Direction == 8)
            {
                yield return "G04";
                yield break;
            }

            var firstCut = Cut(first, 1.25);
            var secondCut = Cut(second, 1.25);
            if (first.Hand != second.Hand)
            {
                if (Distance(firstCut, secondCut) <= 0.2)
                    yield return "G01";
                yield break;
            }

            var firstAxis = firstCut.End - firstCut.Start;
            var secondAxis = secondCut.End - secondCut.Start;
            var offset = new Point(second.X - first.X, second.Y - first.Y);
            double normal = Math.Abs(Cross(offset, firstAxis)) / firstAxis.Length;
            double alignment = Dot(firstAxis, secondAxis) / (firstAxis.Length * secondAxis.Length);
            if (normal > 0.35 || alignment < Math.Cos(0.001 * Math.PI / 180))
                yield return "G02";
        }

        internal static Dictionary<int, double> BombClearances(
            Note note, IReadOnlyList<Bomb> bombs, Func<double, double> seconds, double bpm)
        {
            var result = new Dictionary<int, double>();
            if (note.Direction == 8)
                return result;
            var cut = Cut(note, 0.75);
            double timestamp = seconds(note.Beat);
            double window = 0.375 * 60 / bpm;
            for (int index = 0; index < bombs.Count; index++)
            {
                var bomb = bombs[index];
                if (Math.Abs(seconds(bomb.Beat) - timestamp) <= window)
                    result.Add(index, Distance(new Point(bomb.X, bomb.Y), cut));
            }
            return result;
        }

        internal static bool BlocksWallApproach(Note note, Wall wall)
        {
            if (note.Beat < wall.Beat || note.Beat >= wall.Beat + wall.Duration)
                return false;
            var center = new Point(note.X, note.Y);
            var start = note.Direction == 8 ? center : Cut(note, .75).Start;
            double enter = 0, exit = 1;
            return Clip(start.X, center.X - start.X, wall.X - .5, wall.X + wall.Width - .5) &&
                Clip(start.Y, center.Y - start.Y, wall.CollisionBottom - .5, wall.Y + wall.Height - .5);

            bool Clip(double origin, double delta, double low, double high)
            {
                if (Math.Abs(delta) < 1e-9)
                    return low <= origin && origin <= high;
                double first = (low - origin) / delta, last = (high - origin) / delta;
                enter = Math.Max(enter, Math.Min(first, last));
                exit = Math.Min(exit, Math.Max(first, last));
                return enter <= exit;
            }
        }

        internal static IEnumerable<int> WallConflicts(
            Note note, IReadOnlyList<Wall> walls, Func<double, double> seconds, double recoverySeconds)
        {
            for (int index = 0; index < walls.Count; index++)
            {
                var wall = walls[index];
                if (wall.Beat <= note.Beat &&
                    seconds(note.Beat) - seconds(wall.Beat + wall.Duration) < recoverySeconds &&
                    wall.X <= note.X && note.X < wall.X + wall.Width &&
                    wall.CollisionBottom <= note.Y && note.Y < wall.Y + wall.Height)
                    yield return index;
            }
        }
    }
}
