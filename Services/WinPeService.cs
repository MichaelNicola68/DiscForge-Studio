using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiscForgeStudio.Services;

public sealed record WinPeToolStatus(bool Ready, string? CopypePath, string? MakeWinPeMediaPath, string Message);
public sealed record WinPeBuildResult(bool Success, string IsoPath, long Bytes, string? Sha256, string Message);

public sealed class WinPeService
{
    private Process? _process;
    public void Cancel() { try { if (_process is { HasExited: false }) _process.Kill(true); } catch { } }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public WinPeToolStatus DetectTools()
    {
        foreach (var root in CandidateRoots().Where(Directory.Exists))
        {
            var copype = FindTool(root, "copype.cmd");
            var make = FindTool(root, "MakeWinPEMedia.cmd");
            var env = FindTool(root, "DandISetEnv.bat");
            if (copype is not null && make is not null && env is not null)
                return new(true, copype, make, "Windows ADK und WinPE-Add-on wurden erkannt.");
        }
        return new(false, null, null, "Windows ADK oder das WinPE-Add-on wurde nicht vollständig gefunden.");
    }

    public async Task PrepareAsync(string workingDirectory, IProgress<(int Percent, string Message)> progress, CancellationToken token)
    {
        var tools = ResolveTools();
        EnsureAdministrator();
        var full = Path.GetFullPath(workingDirectory);
        if (Directory.Exists(full))
        {
            if (Directory.EnumerateFileSystemEntries(full).Any())
                throw new InvalidOperationException("Der WinPE-Arbeitsordner ist nicht leer. Bitte einen leeren Ordner wählen.");

            progress.Report((3, "Leerer Auswahlordner wird für CopyPE vorbereitet"));
            Directory.Delete(full, false);
        }
        progress.Report((5, "WinPE-Werkzeuge werden initialisiert"));
        await RunAdkBatchAsync(tools.Environment, tools.Copype, $"amd64 \"{full}\"", progress, 10, 65, token).ConfigureAwait(false);
        var bootWim = Path.Combine(full, "media", "sources", "boot.wim");
        if (!File.Exists(bootWim)) throw new InvalidOperationException("CopyPE wurde beendet, aber media\\sources\\boot.wim fehlt.");
        progress.Report((70, "WinPE-Arbeitssatz ist bereit"));
    }

