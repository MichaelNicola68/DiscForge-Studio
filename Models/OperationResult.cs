namespace DiscForgeStudio.Models;

public sealed record OperationResult(
    string Operation,
    string Status,
    DateTime StartedAt,
    DateTime FinishedAt,
    string Source,
    string MediumType,
    string VolumeLabel,
    string FileSystem,
    long SourceBytes,
    string? TargetPath,
    long ResultBytes,
    string? Sha256,
    string? VerificationStatus,
    string? Message,
    string ApplicationVersion)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
}