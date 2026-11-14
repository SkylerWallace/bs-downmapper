using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    internal static class EngineFiles
    {
        internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        internal static void Replace(string source, string destination)
        {
            if (File.Exists(destination)) File.Replace(source, destination, null);
            else File.Move(source, destination);
        }

        internal static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Hash(stream);
        }

        internal static string Hash(Stream stream)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
    }

    internal static class EngineMath
    {
        internal static double FusedMultiplyAdd(double a, double b, double c)
        {
#if CHROMAPPER
            return WindowsFma(a, b, c);
#else
            return Math.FusedMultiplyAdd(a, b, c);
#endif
        }

#if CHROMAPPER
        [DllImport("ucrtbase.dll", EntryPoint = "fma", CallingConvention = CallingConvention.Cdecl)]
        private static extern double WindowsFma(double a, double b, double c);
#endif

        internal static int PopCount(ulong bits)
        {
#if CHROMAPPER
            bits -= (bits >> 1) & 0x5555555555555555UL;
            bits = (bits & 0x3333333333333333UL) + ((bits >> 2) & 0x3333333333333333UL);
            bits = (bits + (bits >> 4)) & 0x0f0f0f0f0f0f0f0fUL;
            return (int)((bits * 0x0101010101010101UL) >> 56);
#else
            return System.Numerics.BitOperations.PopCount(bits);
#endif
        }
    }

    internal static class ProcessRuntime
    {
        internal static void AddArguments(ProcessStartInfo start, params string[] arguments)
        {
#if CHROMAPPER
            start.Arguments = string.Join(" ", arguments.Select(Quote));
#else
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
#endif
        }

#if CHROMAPPER
        private static string Quote(string argument) =>
            "\"" + Regex.Replace(argument, @"(\\*)(""|$)", match =>
            {
                string slashes = match.Groups[1].Value;
                return slashes + slashes + (match.Groups[2].Length == 0 ? "" : "\\\"");
            }) + "\"";
#endif

        internal static void Stop(Process process)
        {
#if CHROMAPPER
            process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }

        internal static async Task Wait(Process process, CancellationToken cancellation)
        {
#if CHROMAPPER
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler exited = (_, _) => completed.TrySetResult(true);
            process.Exited += exited;
            try
            {
                process.EnableRaisingEvents = true;
                if (process.HasExited) completed.TrySetResult(true);
                await completed.Task.ConfigureAwait(false);
            }
            finally { process.Exited -= exited; }
            cancellation.ThrowIfCancellationRequested();
#else
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
#endif
        }
    }
}
