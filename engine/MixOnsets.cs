using System;

namespace BsDownmap.Engine
{
    /// <summary>Measures the prominence of existing hits in a mono 22,050 Hz mix.</summary>
    public static class MixOnsets
    {
        public const int SampleRate = 22050;
        private const int Hop = 128;
        private const int FftSize = 1024;
        private const int MelBands = 64;

        public static double[] Score(float[] samples, double[] seconds)
        {
            if (seconds.Length == 0) return Array.Empty<double>();
            for (int i = 0; i < seconds.Length; i++)
                if (double.IsNaN(seconds[i]) || double.IsInfinity(seconds[i]) || (i > 0 && seconds[i] < seconds[i - 1]))
                    throw new ArgumentException("Candidate times must be finite and sorted.", nameof(seconds));

            int frames = samples.Length / Hop + 1;
            var rms = new float[frames];
            var flux = new float[frames, 3];
            var spectrum = new Spectrum();
            var decibels = new float[frames, MelBands];
            float peak = float.NegativeInfinity;
            for (int frame = 0; frame < frames; frame++)
                peak = Math.Max(peak, spectrum.Analyze(samples, frame * Hop, decibels, frame, out rms[frame]));

            for (int frame = 1; frame < frames; frame++)
            {
                for (int band = 0; band < MelBands; band++)
                {
                    float current = Decibels(frame, band);
                    float previous = Decibels(frame - 1, band);
                    // Restrict the three-bin maximum to this frequency group.
                    int group = spectrum.Groups[band];
                    if (band > 0 && spectrum.Groups[band - 1] == group)
                        previous = Math.Max(previous, Decibels(frame - 1, band - 1));
                    if (band + 1 < MelBands && spectrum.Groups[band + 1] == group)
                        previous = Math.Max(previous, Decibels(frame - 1, band + 1));
                    flux[frame, group] += Math.Max(0, current - previous);
                }
                for (int group = 0; group < 3; group++)
                    flux[frame, group] /= spectrum.GroupSizes[group];
            }

            float Decibels(int frame, int band) => Math.Max(decibels[frame, band], peak - 80);

            var frameTimes = new double[frames];
            for (int frame = 0; frame < frames; frame++)
                frameTimes[frame] = (double)frame * Hop / SampleRate;
            var features = new double[6][];
            for (int k = 0; k < features.Length; k++)
                features[k] = new double[seconds.Length];
            for (int i = 0; i < seconds.Length; i++)
            {
                double time = seconds[i];
                int first = LowerBound(frameTimes, time - .030);
                int end = LowerBound(frameTimes, time + .040);
                if (first == end) continue;
                float localPeak = 0;
                for (int frame = first; frame < end; frame++)
                {
                    double distance = (frameTimes[frame] - time) / .026;
                    double proximity = Math.Exp(-distance * distance);
                    for (int band = 0; band < 3; band++)
                        features[band][i] = Math.Max(features[band][i], flux[frame, band] * proximity);
                    features[3][i] = Math.Max(features[3][i], Math.Max(0, rms[frame] - rms[Math.Max(0, frame - 2)]) * proximity);
                    localPeak = Math.Max(localPeak, rms[frame]);
                }
                int before = LowerBound(frameTimes, time - .080);
                float previous = localPeak;
                if (before < first)
                {
                    var preceding = new float[first - before];
                    Array.Copy(rms, before, preceding, 0, preceding.Length);
                    Array.Sort(preceding);
                    previous = (preceding[preceding.Length / 2] + preceding[(preceding.Length - 1) / 2]) / 2;
                }
                features[4][i] = Math.Max(0, (double)localPeak - previous);
                features[5][i] = localPeak;
            }
            return Normalize(features, seconds);
        }

        private static double[] Normalize(double[][] features, double[] seconds)
        {
            var floors = new double[features.Length];
            for (int k = 0; k < floors.Length; k++)
                floors[k] = Math.Max(.25 * Percentile(features[k], 0, seconds.Length, .95), 1e-8);
            var result = new double[seconds.Length];
            var scaled = new double[features.Length];
            for (int i = 0; i < result.Length; i++)
            {
                int start = LowerBound(seconds, seconds[i] - 4);
                int end = LowerBound(seconds, seconds[i] + 4);
                for (int k = 0; k < features.Length; k++)
                    scaled[k] = Math.Min(1, features[k][i] / Math.Max(floors[k], Percentile(features[k], start, end, .90)));
                double attack = .7 * Math.Max(scaled[0], Math.Max(scaled[1], scaled[2])) +
                    .3 * (scaled[0] + scaled[1] + scaled[2]) / 3;
                result[i] = .60 * attack + .20 * scaled[3] + .15 * scaled[4] + .05 * scaled[5];
            }
            return result;
        }

