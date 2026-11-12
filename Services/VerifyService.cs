using System.IO;
using System.Security.Cryptography;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class VerifyService
{
    public async Task<VerificationResult> VerifyAsync(
        string driveName,
        BurnSnapshot snapshot,
        IProgress<(int Percent, string Message)> progress,
        CancellationToken token)
    {
        var started = DateTime.Now;
        var driveRoot = driveName.EndsWith('\\') ? driveName : driveName + "\\";
        if (!Directory.Exists(driveRoot))
            throw new IOException($"Die Disc in {driveRoot} ist noch nicht lesbar.");

        var results = new List<VerificationFileResult>();
        for (var index = 0; index < snapshot.Files.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var expected = snapshot.Files[index];
            var relative = expected.RelativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var discPath = Path.Combine(driveRoot, relative);
            progress.Report((5 + (int)Math.Round((index + 1) * 90d / Math.Max(1, snapshot.Files.Count)),
                $"Prüfe {index + 1}/{snapshot.Files.Count}: {relative}"));

            var originalExists = File.Exists(expected.FullPath);
            var discExists = File.Exists(discPath);
            var currentOriginalSize = originalExists ? new FileInfo(expected.FullPath).Length : 0;
            var discSize = discExists ? new FileInfo(discPath).Length : 0;
            var currentOriginalHash = originalExists ? await HashFileAsync(expected.FullPath, token) : "";
            var discHash = discExists && discSize == expected.Size ? await HashFileAsync(discPath, token) : "";

            var originalStatus = !originalExists ? "Original fehlt"
                : currentOriginalSize != expected.Size || currentOriginalHash != expected.Sha256
                    ? "Original seit Brennen verändert"
                    : "Original unverändert";

            var discStatus = !discExists ? "Auf Disc fehlt"
                : discSize != expected.Size ? "Disc-Größe abweichend"
                : discHash != expected.Sha256 ? "Disc SHA-256 abweichend"
                : "OK";

            results.Add(new(relative, discStatus, originalStatus, expected.Sha256, currentOriginalHash, discHash,
                expected.Size, currentOriginalSize, discSize));
        }

        var matching = results.Count(result => result.DiscStatus == "OK");
        var failed = results.Count - matching;
        var changed = results.Count(result => result.OriginalStatus != "Original unverändert");
        progress.Report((100, failed == 0
            ? $"Verify+ bestanden. {changed} Originaldatei(en) nachträglich verändert."
            : $"Verify+ fand {failed} Disc-Abweichung(en)."));
        return new(started, DateTime.Now, driveRoot, snapshot.Created, results.Count, matching, failed, changed, results);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
