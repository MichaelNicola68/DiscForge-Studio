using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class IsoHashService
{
    public async Task<IsoInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return new(false, full, 0, string.Empty, DateTimeOffset.Now, "ISO-Datei wurde nicht gefunden.");
        await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(input, cancellationToken);
        return new(true, full, input.Length, Convert.ToHexString(hash), DateTimeOffset.Now, "ISO-Datei ist lesbar; SHA-256 wurde berechnet.");
    }
}
