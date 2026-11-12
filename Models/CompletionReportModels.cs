namespace DiscForgeStudio.Models;

public enum ReportOutcome
{
    Succeeded,
    SucceededWithWarnings,
    Canceled,
    Failed
}

public enum ReportEntryLevel
{
    Information,
    Warning,
    Error
}

public enum ReportCheckOutcome
{
    Passed,
    Warning,
    Failed,
    NotRun
}

public sealed record ReportMetric(
    string Name,
    string Value,
    string? Unit = null);

public sealed record ReportCheck(
    string Name,
    ReportCheckOutcome Outcome,
    string? Details = null);

public sealed record ReportLogEntry(
    DateTime Timestamp,
    ReportEntryLevel Level,
    string Message,
    string? Details = null);

public sealed record ReportSection(
    string Title,
    IReadOnlyList<ReportMetric> Metrics,
    IReadOnlyList<ReportCheck> Checks,
    IReadOnlyList<string> Notes);

public sealed record CompletionReport(
    Guid Id,
    string Operation,
    ReportOutcome Outcome,
    DateTime StartedAt,
    DateTime FinishedAt,
    string ApplicationVersion,
    string? Source,
    string? Target,
    string? MediumType,
    string? VolumeLabel,
    string? Summary,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyList<ReportLogEntry> LogEntries)
{
    public TimeSpan Duration => FinishedAt - StartedAt;

    public bool WasSuccessful =>
        Outcome is ReportOutcome.Succeeded or ReportOutcome.SucceededWithWarnings;
}
