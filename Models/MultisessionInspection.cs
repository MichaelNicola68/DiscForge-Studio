namespace DiscForgeStudio.Models;

public sealed record MultisessionInspection(
    DateTime Inspected,
    string Drive,
    string Status,
    string FileSystem,
    string MediaType,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    long FreeSectors,
    int SessionCount,
    string FreeSpaceSource,
    bool AppendCandidate,
    int FileCount,
    IReadOnlyList<string> Files,
    string Details);
