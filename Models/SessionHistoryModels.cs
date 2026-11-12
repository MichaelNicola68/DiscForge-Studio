namespace DiscForgeStudio.Models;

public sealed record SessionHistoryFile(string RelativePath, long Size, string Sha256);

public sealed record SessionHistoryEntry(
    string Id,
    int SessionNumber,
    DateTime WrittenAt,
    string Drive,
    bool Finalized,
    long FreeBytesBefore,
    long? FreeBytesAfter,
    string VerificationStatus,
    DateTime? VerifiedAt,
    int MatchingFiles,
    int FailedFiles,
    int MissingPreviousFiles,
    IReadOnlyList<SessionHistoryFile> NewFiles)
{
    public string DisplayText => $"Sitzung {SessionNumber} · {WrittenAt:yyyy-MM-dd HH:mm} · {NewFiles.Count} Datei(en) · {VerificationStatus}";
}
