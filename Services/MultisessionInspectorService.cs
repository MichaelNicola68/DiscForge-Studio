using System.Diagnostics;
using System.Globalization;
using System.IO;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class MultisessionInspectorService
{
    public async Task<MultisessionInspection> InspectAsync(string driveName, CancellationToken token)
    {
        var normalizedDrive = driveName.Trim().TrimEnd('\\');
        var driveRoot = normalizedDrive.EndsWith(":", StringComparison.Ordinal) ? normalizedDrive + "\\" : normalizedDrive;
        DriveInfo driveInfo;
        try { driveInfo = new DriveInfo(driveRoot); }
        catch { throw new InvalidOperationException($"Das Laufwerk {normalizedDrive} konnte nicht geprüft werden."); }
        if (!driveInfo.IsReady)
            throw new InvalidOperationException($"Kein optisches Medium im Laufwerk {normalizedDrive} erkannt. Bitte eine beschriebene Disc einlegen und die Analyse erneut starten.");

        var work = Path.Combine(Path.GetTempPath(), "DF", "inspect-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "inspect.ps1");
            await File.WriteAllTextAsync(script, Script, token);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Drive", driveName.TrimEnd('\\') }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");
            var output = await process.StandardOutput.ReadToEndAsync(token);
            var error = await process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
            {
                var diagnostic = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                const string noMedia = "Kein optisches Medium im Laufwerk";
                const string noMultisession = "Das Medium stellt keine Multisession-Schnittstelle bereit oder ist bereits abgeschlossen.";
                if (diagnostic.Contains(noMedia, StringComparison.OrdinalIgnoreCase))
                {
                    var start = diagnostic.IndexOf(noMedia, StringComparison.OrdinalIgnoreCase);
                    var end = diagnostic.IndexOf(Environment.NewLine, start);
                    throw new InvalidOperationException(end > start ? diagnostic[start..end].Trim() : diagnostic[start..].Trim());
                }
                if (diagnostic.Contains(noMultisession, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(noMultisession);
                var firstLine = diagnostic.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(firstLine) ? "Die Multisession-Analyse konnte nicht abgeschlossen werden." : firstLine.Trim());
            }

            var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("FILE|", StringComparison.Ordinal)) files.Add(line[5..]);
                else { var split=line.IndexOf('|'); if(split>0) values[line[..split]]=line[(split+1)..]; }
            }
            long.TryParse(values.GetValueOrDefault("FREEBYTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeBytes);
            long.TryParse(values.GetValueOrDefault("FREESECTORS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeSectors);
            long.TryParse(values.GetValueOrDefault("TOTALBYTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalBytes);
            long.TryParse(values.GetValueOrDefault("USEDBYTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedBytes);
            int.TryParse(values.GetValueOrDefault("SESSIONS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionCount);
            bool.TryParse(values.GetValueOrDefault("APPEND"), out var append);
            return new(DateTime.Now, driveName, values.GetValueOrDefault("STATUS","Unbekannt"), values.GetValueOrDefault("FILESYSTEM","-"), values.GetValueOrDefault("MEDIATYPE","-"), totalBytes, usedBytes, freeBytes, freeSectors, sessionCount, values.GetValueOrDefault("FREESOURCE","Unbekannt"), append, files.Count, files, values.GetValueOrDefault("DETAILS", ""));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private const string Script = """
param([string]$Drive)
$ErrorActionPreference='Stop'
$master=New-Object -ComObject IMAPI2.MsftDiscMaster2
$recorder=$null;$candidates=@();$wanted=($Drive.Trim().TrimEnd('\\').TrimEnd(':')).ToUpperInvariant()
for($i=0;$i -lt $master.Count;$i++){
 $r=New-Object -ComObject IMAPI2.MsftDiscRecorder2;$r.InitializeDiscRecorder($master.Item($i));$candidates+=$r
 foreach($v in @($r.VolumePathNames)){if(([string]$v).Trim().TrimEnd('\\').TrimEnd(':').ToUpperInvariant() -eq $wanted){$recorder=$r;break}}
 if($null-ne $recorder){break}
}
if(($null-eq $recorder)-and($candidates.Count-eq 1)){$recorder=$candidates[0]}
if($null-eq $recorder){throw 'IMAPI2 recorder mapping failed.'}
$writer=New-Object -ComObject IMAPI2.MsftDiscFormat2Data;$writer.Recorder=$recorder;$writer.ClientName='DiscForge Studio'
if($writer.MediaHeuristicallyBlank){throw 'Die Disc ist leer. Fuer den Inspector ist eine bereits beschriebene, offene Disc erforderlich.'}
$image=New-Object -ComObject IMAPI2FS.MsftFileSystemImage;$image.ChooseImageDefaults($recorder)
$interfaces=$writer.MultisessionInterfaces
if($null-eq $interfaces){throw 'Das Medium stellt keine Multisession-Schnittstelle bereit oder ist bereits abgeschlossen.'}
$image.MultisessionInterfaces=$interfaces
try{$imported=$image.ImportFileSystem()}catch{throw ('Vorhandene Sitzung konnte nicht importiert werden: '+$_.Exception.Message)}
$fs=switch([int]$imported){1{'ISO9660'}2{'Joliet'}4{'UDF'}default{[string]$imported}}
$writerSectors=[int64]$writer.FreeSectorsOnMedia
$freeSectors=0L
$freeSource='IDiscFormat2Data fallback'
foreach($iface in @($interfaces)){
  try{
    $candidate=[int64]$iface.FreeSectorsOnMedia
    if($candidate -gt 0){$freeSectors=$candidate;$freeSource='IMultisessionSequential';break}
  }catch{}
}
if($freeSectors -le 0){$freeSectors=$writerSectors}
$free=$freeSectors*2048L
$totalSectors=[int64]$writer.TotalSectorsOnMedia
$total=$totalSectors*2048L
$used=[Math]::Max(0,$total-$free)
$sessionCount=@($interfaces).Count
$media=[string]$writer.CurrentPhysicalMediaType
Write-Output 'STATUS|Vorhandene Sitzung erfolgreich importiert'
Write-Output ('FILESYSTEM|'+$fs)
Write-Output ('MEDIATYPE|'+$media)
Write-Output ('TOTALBYTES|'+$total)
Write-Output ('USEDBYTES|'+$used)
Write-Output ('FREEBYTES|'+$free)
Write-Output ('SESSIONS|'+$sessionCount)
Write-Output ('FREESECTORS|'+$freeSectors)
Write-Output ('FREESOURCE|'+$freeSource)
Write-Output ('WRITERSECTORS|'+$writerSectors)
Write-Output 'APPEND|True'
Write-Output 'DETAILS|Schreibgeschuetzte Analyse erfolgreich. Noch keine Daten wurden angehaengt.'
$root=[IO.Path]::GetFullPath($Drive.Trim().TrimEnd('\')+'\')
if(Test-Path -LiteralPath $root){Get-ChildItem -LiteralPath $root -File -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $full=[IO.Path]::GetFullPath($_.FullName);if($full.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){$relative=$full.Substring($root.Length).TrimStart('\');if($relative.Length-gt 0){Write-Output ('FILE|'+$relative)}} }}
""";
}
