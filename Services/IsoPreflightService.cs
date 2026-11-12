using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class IsoPreflightService
{
    public IReadOnlyList<string> ValidateBuild(IsoBuildOptions options)
    {
        var errors = new List<string>();
        if (!OperatingSystem.IsWindows()) errors.Add("ISO-Erstellung mit IMAPI ist nur unter Windows verfügbar.");
        if (options.Items.Count == 0) errors.Add("Die Datei-Zusammenstellung ist leer.");
        if (string.IsNullOrWhiteSpace(options.OutputPath)) errors.Add("Kein ISO-Speicherort gewählt.");
        if (string.IsNullOrWhiteSpace(options.VolumeName)) errors.Add("Der Datenträgername ist leer.");
        if (options.FileSystems == IsoFileSystems.None) errors.Add("Kein Dateisystem ausgewählt.");
        var duplicate = options.Items.GroupBy(x => NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) errors.Add($"Doppelter Zielpfad: {duplicate.Key}");
        foreach (var item in options.Items)
        {
            if (!File.Exists(item.SourcePath)) errors.Add($"Quelldatei fehlt: {item.SourcePath}");
            if (Path.IsPathRooted(item.RelativePath) || item.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
                errors.Add($"Unsicherer ISO-Zielpfad: {item.RelativePath}");
        }
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) errors.Add("Der ISO-Zielordner existiert nicht.");
        }
        var sourceBytes = options.Items.Where(x => File.Exists(x.SourcePath)).Sum(x => new FileInfo(x.SourcePath).Length);
        if (options.CapacityLimitBytes is long cap && sourceBytes > cap) errors.Add("Die Quelldaten überschreiten die gewählte Zielkapazität.");
        return errors;
    }

    public IReadOnlyList<string> ValidateBurn(IsoBurnOptions options)
    {
        var errors = new List<string>();
        if (!OperatingSystem.IsWindows()) errors.Add("ISO-Brennen mit IMAPI ist nur unter Windows verfügbar.");
        if (!File.Exists(options.IsoPath)) errors.Add("ISO-Datei wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(options.RecorderUniqueId)) errors.Add("Kein Recorder ausgewählt.");
        return errors;
    }

    private static string NormalizeRelative(string value) => value.Replace('/', '\\').TrimStart('\\');
}
