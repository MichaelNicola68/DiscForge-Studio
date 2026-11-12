using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class OperationReportService
{
    public string Write(OperationResult result)
    {
        var baseDirectory = ResolveBaseDirectory(result.TargetPath);
        var reportDirectory = Path.Combine(baseDirectory, "DiscForge-Berichte");
        Directory.CreateDirectory(reportDirectory);

        var safeOperation = MakeSafeFileName(result.Operation);
        var reportPath = Path.Combine(
            reportDirectory,
            $"DiscForge-{safeOperation}-{result.FinishedAt:yyyyMMdd-HHmmss}.txt");

        var lines = new[]
        {
            "DISCFORGE VORGANGSBERICHT",
            "",
            $"Programmversion: {result.ApplicationVersion}",
            $"Vorgang: {result.Operation}",
            $"Status: {result.Status}",
            $"Beginn: {result.StartedAt:yyyy-MM-dd HH:mm:ss}",
            $"Ende: {result.FinishedAt:yyyy-MM-dd HH:mm:ss}",
            $"Dauer: {FormatDuration(result.Duration)}",
            "",
            $"Quelle: {result.Source}",
            $"Medium: {result.MediumType}",
            $"Datentraegername: {result.VolumeLabel}",
            $"Dateisystem: {result.FileSystem}",
            $"Lesbare Groesse: {FormatBytes(result.SourceBytes)}",
            $"Ziel: {result.TargetPath ?? "-"}",
            $"Ergebnisgroesse: {FormatBytes(result.ResultBytes)}",
            $"SHA-256: {result.Sha256 ?? "-"}",
            $"Pruefung: {result.VerificationStatus ?? "Noch nicht ausgefuehrt"}",
            "",
            $"Meldung: {result.Message ?? "-"}",
            "Sicherheitsmodus: Ausschliesslich lesender Zugriff auf die Quelldisc.",
            "Unvollstaendige Images werden nicht als fertige ISO freigegeben."
        };

        File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
        return reportPath;
    }

    public static string CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unbekannt";
    }

    private static string ResolveBaseDirectory(string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) return directory;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiscForge Studio");
    }

    private static string MakeSafeFileName(string value) =>
        string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))
            .Replace(' ', '-');

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatBytes(long value) => value switch
    {
        <= 0 => "0 B",
        >= 1_073_741_824 => $"{value / 1_073_741_824d:0.00} GiB",
        >= 1_048_576 => $"{value / 1_048_576d:0.00} MiB",
        >= 1024 => $"{value / 1024d:0.00} KiB",
        _ => $"{value} B"
    };
}