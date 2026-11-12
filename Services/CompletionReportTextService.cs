using System.Globalization;
using System.IO;
using System.Text;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class CompletionReportTextService
{
    public string Write(CompletionReport report)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DiscForge Studio", "Berichte");
        Directory.CreateDirectory(folder);
        var safeOperation = string.Concat(report.Operation.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '-');
        var path = Path.Combine(folder,
            $"DiscForge-{safeOperation}-{report.FinishedAt:yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder()
            .AppendLine($"DISCFORGE ABSCHLUSSBERICHT")
            .AppendLine(new string('=', 68))
            .AppendLine($"Kennung: {report.Id}")
            .AppendLine($"Programmversion: {report.ApplicationVersion}")
            .AppendLine($"Vorgang: {report.Operation}")
            .AppendLine($"Ergebnis: {OutcomeText(report.Outcome)}")
            .AppendLine($"Beginn: {report.StartedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Ende: {report.FinishedAt:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Dauer: {FormatDuration(report.Duration)}")
            .AppendLine($"Quelle: {report.Source ?? "-"}")
            .AppendLine($"Ziel: {report.Target ?? "-"}")
            .AppendLine($"Medium: {report.MediumType ?? "-"}")
            .AppendLine($"Datentraegername: {report.VolumeLabel ?? "-"}")
            .AppendLine($"Zusammenfassung: {report.Summary ?? "-"}")
            .AppendLine();

        foreach (var section in report.Sections)
        {
            text.AppendLine(section.Title.ToUpperInvariant());
            text.AppendLine(new string('-', 68));
            foreach (var metric in section.Metrics)
                text.AppendLine($"{metric.Name}: {metric.Value}{(string.IsNullOrWhiteSpace(metric.Unit) ? "" : " " + metric.Unit)}");
            foreach (var check in section.Checks)
                text.AppendLine($"[{CheckText(check.Outcome)}] {check.Name}{(string.IsNullOrWhiteSpace(check.Details) ? "" : ": " + check.Details)}");
            foreach (var note in section.Notes) text.AppendLine("Hinweis: " + note);
            text.AppendLine();
        }

        if (report.LogEntries.Count > 0)
        {
            text.AppendLine("PROTOKOLL");
            text.AppendLine(new string('-', 68));
            foreach (var entry in report.LogEntries)
                text.AppendLine($"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}{(string.IsNullOrWhiteSpace(entry.Details) ? "" : " | " + entry.Details)}");
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string OutcomeText(ReportOutcome value) => value switch
    {
        ReportOutcome.Succeeded => "ERFOLGREICH",
        ReportOutcome.SucceededWithWarnings => "ERFOLGREICH MIT HINWEISEN",
        ReportOutcome.Canceled => "ABGEBROCHEN",
        _ => "FEHLGESCHLAGEN"
    };
    private static string CheckText(ReportCheckOutcome value) => value switch
    {
        ReportCheckOutcome.Passed => "BESTANDEN",
        ReportCheckOutcome.Warning => "HINWEIS",
        ReportCheckOutcome.Failed => "FEHLER",
        _ => "NICHT AUSGEFUEHRT"
    };
    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
