using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed record UpdateSimulationResult(
    bool Success,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Errors,
    int AddCount,
    int ReplaceCount,
    int DeleteCount,
    int BackupFileCount,
    long BackupBytes,
    string PlannedBackupPath);

public sealed class UpdateInstallationSimulationService
{
    public UpdateSimulationResult Simulate(string packagePath, UpdatePackageManifest manifest)
    {
        var checks = new List<string>();
        var errors = new List<string>();
        var add = 0; var replace = 0; var delete = 0; var backupFiles = 0; long backupBytes = 0;
        var appRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var plannedBackup = Path.Combine(appRoot, "Update-Backups", Sanitize(manifest.PatchId) + "-SIMULATION");

        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var root = archive.Entries.Select(e => Normalize(e.FullName).Split('/', 2)[0])
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

            foreach (var file in manifest.Files)
            {
                if (!TryTarget(appRoot, file.Path, out var target, out var pathError))
                {
                    errors.Add($"Unsicherer Zielpfad {file.Path}: {pathError}");
                    continue;
                }

                var operation = file.Operation.Trim();
                if (operation.Equals("Add", StringComparison.OrdinalIgnoreCase))
                {
                    add++;
                    if (File.Exists(target)) errors.Add("ADD gesperrt, Zieldatei existiert bereits: " + file.Path);
                    else checks.Add("ADD möglich: " + file.Path);
                    ValidatePayload(archive, root, file, checks, errors);
                }
                else if (operation.Equals("Replace", StringComparison.OrdinalIgnoreCase))
                {
                    replace++;
                    if (ValidateExisting(target, file.OldSha256, file.Path, errors, out var bytes))
                    {
                        backupFiles++; backupBytes += bytes;
                        checks.Add("REPLACE-Ausgangsdatei und alter SHA-256 stimmen: " + file.Path);
                    }
                    ValidatePayload(archive, root, file, checks, errors);
                }
                else if (operation.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                {
                    delete++;
                    if (ValidateExisting(target, file.OldSha256, file.Path, errors, out var bytes))
                    {
                        backupFiles++; backupBytes += bytes;
                        checks.Add("DELETE-Ausgangsdatei und alter SHA-256 stimmen: " + file.Path);
                    }
                }
                else errors.Add("Unbekannte Dateioperation: " + file.Operation);
            }

            var tempRoot = Path.GetTempPath();
            var drive = new DriveInfo(Path.GetPathRoot(tempRoot)!);
            if (drive.AvailableFreeSpace < backupBytes)
                errors.Add("Im TEMP-Laufwerk ist nicht genügend freier Speicher für eine simulierte Sicherung vorhanden.");
            else checks.Add($"Sicherungskapazität ausreichend: {FormatBytes(backupBytes)} benötigt");

            if (errors.Count == 0)
            {
                checks.Add($"Sicherungsplan vollständig: {backupFiles} bestehende Datei(en), {FormatBytes(backupBytes)}");
                checks.Add("Rücksetzplan vollständig: REPLACE/DELETE aus Sicherung, ADD durch Entfernen rücksetzbar");
                checks.Add("Simulation abgeschlossen: Keine Programmdatei wurde erstellt, ersetzt oder gelöscht");
            }
        }
        catch (Exception ex) { errors.Add("Simulation fehlgeschlagen: " + ex.Message); }

        return new UpdateSimulationResult(errors.Count == 0, checks, errors, add, replace, delete,
            backupFiles, backupBytes, plannedBackup);
    }

    private static bool ValidateExisting(string target, string expectedHash, string relative,
        List<string> errors, out long bytes)
    {
        bytes = 0;
        if (!File.Exists(target)) { errors.Add("Ausgangsdatei fehlt: " + relative); return false; }
        var info = new FileInfo(target); bytes = info.Length;
        if (info.IsReadOnly) errors.Add("Ausgangsdatei ist schreibgeschützt: " + relative);
        using var stream = File.OpenRead(target);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        { errors.Add("Alter SHA-256 stimmt nicht: " + relative); return false; }
        return true;
    }

    private static void ValidatePayload(ZipArchive archive, string root, UpdatePackageFile file,
        List<string> checks, List<string> errors)
    {
        var wanted = Normalize(root + "/payload/" + file.Path);
        var entry = archive.Entries.FirstOrDefault(e => Normalize(e.FullName).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry is null) { errors.Add("Payload-Datei fehlt: " + file.Path); return; }
        using var stream = entry.Open();
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(file.NewSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("Payload-SHA-256 stimmt nicht: " + file.Path);
        else checks.Add("Payload für Änderung lesbar und SHA-256 bestätigt: " + file.Path);
    }

    private static bool TryTarget(string root, string relative, out string target, out string error)
    {
        target = string.Empty; error = string.Empty;
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Contains(':')) { error = "absoluter Pfad"; return false; }
        target = Path.GetFullPath(Path.Combine(root, normalized));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { error = "Pfad verlässt Programmordner"; return false; }
        return true;
    }

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');
    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string FormatBytes(long value) => value < 1024 ? $"{value} B" : value < 1024 * 1024 ? $"{value / 1024d:0.0} KiB" : $"{value / 1024d / 1024d:0.0} MiB";
}
