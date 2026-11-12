using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DiscForgeStudio.Services;

namespace DiscForgeStudio;

public partial class MainWindow
{
    private bool _driveInformationLoaded;

    private void DriveInformation_Loaded(object sender, RoutedEventArgs e)
    {
        if (_driveInformationLoaded) return;
        _driveInformationLoaded = true;
        RefreshDriveInformation();
    }

    private void RefreshDriveInformation_Click(object sender, RoutedEventArgs e) => RefreshDriveInformation();

    private void RefreshDriveInformation()
    {
        dynamic? master = null;
        var recorders = new List<object>();
        try
        {
            DriveHardwareStatusText.Text = "Optische Geräte werden über IMAPI abgefragt ...";
            var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
                ?? throw new PlatformNotSupportedException("IMAPI2.MsftDiscMaster2 ist nicht verfügbar.");
            master = Activator.CreateInstance(masterType)!;
            if (!(bool)master.IsSupportedEnvironment)
                throw new InvalidOperationException("Die Windows-Umgebung unterstützt IMAPI2 nicht.");

            var lines = new List<string>();
            var count = (int)master.Count;
            for (var index = 0; index < count; index++)
            {
                var uniqueId = (string)master.Item(index);
                var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
                    ?? throw new PlatformNotSupportedException("IMAPI2.MsftDiscRecorder2 ist nicht verfügbar.");
                dynamic recorder = Activator.CreateInstance(recorderType)!;
                recorders.Add(recorder);
                recorder.InitializeDiscRecorder(uniqueId);

                object volumePathNames = recorder.VolumePathNames;
                var paths = ReadStrings(volumePathNames);
                object supportedProfiles = recorder.SupportedProfiles;
                var profiles = ReadIntegers(supportedProfiles).Select(static profile => ProfileName(profile)).Where(static name => name.Length > 0).Distinct().ToArray();
                var vendor = SafeText(() => (string)recorder.VendorId, "Unbekannt");
                var product = SafeText(() => (string)recorder.ProductId, "Unbekannt");
                var revision = SafeText(() => (string)recorder.ProductRevision, "Unbekannt");
                var drive = paths.Length > 0 ? paths[0] : "ohne Laufwerksbuchstaben";

                lines.Add($"Laufwerk: {drive}");
                lines.Add($"Hersteller: {vendor.Trim()}");
                lines.Add($"Modell: {product.Trim()}");
                lines.Add($"Firmware: {revision.Trim()}");
                lines.Add($"Unterstützte Medien: {(profiles.Length == 0 ? "Nicht gemeldet" : string.Join(", ", profiles))}");
                if (index == 0) ReadCurrentMedium(recorder, lines);
                if (index + 1 < count) lines.Add(string.Empty);
            }

            DriveHardwareTitleText.Text = count == 1 ? "ERKANNTES OPTISCHES LAUFWERK" : $"ERKANNTE OPTISCHE LAUFWERKE ({count})";
            DriveHardwareStatusText.Text = count == 0 ? "Kein optisches Laufwerk über IMAPI gefunden." : string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            DriveHardwareTitleText.Text = "LAUFWERKSINFORMATION";
            DriveHardwareStatusText.Text = "Geräteinformationen konnten nicht gelesen werden: " + ex.GetBaseException().Message;
        }
        finally
        {
            foreach (var recorder in recorders)
                if (Marshal.IsComObject(recorder)) try { Marshal.FinalReleaseComObject(recorder); } catch { }
            if (master is not null && Marshal.IsComObject(master)) try { Marshal.FinalReleaseComObject(master); } catch { }
        }
    }

    private static string SafeText(Func<string> getter, string fallback)
    {
        try { var value = getter(); return string.IsNullOrWhiteSpace(value) ? fallback : value; }
        catch { return fallback; }
    }

    private static string[] ReadStrings(object? value)
    {
        if (value is not IEnumerable values) return Array.Empty<string>();
        return values.Cast<object?>().Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
    }

    private static int[] ReadIntegers(object? value)
    {
        if (value is not IEnumerable values) return Array.Empty<int>();
        var result = new List<int>();
        foreach (var entry in values) try { result.Add(Convert.ToInt32(entry)); } catch { }
        return result.ToArray();
    }