    public async Task CopyExtrasAsync(string workingDirectory, string extrasDirectory, IProgress<(int Percent, string Message)> progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(extrasDirectory) || !Directory.Exists(extrasDirectory)) return;
        ValidateSeparateDirectories(workingDirectory, extrasDirectory);
        var destinationRoot = Path.Combine(Path.GetFullPath(workingDirectory), "media", "DiscForgeTools");
        Directory.CreateDirectory(destinationRoot);
        var files = Directory.EnumerateFiles(extrasDirectory, "*", SearchOption.AllDirectories).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(extrasDirectory, files[i]);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(files[i], destination, true);
            progress.Report((70 + (int)Math.Round((i + 1d) / Math.Max(1, files.Count) * 10d), $"Zusatzdateien: {i + 1}/{files.Count} · {relative}"));
            await Task.Yield();
        }
    }

    public async Task<WinPeBuildResult> CreateIsoAsync(string workingDirectory, string isoPath, IProgress<(int Percent, string Message)> progress, CancellationToken token)
    {
        var tools = ResolveTools();
        EnsureAdministrator();
        var work = Path.GetFullPath(workingDirectory);
        if (!File.Exists(Path.Combine(work, "media", "sources", "boot.wim")))
            throw new InvalidOperationException("Der WinPE-Arbeitssatz ist unvollständig. Bitte zuerst WINPE VORBEREITEN ausführen.");
        var output = Path.GetFullPath(isoPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        progress.Report((82, "Startfähige WinPE-ISO wird erstellt"));
        await RunAdkBatchAsync(tools.Environment, tools.Make, $"/ISO /F \"{work}\" \"{output}\"", progress, 83, 94, token).ConfigureAwait(false);
        if (!File.Exists(output)) throw new InvalidOperationException("MakeWinPEMedia hat keine ISO-Datei erzeugt.");
        var bytes = new FileInfo(output).Length;
        progress.Report((95, "SHA-256 der WinPE-ISO wird berechnet"));
        await using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        progress.Report((100, "Startfähige WinPE-ISO ist bereit"));
        return new(true, output, bytes, sha, "Startfähige Windows-PE-ISO wurde erfolgreich erstellt.");
    }


    public static void ValidateSeparateDirectories(string workingDirectory, string extrasDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(extrasDirectory)) return;

        var work = NormalizeDirectory(workingDirectory);
        var extras = NormalizeDirectory(extrasDirectory);
        if (string.Equals(work, extras, StringComparison.OrdinalIgnoreCase) ||
            work.StartsWith(extras + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            extras.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WinPE-Arbeitsordner und Zusatzordner müssen getrennt sein und dürfen nicht ineinander liegen.");
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private async Task RunAdkBatchAsync(string environmentPath, string toolPath, string arguments, IProgress<(int Percent, string Message)> progress, int start, int end, CancellationToken token)
    {
        var wrapper = Path.Combine(Path.GetTempPath(), "DiscForge-WinPE-" + Guid.NewGuid().ToString("N") + ".cmd");
        var script = "@echo off\r\n" +
                     "chcp 65001 >nul\r\n" +
                     $"call \"{environmentPath}\" >nul\r\n" +
                     "if errorlevel 1 exit /b %errorlevel%\r\n" +
                     $"call \"{toolPath}\" {arguments}\r\n" +
                     "exit /b %errorlevel%\r\n";
        await File.WriteAllTextAsync(wrapper, script, new UTF8Encoding(false), token).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(toolPath)! };
            psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(wrapper);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Das Windows-PE-Werkzeug konnte nicht gestartet werden.");
            var errors = new StringBuilder(); var lineCount = 0;
            var stdout = Task.Run(async () => { while (await _process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line) if (!string.IsNullOrWhiteSpace(line)) { lineCount++; progress.Report((Math.Min(end - 1, start + Math.Min(8, lineCount)), line.Trim())); } }, token);
            var stderr = Task.Run(async () => { while (await _process.StandardError.ReadLineAsync(token).ConfigureAwait(false) is { } line) if (!string.IsNullOrWhiteSpace(line)) errors.AppendLine(line.Trim()); }, token);
            using var registration = token.Register(Cancel);
            await Task.WhenAll(_process.WaitForExitAsync(token), stdout, stderr).ConfigureAwait(false);
            var exit = _process.ExitCode; _process.Dispose(); _process = null;
            if (exit != 0) throw new InvalidOperationException(errors.Length == 0 ? $"Windows-PE-Werkzeug fehlgeschlagen (Code {exit})." : errors.ToString().Trim());
            progress.Report((end, Path.GetFileNameWithoutExtension(toolPath) + " erfolgreich abgeschlossen"));
        }
        finally { try { File.Delete(wrapper); } catch { } }
    }

    private static (string Copype, string Make, string Environment) ResolveTools()
    {
        foreach (var root in CandidateRoots().Where(Directory.Exists))
        {
            var copype = FindTool(root, "copype.cmd"); var make = FindTool(root, "MakeWinPEMedia.cmd"); var env = FindTool(root, "DandISetEnv.bat");
            if (copype is not null && make is not null && env is not null) return (copype, make, env);
        }
        throw new InvalidOperationException("Windows ADK, WinPE-Add-on oder DandISetEnv.bat wurde nicht gefunden.");
    }
    private static IEnumerable<string> CandidateRoots()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(x86)) yield return Path.Combine(x86, "Windows Kits", "10", "Assessment and Deployment Kit");
        if (!string.IsNullOrWhiteSpace(pf)) yield return Path.Combine(pf, "Windows Kits", "10", "Assessment and Deployment Kit");
    }
    private static string? FindTool(string root, string name) { try { return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault(); } catch { return null; } }
    private static void EnsureAdministrator() { if (!IsAdministrator()) throw new InvalidOperationException("DiscForge muss für Windows PE als Administrator gestartet werden."); }
}
