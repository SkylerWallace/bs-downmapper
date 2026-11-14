using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace BsDownmap.ChroMapper
{
    internal static class BundledAssets
    {
        internal const string Ranker = ".tools/native-engine/shared-percentage-v1-ranker.bdt";
        internal const string Decoder = ".tools/native-engine/libsndfile_x64.bin";
        private static readonly string[] Profiles = { "expert.json", "hard.json", "normal.json", "easy.json" };
        private static readonly string[] RuntimeFiles =
        {
            Ranker, Decoder, ".tools/quickjs-runtime/qjs.exe", ".tools/quickjs-runtime/dispatch.mjs",
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
                foreach (string name in RuntimeFiles)
                {
                    using var source = (archive.GetEntry(name) ??
                        throw new InvalidDataException($"Missing runtime file {name}.")).Open();
                    lease.Write(name, source);
                }
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
