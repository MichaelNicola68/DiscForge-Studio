using System.IO;
using System.Text.Json;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class SessionHistoryService
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiscForge Studio", "Multisession");
    private static string HistoryPath => Path.Combine(Folder, "SessionHistory.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public IReadOnlyList<SessionHistoryEntry> Load()
    {
        if (!File.Exists(HistoryPath)) return Array.Empty<SessionHistoryEntry>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<SessionHistoryEntry>>(
                File.ReadAllText(HistoryPath), Options);
            return entries is null
                ? Array.Empty<SessionHistoryEntry>()
                : entries.OrderByDescending(entry => entry.SessionNumber).ToList();
        }
        catch { return Array.Empty<SessionHistoryEntry>(); }
    }

    public SessionHistoryEntry AddWrittenSession(MultisessionSnapshot snapshot)
    {
        var entries = Load().ToList();
        var nextNumber = entries.Count == 0 ? 1 : entries.Max(entry => entry.SessionNumber) + 1;
        var entry = new SessionHistoryEntry(
            Guid.NewGuid().ToString("N"), nextNumber, snapshot.Created, snapshot.Drive,
            snapshot.Finalized, snapshot.FreeBytesBefore, null, "Noch nicht geprüft", null,
            0, 0, 0,
            snapshot.NewFiles.Select(file => new SessionHistoryFile(file.RelativePath, file.Size, file.Sha256)).ToList());
        entries.Add(entry);
        Save(entries);
        return entry;
    }

    public SessionHistoryEntry? ApplyVerification(MultisessionSnapshot snapshot, MultisessionVerifyResult result)
    {
        var entries = Load().ToList();
        var index = entries.FindIndex(entry =>
            entry.WrittenAt == snapshot.Created &&
            string.Equals(entry.Drive, snapshot.Drive, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var current = entries[index];
        var updated = current with
        {
            FreeBytesAfter = result.FreeBytesAfter,
            VerificationStatus = result.Success ? "BESTANDEN" : "NICHT BESTANDEN",
            VerifiedAt = result.Finished,
            MatchingFiles = result.NewFilesMatching,
            FailedFiles = result.NewFilesFailed,
            MissingPreviousFiles = result.PreviousFilesMissing
        };
        entries[index] = updated;
        Save(entries);
        return updated;
    }

    public string BuildReport(SessionHistoryEntry entry)
    {
        var lines = new List<string>
        {
            ApplicationVersionService.ReportTitle("Multisession-Sitzungsverlauf"),
            "====================================================",
            $"Sitzung: {entry.SessionNumber}",
            $"Kennung: {entry.Id}",
            $"Geschrieben: {entry.WrittenAt:yyyy-MM-dd HH:mm:ss}",
            $"Laufwerk: {entry.Drive}",
            $"Endgültig abgeschlossen: {(entry.Finalized ? "Ja" : "Nein")}",
            $"Freier Speicher vorher: {FormatBytes(entry.FreeBytesBefore)}",
            $"Freier Speicher nachher: {(entry.FreeBytesAfter is long value ? FormatBytes(value) : "Noch nicht ermittelt")}",
            $"Verify+: {entry.VerificationStatus}",
            $"Geprüft: {(entry.VerifiedAt is DateTime date ? date.ToString("yyyy-MM-dd HH:mm:ss") : "Noch nicht")}",
            $"Übereinstimmend: {entry.MatchingFiles}",
            $"Fehlerhaft: {entry.FailedFiles}",
            $"Fehlende frühere Dateien: {entry.MissingPreviousFiles}",
            $"Neue Dateien: {entry.NewFiles.Count}",
            "",
            "Dateien der Sitzung:"
        };
        foreach (var file in entry.NewFiles)
        {
            lines.Add($"- {file.RelativePath}");
            lines.Add($"  Größe: {FormatBytes(file.Size)}");
            lines.Add($"  SHA-256: {file.Sha256}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void Save(IEnumerable<SessionHistoryEntry> entries)
    {
        Directory.CreateDirectory(Folder);
        var temp = HistoryPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries.OrderBy(entry => entry.SessionNumber), Options));
        File.Move(temp, HistoryPath, true);
    }

    private static string FormatBytes(long value) => value >= 1_000_000_000
        ? $"{value / 1_000_000_000d:0.00} GB"
        : value >= 1_000_000 ? $"{value / 1_000_000d:0.0} MB"
        : value >= 1_000 ? $"{value / 1_000d:0.0} KB" : $"{value} B";
}