        private static double Percentile(double[] values, int start, int end, double quantile)
        {
            var sorted = new double[end - start];
            Array.Copy(values, start, sorted, 0, sorted.Length);
            Array.Sort(sorted);
            double position = (sorted.Length - 1) * quantile;
            int low = (int)position, high = Math.Min(low + 1, sorted.Length - 1);
            return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
        }

        private static int LowerBound(double[] values, double target)
        {
            int low = 0, high = values.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (values[middle] < target) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private sealed class Spectrum
        {
            internal readonly int[] Groups = new int[MelBands];
            internal readonly int[] GroupSizes = new int[3];
            private readonly double[] window = new double[FftSize];
            private readonly double[] real = new double[FftSize], imaginary = new double[FftSize];
            private readonly double[] cosine = new double[FftSize / 2], sine = new double[FftSize / 2];
            private readonly int[] reversed = new int[FftSize];
            private readonly float[] power = new float[FftSize / 2 + 1];
            private readonly int[] firstBin = new int[MelBands];
            private readonly float[][] weights = new float[MelBands][];

            internal Spectrum()
            {
                for (int i = 0; i < FftSize; i++)
                {
                    window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / FftSize);
                    if (i > 0) reversed[i] = (reversed[i >> 1] >> 1) | ((i & 1) * (FftSize / 2));
                    if (i < cosine.Length)
                    {
                        cosine[i] = Math.Cos(2 * Math.PI * i / FftSize);
                        sine[i] = -Math.Sin(2 * Math.PI * i / FftSize);
                    }
                }
                var edges = new double[MelBands + 2];
                double step = 200.0 / 3, logStep = Math.Log(6.4) / 27;
                for (int i = 0; i < edges.Length; i++)
                {
                    double start = 35 / step;
                    double scale = start + (15 + Math.Log(10) / logStep - start) * i / (edges.Length - 1);
                    edges[i] = scale < 15 ? scale * step : 1000 * Math.Exp(logStep * (scale - 15));
                }
                for (int band = 0; band < MelBands; band++)
                {
                    double low = edges[band], center = edges[band + 1], high = edges[band + 2];
                    int group = center < 250 ? 0 : center < 2000 ? 1 : 2;
                    Groups[band] = group;
                    GroupSizes[group]++;
                    int first = (int)Math.Ceiling(low * FftSize / SampleRate);
                    int last = (int)Math.Floor(high * FftSize / SampleRate);
                    firstBin[band] = first;
                    weights[band] = new float[Math.Max(0, last - first + 1)];
                    for (int i = 0; i < weights[band].Length; i++)
                    {
                        double frequency = (first + i) * (double)SampleRate / FftSize;
                        float triangle = (float)Math.Max(0, Math.Min((frequency - low) / (center - low), (high - frequency) / (high - center)));
                        weights[band][i] = (float)(triangle * (2 / (high - low)));
                    }
                }
            }

            internal float Analyze(float[] samples, int center, float[,] decibels, int frame, out float rms)
            {
                float energy = 0;
                int start = center - FftSize / 2;
                for (int i = 0; i < FftSize; i++)
                {
                    int index = start + i;
                    float sample = index >= 0 && index < samples.Length ? samples[index] : 0;
                    energy += sample * sample;
                    real[reversed[i]] = sample * window[i];
                    imaginary[reversed[i]] = 0;
                }
                rms = (float)Math.Sqrt(energy / FftSize);
                for (int length = 2; length <= FftSize; length *= 2)
                {
                    int half = length / 2, stride = FftSize / length;
                    for (int i = 0; i < FftSize; i += length)
                        for (int j = 0; j < half; j++)
                        {
                            int left = i + j, right = left + half, twiddle = j * stride;
                            double re = real[right] * cosine[twiddle] - imaginary[right] * sine[twiddle];
                            double im = real[right] * sine[twiddle] + imaginary[right] * cosine[twiddle];
                            real[right] = real[left] - re;
                            imaginary[right] = imaginary[left] - im;
                            real[left] += re;
                            imaginary[left] += im;
                        }
                }
                for (int i = 0; i < power.Length; i++)
                {
                    float re = (float)real[i], im = (float)imaginary[i];
                    float magnitude = (float)Math.Sqrt((double)re * re + (double)im * im);
                    power[i] = magnitude * magnitude;
                }
                float peak = float.NegativeInfinity;
                for (int band = 0; band < MelBands; band++)
                {
                    float value = 0;
                    for (int i = 0; i < weights[band].Length; i++)
                        value += weights[band][i] * power[firstBin[band] + i];
                    float db = 10 * (float)Math.Log10(Math.Max(1e-10f, value));
                    decibels[frame, band] = db;
                    peak = Math.Max(peak, db);
                }
                return peak;
            }
        }
    }
}
