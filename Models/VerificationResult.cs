namespace DiscForgeStudio.Models;

public sealed record VerificationFileResult(
    string RelativePath,
    string DiscStatus,
    string OriginalStatus,
    string SnapshotHash,
    string CurrentOriginalHash,
    string DiscHash,
    long SnapshotSize,
    long CurrentOriginalSize,
    long DiscSize);

public sealed record VerificationResult(
    DateTime Started,
    DateTime Finished,
    string Drive,
    DateTime SnapshotCreated,
    int TotalFiles,
    int MatchingFiles,
    int FailedFiles,
    int ChangedOriginalFiles,
    IReadOnlyList<VerificationFileResult> Files)
{
    public bool Success => FailedFiles == 0 && TotalFiles > 0;
}
