using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class SnapshotService
{
    private static string SnapshotDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscForge Studio",
        "VerifyPlus");

    private static string LastSnapshotPath => Path.Combine(SnapshotDirectory, "LastBurnSnapshot.json");

    public async Task<BurnSnapshot> CreateAsync(
        string drive,
        string discLabel,
        IReadOnlyList<CompilationItem> items,
        IProgress<(int Percent, string Message)> progress,
        CancellationToken token)
    {
        var files = new List<BurnSnapshotFile>();
        var reportClock = Stopwatch.StartNew();
        var lastReportedIndex = -1;

        for (var index = 0; index < items.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = items[index];
            if (!File.Exists(item.FullPath))
                throw new FileNotFoundException("Quelldatei fehlt.", item.FullPath);

            var isFirst = index == 0;
            var isLast = index == items.Count - 1;
            var percentChanged = (int)Math.Round((index + 1) * 100d / Math.Max(1, items.Count)) !=
                                 (int)Math.Round((lastReportedIndex + 1) * 100d / Math.Max(1, items.Count));

            if (isFirst || isLast || (reportClock.ElapsedMilliseconds >= 250 && percentChanged))
            {
                progress.Report((
                    2 + (int)Math.Round((index + 1) * 12d / Math.Max(1, items.Count)),
                    $"Verify+-Snapshot {index + 1}/{items.Count}: {item.RelativePath}"));
                lastReportedIndex = index;
                reportClock.Restart();
            }

            var info = new FileInfo(item.FullPath);
            var hash = await HashFileAsync(item.FullPath, token).ConfigureAwait(false);
            files.Add(new(item.FullPath, item.RelativePath, info.Length, hash));
        }

        return new(ApplicationVersionService.Current, DateTime.Now, discLabel, drive, files);
    }

    public void Save(BurnSnapshot snapshot)
    {
        Directory.CreateDirectory(SnapshotDirectory);
        File.WriteAllText(LastSnapshotPath,
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    public BurnSnapshot? LoadLast()
    {
        if (!File.Exists(LastSnapshotPath)) return null;
        try { return JsonSerializer.Deserialize<BurnSnapshot>(File.ReadAllText(LastSnapshotPath)); }
        catch { return null; }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
