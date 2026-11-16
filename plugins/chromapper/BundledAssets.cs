using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Runtime.InteropServices;
using BsDownmap.Engine;

namespace BsDownmap.ChroMapper
{
    internal static class BundledAssets
    {
        internal const string Ranker = ".tools/native-engine/shared-percentage-v1-ranker.bdt";
        internal const string Decoder = ".tools/native-engine/libsndfile_x64.bin";
        private const string QuickJsDirectory = ".tools/quickjs-runtime/";
        private static readonly string QuickJs = QuickJsDirectory + DirectionRuntime.ExecutableName;

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);

        private static readonly string[] Profiles = { "expert.json", "hard.json", "normal.json", "easy.json" };
        private static readonly string[] RuntimeFiles =
        {
            Ranker, Decoder, QuickJs, ".tools/quickjs-runtime/dispatch.mjs",
        };

        private static Stream Open(string name) =>
            typeof(BundledAssets).Assembly.GetManifestResourceStream("BsDownmap." + name)
            ?? throw new InvalidDataException($"Missing bundled asset {name}.");

        internal static string ReadText(string name)
        {
            using var reader = new StreamReader(Open(name));
            return reader.ReadToEnd();
        }

        internal static byte[] ReadBytes(string name)
        {
            using var source = Open(name);
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            return buffer.ToArray();
        }

        internal static RuntimeFilesLease ExtractRuntime()
        {
            var lease = new RuntimeFilesLease();
            try
            {
                using var archive = new ZipArchive(Open("runtime.zip"), ZipArchiveMode.Read);
                if (archive.GetEntry(QuickJs) == null)
                {
                    bool windows = DirectionRuntime.ExecutableName == "qjs.exe";
                    string otherExecutable = windows ? "qjs" : "qjs.exe";
                    if (archive.GetEntry(QuickJsDirectory + otherExecutable) != null)
                    {
                        string platform = windows ? "win-x64" : "linux-x64";
                        throw new InvalidDataException(
                            $"Wrong BS Downmapper build for this platform. Replace it with BsDownmapper.ChroMapper.{platform}.dll.");
                    }
                }
                foreach (string name in RuntimeFiles)
                {
                    using var source = (archive.GetEntry(name) ??
                        throw new InvalidDataException($"Missing runtime file {name}.")).Open();
                    lease.Write(name, source);
                }
                if (Path.DirectorySeparatorChar == '/' && chmod(lease.PathFor(QuickJs), 0x1C0) != 0) // 0700
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not make QuickJS executable.");

                foreach (string profile in Profiles)
                {
                    using var source = Open(profile);
                    lease.Write("profiles/" + profile, source);
                }
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        internal sealed class RuntimeFilesLease : IDisposable
        {
            internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
                "bs-downmap-" + Guid.NewGuid().ToString("N").Substring(0, 16));

            internal string PathFor(string name) => Path.Combine(DirectoryPath, name.Replace('/', Path.DirectorySeparatorChar));

            internal void Write(string name, Stream source)
            {
                string path = PathFor(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                source.CopyTo(destination);
            }

            public void Dispose()
            {
                for (int attempt = 0; Directory.Exists(DirectoryPath); attempt++)
                {
                    try { Directory.Delete(DirectoryPath, recursive: true); }
                    catch (Exception error) when (attempt < 4 && (error is IOException || error is UnauthorizedAccessException))
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }
}
