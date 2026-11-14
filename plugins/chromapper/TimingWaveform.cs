using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BsDownmap.ChroMapper
{
    // Like Tempo Mapper, keep multiresolution min/max peaks, using CPU bins so the plugin
    // can render with the standard Unity UI material on ChroMapper builds without its shaders.
    internal sealed class TimingWaveform
    {
        private const int MaxBaseBins = 1 << 20;
        private const int FramesPerRead = 131072;
        private readonly List<Vector2[]> levels = new List<Vector2[]>();
        private int framesPerBin, sampleRate, sampleCount;
        internal bool Available => levels.Count > 0;

        internal static IEnumerator Generate(AudioClip clip, Action<TimingWaveform> completed)
        {
            var waveform = new TimingWaveform
            {
                framesPerBin = Math.Max(32, (int)Math.Ceiling(clip.samples / (double)MaxBaseBins)),
                sampleRate = clip.frequency,
                sampleCount = clip.samples
            };
            var binSize = waveform.framesPerBin;
            var bins = new Vector2[(int)Math.Ceiling(clip.samples / (double)binSize)];
            var channels = clip.channels;
            var frames = Math.Min(clip.samples, Math.Max(1, FramesPerRead / binSize) * binSize);
            var buffer = new float[frames * channels];
            for (int offset = 0; offset < clip.samples; offset += frames)
            {
                // GetData wraps at the end of the clip. Exclude the wrapped part of the final read.
                if (!clip.GetData(buffer, offset))
                {
                    completed(waveform);
                    yield break;
                }
                int valid = Math.Min(frames, clip.samples - offset);
                for (int first = 0; first < valid; first += binSize)
                {
                    var peak = new Vector2(1, -1);
                    int end = Math.Min(valid, first + binSize) * channels;
                    for (int i = first * channels; i < end; i++)
                    {
                        peak.x = Math.Min(peak.x, buffer[i]);
                        peak.y = Math.Max(peak.y, buffer[i]);
                    }
                    bins[(offset + first) / binSize] = peak;
                }
                yield return null;
            }
            waveform.levels.Add(bins);
            while (bins.Length > 1)
            {
                var next = new Vector2[(bins.Length + 1) / 2];
                for (int i = 0; i < next.Length; i++)
                {
                    var a = bins[2 * i];
                    var b = bins[Math.Min(2 * i + 1, bins.Length - 1)];
                    next[i] = new Vector2(Math.Min(a.x, b.x), Math.Max(a.y, b.y));
                }
                waveform.levels.Add(next);
                bins = next;
            }
            completed(waveform);
        }

        internal Vector2 Peaks(double fromSeconds, double toSeconds)
        {
            double firstFrame = Math.Max(0, fromSeconds * sampleRate);
            double lastFrame = Math.Min(sampleCount, toSeconds * sampleRate);
            if (firstFrame >= lastFrame) return Vector2.zero;
            int first = (int)(firstFrame / framesPerBin);
            int end = (int)Math.Ceiling(lastFrame / framesPerBin);
            var peak = new Vector2(1, -1);
            // Decompose the visible range into whole cached bins of the largest aligned sizes.
            for (int level = 0; first < end; level++, first >>= 1, end >>= 1)
            {
                var bins = levels[level];
                if ((first & 1) != 0) Include(bins[first++]);
                if ((end & 1) != 0) Include(bins[--end]);
            }
            return peak;

            void Include(Vector2 bin)
            {
                peak.x = Math.Min(peak.x, bin.x);
                peak.y = Math.Max(peak.y, bin.y);
            }
        }
    }
}
