using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class IsoImageService
{
    private readonly IsoPreflightService _preflight = new();
    private readonly IsoHashService _hash = new();

    public async Task<IsoBuildResult> CreateAsync(IsoBuildOptions options, IProgress<IsoProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var errors = _preflight.ValidateBuild(options);
        if (errors.Count > 0) return new(false, options.OutputPath, 0, null, started, DateTimeOffset.Now, string.Join(Environment.NewLine, errors));

        var output = Path.GetFullPath(options.OutputPath);
        if (!output.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) output += ".iso";
        var partial = output + ".partial";
        var outputDirectory = Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Der ISO-Zielordner konnte nicht bestimmt werden.");
        if (!Directory.Exists(outputDirectory)) throw new DirectoryNotFoundException($"ISO-Zielordner nicht gefunden: {outputDirectory}");
        var stage = Path.Combine(outputDirectory, ".DiscForge-Iso-Stage-" + Guid.NewGuid().ToString("N"));
        var phase = "Initialisierung";
        dynamic? image = null; dynamic? result = null; object? imageStreamObject = null;
        try
        {
            phase = "Arbeitsordner anlegen";
            Directory.CreateDirectory(stage);
            stage = Path.GetFullPath(stage);
            if (!Directory.Exists(stage)) throw new DirectoryNotFoundException($"ISO-Arbeitsordner konnte nicht angelegt werden: {stage}");
            long total = 0, done = 0;
            foreach (var item in options.Items) total += new FileInfo(item.SourcePath).Length;
            phase = "Quelldateien vorbereiten";
            foreach (var item in options.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(stage, relative));
                if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Ungültiger ISO-Zielpfad: {item.RelativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.SourcePath, target, true);
                done += new FileInfo(item.SourcePath).Length;
                progress?.Report(new("Dateien vorbereiten", total == 0 ? 0 : (int)(done * 30 / total), done, total, item.RelativePath));
            }

            phase = "IMAPI-Dateisystem initialisieren";
            var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") ?? throw new PlatformNotSupportedException("IMAPI2FS ist nicht verfügbar.");
            image = Activator.CreateInstance(type)!;
            image.FileSystemsToCreate = (int)options.FileSystems;
            image.VolumeName = options.VolumeName;
            if (!Directory.Exists(stage)) throw new DirectoryNotFoundException($"ISO-Arbeitsordner vor AddTree nicht gefunden: {stage}");
            progress?.Report(new("Dateibaum einlesen", 32, done, total, stage));
            phase = "Dateibaum in IMAPI übernehmen";
            image.Root.AddTree(stage, false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Image erzeugen", 35, done, total));
            phase = "Image-Datenstrom erzeugen";
            result = image.CreateResultImage();
            imageStreamObject = result.ImageStream;
            var stream = (IStream)imageStreamObject;
            stream.Stat(out var stat, 1);
            var bytes = stat.cbSize;
            if (options.CapacityLimitBytes is long cap && bytes > cap) throw new InvalidOperationException("Das erzeugte Image überschreitet die gewählte Zielkapazität.");
            phase = "ISO-Zieldatei vorbereiten";
            if (File.Exists(partial)) File.Delete(partial);
            phase = "ISO-Datenstrom speichern";
            await Task.Run(() => CopyComStream(stream, partial, bytes, progress, cancellationToken), cancellationToken);
            if (File.Exists(output)) File.Delete(output);
            phase = "ISO-Datei abschließen";
            File.Move(partial, output);
            var inspection = options.CalculateSha256 ? await _hash.InspectAsync(output, cancellationToken) : null;
            progress?.Report(new("Fertig", 100, bytes, bytes));
            return new(true, output, bytes, inspection?.Sha256, started, DateTimeOffset.Now, "ISO wurde erfolgreich erstellt.");
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(partial)) File.Delete(partial);
            return new(false, output, 0, null, started, DateTimeOffset.Now, "ISO-Erstellung wurde abgebrochen.");
        }
        catch (Exception ex)
        {
            if (File.Exists(partial)) File.Delete(partial);
            return new(false, output, 0, null, started, DateTimeOffset.Now, $"Phase: {phase} | {ex.Message}");
        }
        finally
        {
            ComRelease.Final(imageStreamObject); ComRelease.Final(result); ComRelease.Final(image);
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
        }
    }

    private static void CopyComStream(IStream input, string output, long total, IProgress<IsoProgress>? progress, CancellationToken token)
    {
        const int size = 1024 * 1024;
        var buffer = new byte[size];
        var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
        long done = 0;
        try
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, size);
            while (done < total)
            {
                token.ThrowIfCancellationRequested();
                input.Read(buffer, (int)Math.Min(size, total - done), readPtr);
                var read = Marshal.ReadInt32(readPtr);
                if (read <= 0) break;
                file.Write(buffer, 0, read); done += read;
                progress?.Report(new("ISO speichern", 35 + (int)(done * 60 / Math.Max(1, total)), done, total));
            }
            file.Flush(true);
            if (done != total) throw new EndOfStreamException($"Image-Datenstrom unvollständig: {done} von {total} Bytes.");
        }
        finally { Marshal.FreeCoTaskMem(readPtr); }
    }
}
