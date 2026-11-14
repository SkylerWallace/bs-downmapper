using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BsDownmap.Engine
{
    public sealed record SpreadTarget(string Difficulty, string Foundation, string Fingerprint);
    public sealed record SpreadTargetContext(string Source, string Staging, string Reports,
        SpreadTarget Target, IReadOnlyList<string> Completed, IReadOnlyList<string> Pending);

    public static class SpreadGeneration
    {
        public static async Task Run(string song, string reports, string expectedInfo, IReadOnlyList<SpreadTarget> targets,
            Func<SpreadTargetContext, CancellationToken, Task> generate, CancellationToken cancellation = default, Action beforePublish = null)
        {
            song = Path.GetFullPath(song);
            reports = Path.GetFullPath(reports);
            if (reports.Equals(song, StringComparison.OrdinalIgnoreCase) ||
                reports.StartsWith(song + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Generation reports must be outside the song folder.");
            var ranks = targets.Select(target => target.Difficulty).ToArray();
            if (ranks.Length == 0 || !ranks.SequenceEqual(TimingPreparation.Standard.Skip(1).Where(ranks.Contains)) ||
                targets.Any(target => !TimingPreparation.Standard.Contains(target.Foundation)))
                throw new InvalidDataException("Choose distinct Standard targets in highest-to-lowest order.");
            var expected = targets.ToDictionary(target => target.Difficulty, target => target.Fingerprint);
            SpreadPublisher.CheckSnapshot(song, expectedInfo, expected);
            var work = Path.Combine(reports, "work-" + Path.GetRandomFileName().Replace(".", ""));
            var staged = Path.Combine(work, "song");
            Directory.CreateDirectory(staged);
            try
            {
                foreach (string asset in Directory.EnumerateFiles(song))
                {
                    cancellation.ThrowIfCancellationRequested();
                    File.Copy(asset, Path.Combine(staged, Path.GetFileName(asset)));
                }
                BookmarkFiles.Copy(song, staged);
                for (int i = 0; i < targets.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    await generate(new SpreadTargetContext(song, staged, reports, targets[i],
                        ranks.Take(i).ToArray(), ranks.Skip(i + 1).ToArray()), cancellation).ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                beforePublish?.Invoke();
                SpreadPublisher.Publish(song, staged, reports, expectedInfo, expected);
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }

        public static string Fingerprint(string path)
        {
            if (!File.Exists(path))
                return "missing";
            using var stream = File.OpenRead(path);
            return EngineFiles.Hash(stream).ToLowerInvariant();
        }
    }
}
