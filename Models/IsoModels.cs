using System;
using System.Collections.Generic;

namespace DiscForgeStudio.Models;

[Flags]
public enum IsoFileSystems { None = 0, Iso9660 = 1, Joliet = 2, Udf = 4 }

public sealed record IsoSourceItem(string SourcePath, string RelativePath);

public sealed record IsoBuildOptions(
    IReadOnlyList<IsoSourceItem> Items,
    string OutputPath,
    string VolumeName,
    IsoFileSystems FileSystems,
    long? CapacityLimitBytes = null,
    bool CalculateSha256 = true);

public sealed record IsoBuildResult(
    bool Success, string OutputPath, long ImageBytes, string? Sha256,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Message);

public sealed record IsoBurnOptions(
    string IsoPath, string RecorderUniqueId, bool CloseMedia = true,
    bool EjectAfterWrite = true, bool CalculateImageSha256 = true);

public sealed record IsoBurnResult(
    bool Success, string IsoPath, long ImageBytes, string? ImageSha256,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Message);

public sealed record IsoInspectionResult(
    bool Success, string Path, long Bytes, string Sha256,
    DateTimeOffset InspectedAt, string Message);

public sealed record IsoProgress(string Phase, int Percent, long ProcessedBytes, long TotalBytes, string? CurrentItem = null);
