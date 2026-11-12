using System.IO;
using System.Security.Cryptography;

namespace DiscForgeStudio.Services;

public sealed record DiscImageReadResult(string ImagePath, long BytesRead, string Sha256, DateTime Started, DateTime Finished);

public sealed class DiscImageReadService
{
    private const int BufferSize = 1024 * 1024;

    public async Task<DiscImageReadResult> ReadAsync(
        string driveName,
        string targetPath,
        long expectedBytes,
        IProgress<(int Percent, string Message)> progress,
        CancellationToken token)
    {
        if (expectedBytes <= 0) throw new InvalidOperationException("Die lesbare Disc-Groesse konnte nicht bestimmt werden.");
        var devicePath = @"\\.\" + driveName.Trim().TrimEnd('\\');
        var partialPath = targetPath + ".partial";
        if (File.Exists(targetPath)) throw new IOException("Die Ziel-ISO existiert bereits. Bitte einen anderen Dateinamen waehlen.");
        if (File.Exists(partialPath)) File.Delete(partialPath);

        var started = DateTime.Now;
        long totalRead = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var source = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[BufferSize];
            while (totalRead < expectedBytes)
            {
                token.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, expectedBytes - totalRead);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false);
                if (read <= 0) throw new EndOfStreamException("Die Disc endete frueher als erwartet.");
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                totalRead += read;
                var percent = (int)Math.Clamp(totalRead * 100L / expectedBytes, 0, 100);
                progress.Report((percent, $"Disc wird gelesen: {FormatBytes(totalRead)} von {FormatBytes(expectedBytes)}"));
            }
            await target.FlushAsync(token).ConfigureAwait(false);
            target.Close();
            File.Move(partialPath, targetPath);
            return new DiscImageReadResult(targetPath, totalRead, Convert.ToHexString(hash.GetHashAndReset()), started, DateTime.Now);
        }
        catch
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            throw;
        }
    }

    public static async Task<string> ComputeSha256Async(string path, IProgress<(int Percent, string Message)> progress, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = stream.Length;
        long readTotal = 0;
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read); readTotal += read;
            var percent = length <= 0 ? 100 : (int)Math.Clamp(readTotal * 100L / length, 0, 100);
            progress.Report((percent, $"Image wird geprueft: {FormatBytes(readTotal)} von {FormatBytes(length)}"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FormatBytes(long value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.00} GB" : $"{value / 1_000_000d:0.0} MB";
}