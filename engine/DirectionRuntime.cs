using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    internal sealed class DirectionRuntime
    {
        internal static string ExecutableName =>
            Path.DirectorySeparatorChar == '\\' ? "qjs.exe" : "qjs";

        private readonly string executable;
        private readonly string dispatch;

        internal DirectionRuntime(string runtimeDirectory)
        {
            executable = Path.GetFullPath(Path.Combine(runtimeDirectory, ExecutableName));
            dispatch = Path.Combine(Path.GetDirectoryName(executable)!, "dispatch.mjs");
        }

        internal Task<JsonObject> Plan(JsonObject input, CancellationToken cancellation) =>
            Run("plan_directions", input, cancellation);

        internal Task<JsonObject> Audit(JsonObject chart, double bpm, JsonArray reentries, CancellationToken cancellation,
            JsonArray repairDomains = null) =>
            Run("check_parity", new JsonObject
            {
                ["chart"] = chart.DeepClone(),
                ["bpm"] = bpm,
                ["reentries"] = reentries.DeepClone(),
                ["repairDomains"] = repairDomains,
            }, cancellation);

        private async Task<JsonObject> Run(string task, JsonObject input, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(executable) || !File.Exists(dispatch))
                throw new FileNotFoundException("The bundled direction runtime is missing.", executable);

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            ProcessRuntime.AddArguments(start, Path.GetFileName(dispatch), task);

            using var process = Process.Start(start) ?? throw new IOException("Could not start the direction runtime.");
            using var registration = cancellation.Register(() =>
            {
                try { ProcessRuntime.Stop(process); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            });
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            IOException inputError = null;
            try
            {
                await process.StandardInput.WriteAsync(input.ToJsonString()).ConfigureAwait(false);
            }
            catch (IOException error)
            {
                inputError = error;
            }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException error) { inputError ??= error; }
            }
            await ProcessRuntime.Wait(process, CancellationToken.None).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();

            string stderr = await errors.ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                foreach (string line in stderr.Split('\n'))
                {
                    const string prefix = "Error: No supported direction path";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        throw new DirectionPathFailure(line["Error: ".Length..].TrimEnd());
                }
                throw new IOException($"Direction runtime exited with code {process.ExitCode}. {stderr.Trim()}", inputError);
            }
            if (inputError != null)
                throw new IOException("Could not send input to the direction runtime.", inputError);
            try
            {
                return JsonNode.Parse(stdout) as JsonObject ?? throw new InvalidDataException("Expected a direction result object.");
            }
            #if CHROMAPPER
            catch (Newtonsoft.Json.JsonException error)
#else
            catch (JsonException error)
#endif
            {
                throw new InvalidDataException("The direction runtime returned invalid JSON.", error);
            }
        }
    }

    internal sealed class DirectionPathFailure : InvalidOperationException
    {
        internal DirectionPathFailure(string message) : base(message) { }
    }
}

