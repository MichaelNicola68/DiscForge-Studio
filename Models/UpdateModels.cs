namespace DiscForgeStudio.Models;

public sealed record UpdatePackageFile(
    string Path,
    string OldSha256,
    string NewSha256,
    string Operation);

public sealed record UpdatePackageManifest(
    int SchemaVersion,
    string PatchId,
    string SourceVersion,
    string TargetVersion,
    string Title,
    string RiskLevel,
    bool RequiresFullTest,
    IReadOnlyList<string> AffectedModules,
    IReadOnlyList<UpdatePackageFile> Files,
    string TestProfile,
    string? Description = null,
    IReadOnlyList<string>? NewFeatures = null,
    IReadOnlyList<string>? Improvements = null);

public sealed record UpdateValidationResult(
    bool Success,
    string Summary,
    UpdatePackageManifest? Manifest,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Errors);
