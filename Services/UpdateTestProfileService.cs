using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class UpdateTestProfileService
{
    public IReadOnlyList<string> Resolve(UpdatePackageManifest manifest)
    {
        var tests = new List<string> { "Release-Build", "Programmstart", "Update-Center öffnen" };
        foreach (var module in manifest.AffectedModules ?? Array.Empty<string>())
        {
            if (module.Contains("SessionHistory", StringComparison.OrdinalIgnoreCase))
                tests.AddRange(["Sitzungsverlauf laden", "Sitzungsbericht erzeugen"]);
            if (module.Contains("Compilation", StringComparison.OrdinalIgnoreCase))
                tests.AddRange(["Dateien und Ordner hinzufügen", "Projekt speichern und laden"]);
            if (module.Contains("Iso", StringComparison.OrdinalIgnoreCase))
                tests.AddRange(["ISO-Vorprüfung", "ISO-Modul öffnen"]);
            if (module.Contains("DiscImage", StringComparison.OrdinalIgnoreCase))
                tests.AddRange(["Disc-zu-Image-Vorprüfung", "Berichtsfunktion"]);
            if (module.Contains("Burn", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("Verify", StringComparison.OrdinalIgnoreCase) ||
                module.Contains("Multisession", StringComparison.OrdinalIgnoreCase))
                tests.Add("Echter Mediumtest erforderlich");
        }
        if (manifest.RequiresFullTest || string.Equals(manifest.RiskLevel, "High", StringComparison.OrdinalIgnoreCase))
            tests.Add("Vollständiger Regressionstest erforderlich");
        return tests.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