    private static string ProfileName(int profile) => profile switch
    {
        0x08 => "CD-ROM", 0x09 => "CD-R", 0x0A => "CD-RW",
        0x10 => "DVD-ROM", 0x11 => "DVD-R", 0x12 => "DVD-RAM",
        0x13 or 0x14 => "DVD-RW", 0x15 or 0x16 => "DVD-R DL",
        0x1A => "DVD+RW", 0x1B => "DVD+R", 0x2A => "DVD+RW DL", 0x2B => "DVD+R DL",
        0x40 => "BD-ROM", 0x41 or 0x42 => "BD-R", 0x43 => "BD-RE",
        _ => string.Empty
    };
    private void ReadCurrentMedium(object recorderObject, List<string> lines)
    {
        dynamic? format = null;
        try
        {
            dynamic recorder = recorderObject;
            var formatType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")
                ?? throw new PlatformNotSupportedException("IMAPI2.MsftDiscFormat2Data ist nicht verfügbar.");
            format = Activator.CreateInstance(formatType)!;
            format.Recorder = recorder;
            format.ClientName = ApplicationVersionService.ProductLabel;

            bool recorderSupported = (bool)format.IsRecorderSupported(recorder);
            bool mediaSupported = (bool)format.IsCurrentMediaSupported(recorder);
            int mediaType = Convert.ToInt32(format.CurrentPhysicalMediaType);
            int mediaStatus = Convert.ToInt32(format.CurrentMediaStatus);
            bool heuristicallyBlank = (bool)format.MediaHeuristicallyBlank;
            bool physicallyBlank = (bool)format.MediaPhysicallyBlank;
            long totalSectors = Convert.ToInt64(format.TotalSectorsOnMedia);
            long freeSectors = Convert.ToInt64(format.FreeSectorsOnMedia);
            long totalBytes = Math.Max(0, totalSectors) * 2048L;
            long freeBytes = Math.Max(0, freeSectors) * 2048L;
            string medium = PhysicalMediaName(mediaType);
            string state = MediaStateName(mediaStatus, heuristicallyBlank, physicallyBlank);

            lines.Add(string.Empty);
            lines.Add($"Eingelegtes Medium: {medium}");
            lines.Add($"Medienstatus: {state}");
            lines.Add($"Kapazität: {FormatDriveBytes(totalBytes)}");
            lines.Add($"Frei: {FormatDriveBytes(freeBytes)}");
            lines.Add($"Beschreibbar: {(recorderSupported && mediaSupported ? "Ja" : "Nein")}");

            MediaTypeText.Text = medium;
            FileSystemText.Text = heuristicallyBlank ? "Leer / noch kein Dateisystem" : "Vorhandenes Dateisystem";
            ProtectionText.Text = recorderSupported && mediaSupported ? "Beschreibbar" : "Nicht beschreibbar";
            VolumeLabelText.Text = heuristicallyBlank ? "-" : "Vorhandener Datenträger";
            CapacityText.Text = FormatDriveBytes(totalBytes);
            FreeSpaceText.Text = FormatDriveBytes(freeBytes);
            StatusText.Text = $"{medium} erkannt: {state}.";
        }
        catch (COMException ex) when ((uint)ex.HResult == 0xC0AA0202u)
        {
            ApplyNoMediumState(lines, "Kein Medium");
        }
        catch (Exception ex)
        {
            lines.Add(string.Empty);
            lines.Add("Medienabfrage: " + ex.GetBaseException().Message);
            StatusText.Text = "Medium konnte nicht vollständig abgefragt werden.";
        }
        finally
        {
            if (format is not null && Marshal.IsComObject(format)) try { Marshal.FinalReleaseComObject(format); } catch { }
        }
    }

    private void ApplyNoMediumState(List<string> lines, string message)
    {
        lines.Add(string.Empty);
        lines.Add("Eingelegtes Medium: " + message);
        MediaTypeText.Text = message;
        FileSystemText.Text = "-";
        ProtectionText.Text = "Nicht geprüft";
        VolumeLabelText.Text = "-";
        CapacityText.Text = "0 MB";
        FreeSpaceText.Text = "Nicht verfügbar";
        StatusText.Text = "Bitte einen Datenträger einlegen.";
    }

    private static string PhysicalMediaName(int value) => value switch
    {
        1 => "CD-ROM", 2 => "CD-R", 3 => "CD-RW", 4 => "DVD-ROM",
        5 => "DVD-RAM", 6 => "DVD+R", 7 => "DVD+RW", 8 => "DVD+R DL",
        9 => "DVD-R", 10 => "DVD-RW", 11 => "DVD-R DL", 12 => "Wiederbeschreibbarer Datenträger",
        14 => "HD DVD-ROM", 15 => "HD DVD-R", 16 => "HD DVD-RAM",
        17 => "BD-ROM", 18 => "BD-R", 19 => "BD-RE",
        _ => "Unbekanntes Medium"
    };

    private static string MediaStateName(int status, bool heuristicBlank, bool physicalBlank)
    {
        if (heuristicBlank || physicalBlank || (status & 2) != 0) return "Leer";
        var states = new List<string>();
        if ((status & 1) != 0) states.Add("Nur überschreibbar");
        if ((status & 4) != 0) states.Add("Anhängen möglich");
        if ((status & 8) != 0) states.Add("Letzte Sitzung");
        if ((status & 1024) != 0) states.Add("Beschädigt");
        if ((status & 2048) != 0) states.Add("Löschen erforderlich");
        if ((status & 8192) != 0) states.Add("Schreibgeschützt");
        if ((status & 16384) != 0) states.Add("Abgeschlossen");
        if ((status & 32768) != 0) states.Add("Nicht unterstützt");
        return states.Count == 0 ? "Erkannt" : string.Join(", ", states);
    }

    private static string FormatDriveBytes(long value) => value >= 1024L * 1024 * 1024
        ? $"{value / 1024d / 1024 / 1024:N2} GB"
        : $"{value / 1024d / 1024:N1} MB";

}
