using System.Diagnostics;
using System.IO;
using System.Text;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class MultisessionWriterService
{
    private Process? _process;
    public void Cancel() { try { if (_process is { HasExited: false }) _process.Kill(true); } catch { } }

    public async Task AppendAsync(string driveName, IReadOnlyList<CompilationItem> items, bool finalize, bool eject,
        IProgress<(int Percent, string Message)> progress, CancellationToken token)
    {
        var work = Path.Combine(Path.GetTempPath(), "DF", "multi-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(work, "src"); Directory.CreateDirectory(source);
        try
        {
            progress.Report((3, "Bereite neue Sitzung vor..."));
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                var target = Path.Combine(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.FullPath, target, true);
            }
            var script = Path.Combine(work, "append.ps1");
            await File.WriteAllTextAsync(script, Script, Encoding.UTF8, token);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Drive", driveName.TrimEnd('\\'), "-Source", source, "-Finalize", finalize ? "1" : "0", "-Eject", eject ? "1" : "0" }) psi.ArgumentList.Add(arg);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var line = await _process.StandardOutput.ReadLineAsync(token);
                if (line is null) break;
                if (line.StartsWith("PROGRESS:")) { var parts=line.Split(':',3); if(parts.Length==3 && int.TryParse(parts[1],out var p)) progress.Report((p,parts[2])); }
                else if(line.Length>0) progress.Report((-1,line));
            }
            await _process.WaitForExitAsync(token);
            var error = await _process.StandardError.ReadToEndAsync();
            if (_process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Multisession-Schreibvorgang fehlgeschlagen." : error.Trim());
            progress.Report((100, "Neue Sitzung erfolgreich geschrieben."));
        }
        finally { _process=null; try { Directory.Delete(work,true); } catch { } }
    }

    private const string Script = """
param([string]$Drive,[string]$Source,[int]$Finalize=0,[int]$Eject=1)
$ErrorActionPreference='Stop'
function P([int]$n,[string]$m){Write-Output ('PROGRESS:'+$n+':'+$m)}
P 8 'Suche optisches Laufwerk...'
$master=New-Object -ComObject IMAPI2.MsftDiscMaster2;$recorder=$null;$candidates=@();$wanted=($Drive.Trim().TrimEnd('\\').TrimEnd(':')).ToUpperInvariant()
for($i=0;$i -lt $master.Count;$i++){$r=New-Object -ComObject IMAPI2.MsftDiscRecorder2;$r.InitializeDiscRecorder($master.Item($i));$candidates+=$r;foreach($v in @($r.VolumePathNames)){if(([string]$v).Trim().TrimEnd('\\').TrimEnd(':').ToUpperInvariant()-eq $wanted){$recorder=$r;break}};if($null-ne $recorder){break}}
if(($null-eq $recorder)-and($candidates.Count-eq 1)){$recorder=$candidates[0]};if($null-eq $recorder){throw 'IMAPI2 recorder mapping failed.'}
$writer=New-Object -ComObject IMAPI2.MsftDiscFormat2Data;$writer.Recorder=$recorder;$writer.ClientName='DiscForge Studio'
if($writer.MediaHeuristicallyBlank){throw 'Die Disc ist leer. Verwenden Sie fuer eine leere Disc den normalen Brennmodus.'}
if(-not(Test-Path -LiteralPath $Source -PathType Container)){throw 'Temporary source folder missing.'}
$interfaces=$writer.MultisessionInterfaces;if($null-eq $interfaces){throw 'Die Disc ist abgeschlossen oder bietet keine Multisession-Schnittstelle.'}
P 20 'Importiere vorhandene Sitzung...'
$image=New-Object -ComObject IMAPI2FS.MsftFileSystemImage;$image.ChooseImageDefaults($recorder);$image.MultisessionInterfaces=$interfaces
try{$null=$image.ImportFileSystem()}catch{throw ('Vorhandene Sitzung konnte nicht importiert werden: '+$_.Exception.Message)}
$root=[IO.Path]::GetFullPath($Drive.Trim().TrimEnd('\')+'\');$conflicts=@()
Get-ChildItem -LiteralPath $Source -File -Recurse -Force | ForEach-Object {$rel=$_.FullName.Substring($Source.TrimEnd('\\').Length+1);if(Test-Path -LiteralPath (Join-Path $root $rel)){$conflicts+=$rel}}
if($conflicts.Count-gt 0){throw ('Namenskonflikt mit vorhandener Disc-Datei: '+($conflicts -join ', '))}
P 35 'Füge neue Dateien zum importierten Dateisystem hinzu...'
$image.Root.AddTree([IO.Path]::GetFullPath($Source).TrimEnd('\\'),$false)
$result=$image.CreateResultImage();$writer.ForceMediaToBeClosed=([bool]$Finalize)
P 55 'Schreibe neue Sitzung...';$writer.Write($result.ImageStream)
P 95 'Finalisiere Sitzung...';if($Eject-eq 1){$recorder.EjectMedia()};P 100 'Neue Sitzung wurde erfolgreich geschrieben.'
""";
}
