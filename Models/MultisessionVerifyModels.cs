namespace DiscForgeStudio.Models;

public sealed record MultisessionSnapshotFile(string FullPath, string RelativePath, long Size, string Sha256);

public sealed record MultisessionSnapshot(
    string Version,
    DateTime Created,
    string Drive,
    long FreeBytesBefore,
    IReadOnlyList<string> PreviousFiles,
    IReadOnlyList<MultisessionSnapshotFile> NewFiles,
    bool Finalized);

public sealed record MultisessionVerifyFile(string RelativePath, string Status, string ExpectedHash, string DiscHash, long ExpectedSize, long DiscSize);

public sealed record MultisessionVerifyResult(
    DateTime Started,
    DateTime Finished,
    DateTime SnapshotCreated,
    string Drive,
    int NewFilesTotal,
    int NewFilesMatching,
    int NewFilesFailed,
    int PreviousFilesMissing,
    long FreeBytesBefore,
    long FreeBytesAfter,
    IReadOnlyList<MultisessionVerifyFile> NewFiles,
    IReadOnlyList<string> MissingPreviousFiles)
{
    public bool Success => NewFilesFailed == 0 && PreviousFilesMissing == 0 && NewFilesTotal > 0;
}
