using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed record SafetyFinding(CompilationItem Item, string Reason);

public sealed class SafetyScanService
{
    private static readonly HashSet<string> ExactFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "publish", "publish-new", ".git", ".vs", "node_modules"
    };

    private static readonly string[] BackupPrefixes =
    {
        "Backup-before-",
        "Backup-vor-"
    };

    public IReadOnlyList<SafetyFinding> Scan(IEnumerable<CompilationItem> items)
    {
        var findings = new List<SafetyFinding>();
        foreach (var item in items)
        {
            var normalized = item.RelativePath.Replace('/', '\\').TrimStart('\\');
            var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);

            string? reason = null;
            foreach (var segment in segments)
            {
                if (ExactFolders.Contains(segment))
                {
                    reason = $"Projekt-/Buildordner: {segment}";
                    break;
                }

                var prefix = BackupPrefixes.FirstOrDefault(value =>
                    segment.StartsWith(value, StringComparison.OrdinalIgnoreCase));
                if (prefix is not null)
                {
                    reason = $"Sicherungsordner: {segment}";
                    break;
                }
            }

            if (reason is not null)
                findings.Add(new(item, reason));
        }
        return findings;
    }
}
