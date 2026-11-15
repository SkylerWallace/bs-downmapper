using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BsDownmap.Engine
{
    /// <summary>Decodes the song mix and measures onset evidence at existing chart beats.</summary>
    public sealed class MixAudio : IDisposable
    {
        private IntPtr library;
        private readonly OpenFile open;
        private readonly ReadFrames read;
        private readonly CloseFile close;

        public MixAudio(string decoderPath)
        {
            library = NativeLibrary.Load(Path.GetFullPath(decoderPath));
            try
            {
                if (Path.DirectorySeparatorChar == '\\')
                    open = Export<OpenWideFile>("sf_wchar_open").Invoke;
                else
                    open = Export<OpenUtf8File>("sf_open").Invoke;
                read = Export<ReadFrames>("sf_readf_float");
                close = Export<CloseFile>("sf_close");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IReadOnlyDictionary<double, double> Score(string songPath, IReadOnlyList<Note> source,
            Func<double, double> seconds, double offsetSeconds = 0, CancellationToken cancellation = default)
        {
            if (!double.IsFinite(offsetSeconds))
                throw new ArgumentOutOfRangeException(nameof(offsetSeconds));
            var beats = NestedTimings.DistinctBeats(source);
            if (beats.Length == 0)
                return new Dictionary<double, double>();
            var (mono, sampleRate) = Decode(songPath, cancellation);
            double offset = Math.Ceiling(offsetSeconds * sampleRate) / sampleRate;
            var samples = Resample(mono, sampleRate, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var scores = MixOnsets.Score(samples, beats.Select(beat => seconds(beat) + offset).ToArray());
            return beats.Zip(scores, (beat, score) => (First: beat, Second: score)).ToDictionary(pair => pair.First, pair => pair.Second);
        }

        internal (float[] Mono, int SampleRate) Decode(string path, CancellationToken cancellation)
        {
            if (library == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(MixAudio));
            var info = new SoundInfo();
            var file = open(Path.GetFullPath(path), 0x10, ref info);
            if (file == IntPtr.Zero)
                throw new InvalidDataException($"Could not decode song audio at {path}.");
            try
            {
                if (info.Channels is < 1 or > 64 || info.SampleRate <= 0 || info.Frames is < 1 or > int.MaxValue)
                    throw new InvalidDataException("Song audio has unsupported sample dimensions.");
                const int blockFrames = 8192;
                var interleaved = new float[blockFrames * info.Channels];
                var mono = new float[(int)info.Frames];
                int position = 0;
                while (position < mono.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    long frames = read(file, interleaved, Math.Min(blockFrames, mono.Length - position));
                    if (frames <= 0)
                        break;
                    for (int frame = 0; frame < frames; frame++)
                    {
                        float sum = 0;
                        int first = frame * info.Channels;
                        for (int channel = 0; channel < info.Channels; channel++)
                            sum += interleaved[first + channel];
                        mono[position++] = sum / info.Channels;
                    }
                }
                if (position != mono.Length)
                    throw new InvalidDataException($"Song audio ended after {position} of {mono.Length} frames.");
                return (mono, info.SampleRate);
            }
            finally
            {
                close(file);
            }
        }

        internal static float[] Resample(float[] mono, int sourceRate, CancellationToken cancellation)
        {
            if (sourceRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceRate));
            if (sourceRate == MixOnsets.SampleRate)
                return mono;

            const int halfWidth = 48;
            const int phases = 1024;
            double ratio = (double)MixOnsets.SampleRate / sourceRate;
            double cutoff = Math.Min(1, ratio) * .95;
            var kernel = new double[phases + 1][];
            for (int phase = 0; phase <= phases; phase++)
            {
                double fraction = (double)phase / phases;
                var weights = new double[2 * halfWidth];
                double sum = 0;
                for (int index = 0; index < weights.Length; index++)
                {
                    double distance = index - halfWidth + 1 - fraction;
                    if (Math.Abs(distance) >= halfWidth)
                        continue;
                    double angle = Math.PI * cutoff * distance;
                    double windowAngle = Math.PI * distance / halfWidth;
                    double window = .42 + .5 * Math.Cos(windowAngle) + .08 * Math.Cos(2 * windowAngle);
                    weights[index] = cutoff * (angle == 0 ? 1 : Math.Sin(angle) / angle) * window;
                    sum += weights[index];
                }
                for (int index = 0; index < weights.Length; index++)
                    weights[index] /= sum;
                kernel[phase] = weights;
            }

            var result = new float[checked((int)Math.Ceiling(mono.Length * ratio))];
            for (int index = 0; index < result.Length; index++)
            {
                if (index % 4096 == 0)
                    cancellation.ThrowIfCancellationRequested();
                double input = index * (double)sourceRate / MixOnsets.SampleRate;
                int center = (int)input;
                int phase = (int)Math.Round((input - center) * phases);
                var weights = kernel[phase];
                int first = center - halfWidth + 1;
                int low = Math.Max(0, -first), high = Math.Min(weights.Length, mono.Length - first);
                double value = 0;
                for (int tap = low; tap < high; tap++)
                    value += mono[first + tap] * weights[tap];
                result[index] = (float)value;
            }
            return result;
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref library, IntPtr.Zero);
            if (handle != IntPtr.Zero)
                NativeLibrary.Free(handle);
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        [StructLayout(LayoutKind.Sequential)]
        private struct SoundInfo
        {
            public long Frames;
            public int SampleRate, Channels, Format, Sections, Seekable;
        }

        private delegate IntPtr OpenFile(string path, int mode, ref SoundInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr OpenWideFile([MarshalAs(UnmanagedType.LPWStr)] string path, int mode, ref SoundInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr OpenUtf8File([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode, ref SoundInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long ReadFrames(IntPtr file, [Out] float[] samples, long count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CloseFile(IntPtr file);
    }
}
