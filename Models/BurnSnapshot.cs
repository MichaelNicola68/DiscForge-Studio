namespace DiscForgeStudio.Models;

public sealed record BurnSnapshotFile(
    string FullPath,
    string RelativePath,
    long Size,
    string Sha256);

public sealed record BurnSnapshot(
    string Version,
    DateTime Created,
    string DiscLabel,
    string Drive,
    IReadOnlyList<BurnSnapshotFile> Files);
