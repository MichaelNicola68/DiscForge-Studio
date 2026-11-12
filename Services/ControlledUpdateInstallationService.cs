using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed record ControlledInstallationResult(bool Success, bool RolledBack, string BackupPath,
    IReadOnlyList<string> Checks, IReadOnlyList<string> Errors);

public sealed class ControlledUpdateInstallationService
{
    private const string AllowedProfile = "ControlledInstallationTest";
    private const string RollbackProfile = "ControlledRollbackTest";
    private const string AllowedPrefix = "Documentation/UpdateTests/";

    public ControlledInstallationResult Install(string packagePath, UpdatePackageManifest manifest)
    {
        var checks = new List<string>(); var errors = new List<string>();
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var normalizedPatchId = (manifest.PatchId ?? string.Empty).Trim();
        if (normalizedPatchId.Length == 0)
            return new(false, false, string.Empty, checks, new[] { "Patch-ID fehlt. Installation wurde vor jeder Dateiänderung abgebrochen." });
        var markerDirectory = Path.Combine(root, "Update-Backups", "Installed-Patches");
        var markerPath = Path.Combine(markerDirectory, Safe(normalizedPatchId) + ".installed.txt");
        if (File.Exists(markerPath))
            return new(false, false, string.Empty, checks, new[] { "Wiederholungsschutz: Dieses Update wurde bereits erfolgreich installiert: " + normalizedPatchId });
        var backup = Path.Combine(root, "Update-Backups", Safe(normalizedPatchId) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var journal = new List<(string Operation,string Target,string? Backup)>();
        var rolledBack = false;
        try
        {
            var profile = (manifest.TestProfile ?? string.Empty).Trim();
            var patchId = normalizedPatchId;
            var rollbackTest = string.Equals(profile, RollbackProfile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(patchId, "DEV16-ROLLBACK-TEST-003", StringComparison.OrdinalIgnoreCase);
            var controlledTest = string.Equals(profile, AllowedProfile, StringComparison.OrdinalIgnoreCase);
            if (!rollbackTest && !controlledTest)
                throw new InvalidOperationException($"Dieses Paket ist nicht freigegeben. Profil: '{profile}', Patch-ID: '{patchId}', Dienst: Rollback-V3.");
            if (manifest.Files.Count == 0) throw new InvalidOperationException("Das Paket enthält keine Dateioperationen.");
            foreach (var f in manifest.Files)
                if (!f.Path.Replace('\\','/').StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Produktive Dateioperation gesperrt: " + f.Path);

            Directory.CreateDirectory(backup);
            using var archive = ZipFile.OpenRead(packagePath);
            var archiveRoot = archive.Entries.Select(e => Normalize(e.FullName).Split('/',2)[0]).First(x => x.Length > 0);

            foreach (var file in manifest.Files)
            {
                var target = SafeTarget(root, file.Path);
                var relative = file.Path.Replace('/','\\');
                if (file.Operation.Equals("Add", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(target)) throw new IOException("ADD-Zieldatei existiert bereits: " + file.Path);
                    var staged = ExtractVerified(archive, archiveRoot, file, backup);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(staged, target, false); journal.Add(("Add",target,null));
                    VerifyFile(target,file.NewSha256); checks.Add("ADD installiert und SHA-256 bestätigt: " + file.Path);
                }
                else if (file.Operation.Equals("Replace", StringComparison.OrdinalIgnoreCase))
                {
                    VerifyFile(target,file.OldSha256);
                    var saved = Path.Combine(backup,"original",relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target,saved,true);
                    var staged = ExtractVerified(archive,archiveRoot,file,backup);
                    File.Copy(staged,target,true); journal.Add(("Replace",target,saved));
                    VerifyFile(target,file.NewSha256); checks.Add("REPLACE installiert und SHA-256 bestätigt: " + file.Path);
                    if (rollbackTest) throw new InvalidOperationException("Absichtlich ausgelöster DEV16-Rollback-Testfehler nach ADD und REPLACE.");
                }
                else if (file.Operation.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                {
                    VerifyFile(target,file.OldSha256);
                    var saved = Path.Combine(backup,"original",relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target,saved,true);
                    File.Delete(target); journal.Add(("Delete",target,saved));
                    if (File.Exists(target)) throw new IOException("DELETE fehlgeschlagen: " + file.Path);
                    checks.Add("DELETE ausgeführt und bestätigt: " + file.Path);
                }
                else throw new InvalidOperationException("Unbekannte Operation: " + file.Operation);
            }
            var reportPath = Path.Combine(backup,"install-report.txt");
            File.WriteAllLines(reportPath, new[] { "SUCCESS", normalizedPatchId, DateTime.Now.ToString("O") }.Concat(checks), Encoding.UTF8);
            Directory.CreateDirectory(markerDirectory);
            var temporaryMarker = markerPath + ".tmp";
            File.WriteAllLines(temporaryMarker, new[] { normalizedPatchId, DateTime.Now.ToString("O"), reportPath }, Encoding.UTF8);
            File.Move(temporaryMarker, markerPath, true);
            checks.Add("Installationsbericht gespeichert: " + reportPath);
            checks.Add("Wiederholungsschutz aktiviert: " + markerPath);
            return new(true,false,backup,checks,errors);
        }
        catch(Exception ex)
        {
            errors.Add(ex.Message);
            try
            {
                if (journal.Count == 0)
                {
                    errors.Add("Rollback wurde nicht ausgeführt, da noch keine Dateiänderung begonnen hatte.");
                    return new(false, false, backup, checks, errors);
                }
                foreach(var item in journal.AsEnumerable().Reverse())
                {
                    if(item.Operation=="Add") { if(File.Exists(item.Target)) File.Delete(item.Target); }
                    else if(item.Backup is not null) { Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!); File.Copy(item.Backup,item.Target,true); }
                }
                foreach (var file in manifest.Files)
                {
                    var target = SafeTarget(root, file.Path);
                    if (file.Operation.Equals("Add", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(target)) throw new IOException("Rollback-Prüfung fehlgeschlagen, ADD-Datei existiert noch: " + file.Path);
                    }
                    else VerifyFile(target, file.OldSha256);
                }
                rolledBack=true;
                checks.Add("Automatisches Rollback vollständig ausgeführt.");
                checks.Add("Rollback-Nachprüfung bestanden: ADD entfernt, REPLACE und DELETE im Ausgangszustand.");
            }
            catch(Exception rollbackEx){ errors.Add("Rollback-Fehler: "+rollbackEx.Message); }
            try { Directory.CreateDirectory(backup); File.WriteAllLines(Path.Combine(backup,"install-report.txt"),new[] { "FAILED", normalizedPatchId, DateTime.Now.ToString("O") }.Concat(errors),Encoding.UTF8); } catch { }
            return new(false,rolledBack,backup,checks,errors);
        }
    }

    private static string ExtractVerified(ZipArchive archive,string root,UpdatePackageFile file,string backup)
    {
        var wanted=Normalize(root+"/payload/"+file.Path);
        var entry=archive.Entries.FirstOrDefault(e=>Normalize(e.FullName).Equals(wanted,StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("Payload fehlt: "+file.Path);
        var staged=Path.Combine(backup,"staged",file.Path.Replace('/','\\')); Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        using(var input=entry.Open()) using(var output=File.Create(staged)) input.CopyTo(output);
        VerifyFile(staged,file.NewSha256); return staged;
    }
    private static void VerifyFile(string path,string expected)
    {
        if(!File.Exists(path)) throw new FileNotFoundException("Datei fehlt.",path);
        using var s=File.OpenRead(path); var actual=Convert.ToHexString(SHA256.HashData(s));
        if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 stimmt nicht: "+path);
    }
    private static string SafeTarget(string root,string relative)
    {
        var target=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsicherer Zielpfad: "+relative);
        return target;
    }
    private static string Normalize(string value)=>value.Replace('\\','/').TrimStart('/');
    private static string Safe(string value)=>string.Concat(value.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
}
