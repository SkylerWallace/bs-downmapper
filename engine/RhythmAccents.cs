using System;
using System.Collections.Generic;
using System.Linq;

namespace BsDownmap.Engine
{
    internal static class RhythmAccents
    {
        private const int Spaced = 1, Chord = 2, Handoff = 4;
        private static readonly int[] CueOffsets = { -16, -8, -4, -2, 2, 4, 8, 16 };

        internal static int PulseRank(double beat)
        {
            long half = (long)Math.Round(beat * 2);
            return Math.Abs(beat - half / 2.0) > .0625 ? 3 : half % 2 != 0 ? 2 : half % 4 != 0 ? 1 : 0;
        }

        internal static double[] CueWeights(double[] beats, int[] hands, int[] starts)
        {
            var cues = new (int Roles, double Before, double After)[beats.Length];
            var weights = new double[beats.Length];
            for (int section = 0; section + 1 < starts.Length; section++)
            {
                int start = starts[section], end = starts[section + 1], run = 0;
                for (int i = start; i < end; i++)
                {
                    double before = i > start ? beats[i] - beats[i - 1] : 2;
                    double after = i + 1 < end ? beats[i + 1] - beats[i] : 2;
                    int roles = 0;
                    if (Math.Min(before, after) >= .25 && Math.Max(before, after) >= .75 && Math.Abs(before - after) >= .25)
                        roles |= Spaced;
                    if (hands[i] == 3 && (i > start && hands[i - 1] != 3 || i + 1 < end && hands[i + 1] != 3))
                        roles |= Chord;
                    if (i > start && before <= 1 && hands[i] != 3 && hands[i - 1] != 3)
                    {
                        if (hands[i] != hands[i - 1] && run >= 3)
                            roles |= Handoff;
                        run = hands[i] == hands[i - 1] ? run + 1 : 1;
                    }
                    else
                        run = hands[i] == 3 ? 0 : 1;
                    cues[i] = (roles, Math.Min(before, 2), Math.Min(after, 2));
                }
            }
            for (int i = 0; i < beats.Length; i++)
            {
                var cue = cues[i];
                if (cue.Roles == 0)
                    continue;
                int support = 0;
                foreach (int offset in CueOffsets)
                {
                    double target = beats[i] + offset;
                    for (int j = MeterRepeats.Bound(beats, target - .025); j < beats.Length && beats[j] <= target + .025; j++)
                    {
                        var other = cues[j];
                        if ((cue.Roles & other.Roles) != 0 && Math.Abs(cue.Before - other.Before) <= .125 &&
                            Math.Abs(cue.After - other.After) <= .125)
                        {
                            support++;
                            break;
                        }
                    }
                }
                bool handoff = (cue.Roles & Handoff) != 0;
                if (support > 0 || handoff)
                    weights[i] = Math.Min(1, .28 * support + (handoff ? .35 : .18));
            }
            return weights;
        }

        internal static double[] AccentGrid(double[] beats, double[] times, int[] hands, int[] counts, int[] starts, double pulse)
        {
            var slots = beats.Select(beat => (int)Math.Round(beat * 48)).ToArray();
            var weights = new double[beats.Length];
            for (int section = 0; section + 1 < starts.Length; section++)
            {
                int first = starts[section], end = starts[section + 1];
                for (int i = first + 1; i < end - 1; i++)
                {
                    int left = Math.Clamp(MeterRepeats.Bound(beats, beats[i] - 16, first: first), first, i);
                    int right = Math.Clamp(MeterRepeats.Bound(beats, beats[i] + 16, first: i), i, end);
                    if (right - left < 3 || beats[right - 1] - beats[left] < 12)
                        continue;
                    double rate = (times[right - 1] - times[left]) / (beats[right - 1] - beats[left]);
                    var nearby = Enumerable.Range(left, right - left);
                    if (2 * rate >= .75 * pulse && 2 * rate <= 1.25 * pulse)
                    {
                        int phase = Dominant(nearby.Where(j => slots[j] % 12 != 0 && j + 1 < end &&
                            beats[j + 1] - beats[j] >= (hands[j] == 3 ? .75 : 1)).Select(j => Mod(slots[j], 192)), 4, .70);
                        if (phase >= 0)
                        {
                            int slot = Mod(slots[i], 192), companion = Mod((int)Math.Round(phase / 48.0 - 2) * 48, 192);
                            if (slot == companion || slot == phase && (hands[i] == 3 || beats[i + 1] - beats[i] >= 1))
                                weights[i] = 2.5;
                            else if (slot == Mod(companion + 96, 192))
                                weights[i] = 1;
                            continue;
                        }
                    }
                    if (3 * rate >= .8 * pulse && 3 * rate <= 1.45 * pulse)
                    {
                        int phase = Dominant(nearby.Where(j => hands[j] == 3 && slots[j] % 48 == 0)
                            .Select(j => Mod(slots[j] / 48, 3)), 4, .67);
                        if (phase >= 0)
                        {
                            if (Mod(slots[i], 144) == 48 * phase)
                                weights[i] = 2;
                            continue;
                        }
                    }
                    if (5 * rate >= 1.9 * pulse && 5 * rate <= 2.6 * pulse)
                    {
                        int phase = Dominant(nearby.Where(j => hands[j] == 3 && counts[j] >= 3)
                            .Select(j => Mod(slots[j], 240)), 3, .70);
                        if (phase >= 0)
                        {
                            int slot = Mod(slots[i], 240);
                            weights[i] = slot == phase ? 2.5 : slot == Mod(phase + 144, 240) ? 1.5 : 0;
                        }
                    }
                }
            }
            return weights;
        }

        internal static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;

        private static int Dominant(IEnumerable<int> phases, int minimum, double fraction)
        {
            var counts = new Dictionary<int, int>();
            foreach (int phase in phases)
                counts[phase] = counts.GetValueOrDefault(phase) + 1;
            var best = counts.OrderByDescending(pair => pair.Value).FirstOrDefault();
            return best.Value >= minimum && best.Value >= fraction * counts.Values.Sum() ? best.Key : -1;
        }
    }
}
