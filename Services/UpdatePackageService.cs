using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class UpdatePackageService
{
    private const int MaxEntries = 2048;
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add", "Replace", "Delete"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public UpdateValidationResult Validate(string packagePath, string currentVersion)
    {
        var checks = new List<string>();
        var errors = new List<string>();
        UpdatePackageManifest? manifest = null;

        try
        {
            if (!File.Exists(packagePath))
                throw new FileNotFoundException("Patch-Paket wurde nicht gefunden.", packagePath);

            var packageInfo = new FileInfo(packagePath);
            if (packageInfo.Length > MaxPackageBytes)
                errors.Add($"Patch-ZIP ist größer als {MaxPackageBytes / 1024 / 1024} MiB.");

            using var archive = ZipFile.OpenRead(packagePath);
            checks.Add($"ZIP lesbar: {archive.Entries.Count} Einträge");

            if (archive.Entries.Count == 0) errors.Add("Das Patch-ZIP ist leer.");
            if (archive.Entries.Count > MaxEntries) errors.Add($"Das Patch-ZIP enthält mehr als {MaxEntries} Einträge.");

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                try { expandedBytes = checked(expandedBytes + entry.Length); }
                catch (OverflowException) { errors.Add("Die entpackte Gesamtgröße ist ungültig."); break; }
            }
            if (expandedBytes > MaxExpandedBytes)
                errors.Add($"Die entpackte Gesamtgröße überschreitet {MaxExpandedBytes / 1024 / 1024} MiB.");

            var roots = archive.Entries
                .Select(e => NormalizeArchivePath(e.FullName).Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count != 1) errors.Add("Das Patch-ZIP muss genau einen Hauptordner enthalten.");
            var root = roots.FirstOrDefault() ?? string.Empty;

            foreach (var entry in archive.Entries)
            {
                if (!TryNormalizeRelativePath(entry.FullName, out _, out var pathError))
                    errors.Add($"Unsicherer ZIP-Pfad: {entry.FullName} ({pathError})");
            }

            var manifestEntries = archive.Entries.Where(e =>
                NormalizeArchivePath(e.FullName).Equals(root + "/patch-manifest.json", StringComparison.OrdinalIgnoreCase)).ToList();

            if (manifestEntries.Count == 0) errors.Add("patch-manifest.json fehlt im Hauptordner.");
            else if (manifestEntries.Count > 1) errors.Add("patch-manifest.json ist mehrfach vorhanden.");
            else
            {
                using var stream = manifestEntries[0].Open();
                manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(stream, JsonOptions);
                ValidateManifest(archive, root, manifest, currentVersion, checks, errors);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetBaseException().Message);
        }

        return new UpdateValidationResult(
            errors.Count == 0,
            errors.Count == 0 ? "Patch-Paket ist technisch gültig." : "Patch-Paket wurde nicht freigegeben.",
            manifest,
            checks,
            errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void ValidateManifest(ZipArchive archive, string root, UpdatePackageManifest? manifest,
        string currentVersion, List<string> checks, List<string> errors)
    {
        if (manifest is null) { errors.Add("Manifest konnte nicht gelesen werden."); return; }

        if (manifest.SchemaVersion != 1) errors.Add("Nicht unterstützte Manifestversion.");
        else checks.Add("Manifest-Schema 1 erkannt");

        if (!string.Equals(manifest.SourceVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Ausgangsversion passt nicht: benötigt {manifest.SourceVersion}, installiert {currentVersion}");
        else checks.Add("Ausgangsversion stimmt");

        if (string.IsNullOrWhiteSpace(manifest.PatchId)) errors.Add("Patch-ID fehlt.");
        if (string.IsNullOrWhiteSpace(manifest.TargetVersion)) errors.Add("Zielversion fehlt.");
        if (string.Equals(manifest.SourceVersion, manifest.TargetVersion, StringComparison.OrdinalIgnoreCase))
            errors.Add("Ausgangs- und Zielversion dürfen nicht identisch sein.");
        if (manifest.Files is null || manifest.Files.Count == 0) { errors.Add("Das Manifest enthält keine Dateien."); return; }

        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!TryNormalizeRelativePath(file.Path, out var rel, out var pathError))
            { errors.Add($"Unsicherer Manifestpfad: {file.Path} ({pathError})"); continue; }
            if (!manifestPaths.Add(rel)) { errors.Add("Doppelter Manifestpfad: " + rel); continue; }
            if (!AllowedOperations.Contains(file.Operation))
            { errors.Add($"Unzulässige Operation '{file.Operation}' für {rel}."); continue; }

            if (file.Operation.Equals("Delete", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(file.NewSha256)) errors.Add("Delete darf keinen NewSha256-Wert enthalten: " + rel);
                continue;
            }

            if (!IsSha256(file.NewSha256)) { errors.Add("Ungültiger NewSha256-Wert: " + rel); continue; }
            var payloadPath = root + "/payload/" + rel;
            var payloadMatches = archive.Entries.Where(e => !IsDirectory(e) &&
                NormalizeArchivePath(e.FullName).Equals(payloadPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (payloadMatches.Count == 0) { errors.Add("Payload fehlt: " + rel); continue; }
            if (payloadMatches.Count > 1) { errors.Add("Payload ist mehrfach vorhanden: " + rel); continue; }

            using var input = payloadMatches[0].Open();
            var hash = Convert.ToHexString(SHA256.HashData(input));
            if (!hash.Equals(file.NewSha256, StringComparison.OrdinalIgnoreCase)) errors.Add("SHA-256 stimmt nicht: " + rel);
            else checks.Add("SHA-256 OK: " + rel);
        }

        var actualPayload = archive.Entries.Where(e => !IsDirectory(e))
            .Select(e => NormalizeArchivePath(e.FullName))
            .Where(p => p.StartsWith(root + "/payload/", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(root.Length + "/payload/".Length)..])
            .ToList();

        foreach (var payload in actualPayload)
            if (!manifestPaths.Contains(payload)) errors.Add("Nicht im Manifest aufgeführte Payload-Datei: " + payload);

        if (errors.Count == 0) checks.Add($"Manifest und Payload vollständig abgeglichen: {manifestPaths.Count} Datei(en)");
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string NormalizeArchivePath(string value) => value.Replace('\\', '/').TrimStart('/');

    private static bool TryNormalizeRelativePath(string? value, out string normalized, out string error)
    {
        normalized = string.Empty; error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) { error = "Pfad ist leer"; return false; }
        var candidate = value.Replace('\\', '/');
        if (candidate.StartsWith('/') || Path.IsPathRooted(value) || candidate.Contains(':'))
        { error = "absoluter oder laufwerksbezogener Pfad"; return false; }
        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { error = "Pfad enthält keinen Dateinamen"; return false; }
        if (parts.Any(p => p is "." or "..")) { error = "Punktsegment ist nicht erlaubt"; return false; }
        if (parts.Any(p => p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        { error = "ungültiges Zeichen"; return false; }
        normalized = string.Join('/', parts);
        return true;
    }
}
