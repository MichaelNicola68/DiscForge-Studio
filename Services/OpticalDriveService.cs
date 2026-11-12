using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class OpticalDriveService
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    public IReadOnlyList<OpticalDriveInfo> Scan() => DriveInfo.GetDrives()
        .Where(d => d.DriveType == DriveType.CDRom)
        .Select(ReadDrive)
        .ToList();

    private static OpticalDriveInfo ReadDrive(DriveInfo drive)
    {
        if (!drive.IsReady)
        {
            var imapi = TryReadBlankOrWritableMedium(drive.Name);
            if (imapi is not null) return imapi;
            return new(drive.Name, $"Optisches Laufwerk {drive.Name.TrimEnd('\\')}", false, "-", "-", "Kein Medium", 0, 0, false, "Nicht verfuegbar", "Nicht geprueft", "Bitte einen Datentraeger einlegen.", "WARTET", Yellow);
        }

        try
        {
            var total = drive.TotalSize;
            var imapiMedia = TryReadImapiMediaType(drive.Name);
            var media = imapiMedia ?? (total switch
            {
                <= 900_000_000 => "CD",
                <= 9_000_000_000 => "DVD",
                _ => "Blu-ray oder groesseres optisches Medium"
            });
            var fileSystem = Empty(drive.DriveFormat);
            var free = drive.AvailableFreeSpace;
            var writable = free > 0;
            var freeDisplay = writable ? FormatBytes(free) : "Nicht beschreibbar / abgeschlossen";
            var protection = fileSystem.Equals("CDFS", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("UDF", StringComparison.OrdinalIgnoreCase)
                ? "Kein eindeutiger Schutz feststellbar"
                : "Unbekannte oder besondere Struktur";
            return new(drive.Name, $"{media} in {drive.Name.TrimEnd('\\')}", true, Empty(drive.VolumeLabel), fileSystem, media, total, free, writable, freeDisplay, protection, "Medium wurde erfolgreich gelesen.", "BEREIT", Green);
        }
        catch (Exception ex)
        {
            return new(drive.Name, $"Optisches Laufwerk {drive.Name.TrimEnd('\\')}", true, "Nicht lesbar", "Unbekannt", "Unbekannt", 0, 0, false, "Nicht verfuegbar", "Analyse nicht moeglich", ex.Message, "FEHLER", Red);
        }
    }

    private static string? TryReadImapiMediaType(string driveName)
    {
        dynamic? master = null;
        dynamic? recorder = null;
        dynamic? format = null;
        try
        {
            var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2");
            var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2");
            var formatType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data");
            if (masterType is null || recorderType is null || formatType is null) return null;
            master = Activator.CreateInstance(masterType)!;
            var wanted = NormalizeDrive(driveName);
            for (var index = 0; index < (int)master.Count; index++)
            {
                recorder = Activator.CreateInstance(recorderType)!;
                recorder.InitializeDiscRecorder((string)master.Item(index));
                var paths = ReadStrings((object)recorder.VolumePathNames);
                if (!paths.Any(path => NormalizeDrive(path).Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    Release(recorder); recorder = null; continue;
                }
                format = Activator.CreateInstance(formatType)!;
                format.Recorder = recorder;
                format.ClientName = "DiscForge Studio";
                return PhysicalMediaName(Convert.ToInt32(format.CurrentPhysicalMediaType));
            }
        }
        catch { return null; }
        finally { Release(format); Release(recorder); Release(master); }
        return null;
    }
    private static OpticalDriveInfo? TryReadBlankOrWritableMedium(string driveName)
    {
        dynamic? master = null;
        dynamic? recorder = null;
        dynamic? format = null;
        try
        {
            var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2");
            var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2");
            var formatType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data");
            if (masterType is null || recorderType is null || formatType is null) return null;
            master = Activator.CreateInstance(masterType)!;
            var wanted = NormalizeDrive(driveName);
            for (var index = 0; index < (int)master.Count; index++)
            {
                recorder = Activator.CreateInstance(recorderType)!;
                recorder.InitializeDiscRecorder((string)master.Item(index));
                var paths = ReadStrings((object)recorder.VolumePathNames);
                if (!paths.Any(path => NormalizeDrive(path).Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    Release(recorder); recorder = null; continue;
                }
                format = Activator.CreateInstance(formatType)!;
                format.Recorder = recorder;
                format.ClientName = "DiscForge Studio";
                var recorderSupported = (bool)format.IsRecorderSupported(recorder);
                var mediaSupported = (bool)format.IsCurrentMediaSupported(recorder);
                var type = Convert.ToInt32(format.CurrentPhysicalMediaType);
                var status = Convert.ToInt32(format.CurrentMediaStatus);
                var heuristicBlank = (bool)format.MediaHeuristicallyBlank;
                var physicalBlank = (bool)format.MediaPhysicallyBlank;
                var totalBytes = Math.Max(0, Convert.ToInt64(format.TotalSectorsOnMedia)) * 2048L;
                var freeBytes = Math.Max(0, Convert.ToInt64(format.FreeSectorsOnMedia)) * 2048L;
                var medium = PhysicalMediaName(type);
                var blank = heuristicBlank || physicalBlank || (status & 2) != 0;
                var writable = recorderSupported && mediaSupported && (status & 32768) == 0 && (status & 8192) == 0 && (status & 16384) == 0;
                var state = blank ? "Leer" : MediaStateName(status);
                var label = writable ? "BEREIT" : "PRUEFEN";
                var brush = writable ? Green : Yellow;
                var message = writable ? $"{medium} erkannt: {state}." : $"{medium} erkannt, aber nicht beschreibbar: {state}.";
                return new(driveName, $"{medium} in {driveName.TrimEnd('\\')}", true, "-", blank ? "Leer / noch kein Dateisystem" : "Vorhandenes Dateisystem", medium, totalBytes, freeBytes, writable, FormatBytes(freeBytes), writable ? "Beschreibbar" : "Nicht beschreibbar", message, label, brush);
            }
        }
        catch { return null; }
        finally { Release(format); Release(recorder); Release(master); }
        return null;
    }

    private static string PhysicalMediaName(int value) => value switch
    {
        1 => "CD-ROM", 2 => "CD-R", 3 => "CD-RW", 4 => "DVD-ROM", 5 => "DVD-RAM",
        6 => "DVD+R", 7 => "DVD+RW", 8 => "DVD+R DL", 9 => "DVD-R", 10 => "DVD-RW",
        11 => "DVD-R DL", 12 => "Wiederbeschreibbarer Datentraeger", 14 => "HD DVD-ROM",
        15 => "HD DVD-R", 16 => "HD DVD-RAM", 17 => "BD-ROM", 18 => "BD-R", 19 => "BD-RE",
        _ => "Unbekanntes Medium"
    };

    private static string MediaStateName(int status)
    {
        var states = new List<string>();
        if ((status & 1) != 0) states.Add("Nur ueberschreibbar");
        if ((status & 4) != 0) states.Add("Anhaengen moeglich");
        if ((status & 8) != 0) states.Add("Letzte Sitzung");
        if ((status & 1024) != 0) states.Add("Beschaedigt");
        if ((status & 2048) != 0) states.Add("Loeschen erforderlich");
        if ((status & 8192) != 0) states.Add("Schreibgeschuetzt");
        if ((status & 16384) != 0) states.Add("Abgeschlossen");
        if ((status & 32768) != 0) states.Add("Nicht unterstuetzt");
        return states.Count == 0 ? "Erkannt" : string.Join(", ", states);
    }

    private static string[] ReadStrings(object? value)
    {
        if (value is not IEnumerable values) return Array.Empty<string>();
        return values.Cast<object?>().Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
    }

    private static string NormalizeDrive(string value) => value.Trim().TrimEnd('\\').ToUpperInvariant();
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) try { Marshal.FinalReleaseComObject(value); } catch { } }
    private static string Empty(string? value) => string.IsNullOrWhiteSpace(value) ? "Ohne Bezeichnung" : value;
    private static string FormatBytes(long value) => value <= 0 ? "0 MB" : value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.00} GB" : $"{value / 1_000_000d:0.0} MB";

    public void Eject(string driveName) => SendTrayCommand(driveName, "open");
    public void Close(string driveName) => SendTrayCommand(driveName, "closed");
    private static void SendTrayCommand(string driveName, string action)
    {
        var letter = driveName.TrimEnd('\\');
        mciSendString($"open {letter} type cdaudio alias discforge", null, 0, IntPtr.Zero);
        mciSendString($"set discforge door {action}", null, 0, IntPtr.Zero);
        mciSendString("close discforge", null, 0, IntPtr.Zero);
    }
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr callback);
}