using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace DiscForgeStudio;

public partial class MainWindow
{
    private void RunAdvancedIsoBurnPreflight()
    {
        dynamic? recorder = null;
        dynamic? format = null;
        try
        {
            var options = CreateBurnOptions();
            var errors = new List<string>(_isoPreflightService.ValidateBurn(options));
            var warnings = new List<string>();
            var checks = new List<string>();
            var iso = new FileInfo(options.IsoPath);
            if (!iso.Exists) errors.Add("ISO-Datei wurde nicht gefunden.");
            else
            {
                checks.Add($"ISO-Datei: {iso.FullName}");
                checks.Add($"ISO-Größe: {FormatIsoBytes(iso.Length)}");
                if (iso.Length == 0) errors.Add("ISO-Datei ist leer.");
                if (iso.Length % 2048 != 0) errors.Add("ISO-Größe ist kein Vielfaches der Sektorgröße 2048 Byte.");
            }

            var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
                ?? throw new PlatformNotSupportedException("IMAPI2.MsftDiscRecorder2 ist nicht verfügbar.");
            recorder = Activator.CreateInstance(recorderType)!;
            recorder.InitializeDiscRecorder(options.RecorderUniqueId);

            string drive = FirstArrayText(recorder.VolumePathNames, "ohne Laufwerksbuchstaben");
            string vendor = Convert.ToString(recorder.VendorId)?.Trim() ?? "Unbekannt";
            string product = Convert.ToString(recorder.ProductId)?.Trim() ?? "Unbekannt";
            string revision = Convert.ToString(recorder.ProductRevision)?.Trim() ?? "Unbekannt";
            checks.Add($"Laufwerk: {drive} | {vendor} {product} | Firmware {revision}");

            var formatType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")
                ?? throw new PlatformNotSupportedException("IMAPI2.MsftDiscFormat2Data ist nicht verfügbar.");
            format = Activator.CreateInstance(formatType)!;
            format.Recorder = recorder;
            format.ClientName = "DiscForgeStudio";

            bool recorderSupported = (bool)format.IsRecorderSupported(recorder);
            bool mediaSupported = (bool)format.IsCurrentMediaSupported(recorder);
            int mediaType = Convert.ToInt32(format.CurrentPhysicalMediaType);
            int status = Convert.ToInt32(format.CurrentMediaStatus);
            bool heuristicBlank = (bool)format.MediaHeuristicallyBlank;
            bool physicalBlank = (bool)format.MediaPhysicallyBlank;
            long totalSectors = Convert.ToInt64(format.TotalSectorsOnMedia);
            long freeSectors = Convert.ToInt64(format.FreeSectorsOnMedia);
            long totalBytes = Math.Max(0, totalSectors) * 2048L;
            long freeBytes = Math.Max(0, freeSectors) * 2048L;
            string medium = IsoPreflightMediaName(mediaType);
            string mediumState = IsoPreflightStateName(status, heuristicBlank, physicalBlank);

            checks.Add($"Medium: {medium} | Status: {mediumState}");
            checks.Add($"Kapazität: {FormatIsoBytes(totalBytes)} | Frei: {FormatIsoBytes(freeBytes)}");
            checks.Add($"Recorder unterstützt: {(recorderSupported ? "Ja" : "Nein")} | Medium unterstützt: {(mediaSupported ? "Ja" : "Nein")}");
            checks.Add($"Nach Brennen abschließen: {(options.CloseMedia ? "Ja" : "Nein")} | Auswerfen: {(options.EjectAfterWrite ? "Ja" : "Nein")}");

            if (!recorderSupported) errors.Add("Das ausgewählte Gerät wird vom IMAPI-Datenschreiber nicht unterstützt.");
            if (!mediaSupported) errors.Add("Das eingelegte Medium wird vom ausgewählten Laufwerk nicht unterstützt.");
            if (mediaType == 0) errors.Add("Kein Medium erkannt oder Medientyp unbekannt.");
            if ((status & 1024) != 0) errors.Add("Das Medium wird als beschädigt gemeldet.");
            if ((status & 8192) != 0) errors.Add("Das Medium ist schreibgeschützt.");
            if ((status & 16384) != 0) errors.Add("Das Medium ist bereits abgeschlossen.");
            if ((status & 32768) != 0) errors.Add("Das Medium wird als nicht unterstützt gemeldet.");
            if ((status & 2048) != 0) errors.Add("Das wiederbeschreibbare Medium muss vor dem Schreiben gelöscht werden.");
            if (iso.Exists && iso.Length > freeBytes) errors.Add($"ISO ist um {FormatIsoBytes(iso.Length - freeBytes)} größer als der freie Speicher.");
            if (iso.Exists && iso.Length > totalBytes) errors.Add("ISO ist größer als die Gesamtkapazität des Mediums.");
            if (!heuristicBlank && !physicalBlank && (status & 4) == 0 && (status & 1) == 0)
                warnings.Add("Das Medium ist nicht leer und nicht als anhängbar gemeldet.");
            if (mediaType == 3 && iso.Exists && iso.Length <= freeBytes)
                checks.Add("CD-RW-Kapazitätsprüfung: bestanden");

            _isoBurnPreflightPassed = errors.Count == 0;
            IsoBurnStartButton.IsEnabled = _isoBurnPreflightPassed;
            var report = new StringBuilder();
            report.AppendLine(_isoBurnPreflightPassed ? "ISO-BRENNVORPRÜFUNG BESTANDEN" : "ISO-BRENNVORPRÜFUNG NICHT BESTANDEN");
            report.AppendLine(new string('=', 62));
            foreach (var check in checks) report.AppendLine("[OK] " + check);
            foreach (var warning in warnings) report.AppendLine("[HINWEIS] " + warning);
            foreach (var error in errors) report.AppendLine("[FEHLER] " + error);
            report.AppendLine();
            report.AppendLine(_isoBurnPreflightPassed ? "Freigabe: ISO JETZT BRENNEN wurde aktiviert." : "Freigabe: Brennen bleibt gesperrt.");
            IsoLogTextBox.Text = report.ToString();
            _isoReport = BuildReport("Erweiterte ISO-Brennvorprüfung", _isoBurnPreflightPassed ? "Bestanden" : "Nicht bestanden", report.ToString());
            SaveIsoReportButton.IsEnabled = true;
            SetIsoStatus(_isoBurnPreflightPassed ? "Brennvorprüfung bestanden" : "Brennvorprüfung fehlgeschlagen", _isoBurnPreflightPassed ? $"{medium}: ISO passt auf das Medium; Brennen ist freigegeben." : "Mindestens eine Sicherheitsprüfung ist fehlgeschlagen.", _isoBurnPreflightPassed ? 100 : 0);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0xC0AA0202u)
        {
            _isoBurnPreflightPassed = false;
            IsoBurnStartButton.IsEnabled = false;
            IsoLogTextBox.Text = "ISO-BRENNVORPRÜFUNG NICHT BESTANDEN\r\n[FEHLER] Kein Medium im ausgewählten Laufwerk erkannt.";
            SetIsoStatus("Kein Medium", "Bitte ein beschreibbares Medium einlegen und erneut prüfen.", 0);
        }
        catch (Exception ex)
        {
            _isoBurnPreflightPassed = false;
            IsoBurnStartButton.IsEnabled = false;
            ShowIsoError(ex);
        }
        finally
        {
            if (format is not null && Marshal.IsComObject(format)) try { Marshal.FinalReleaseComObject(format); } catch { }
            if (recorder is not null && Marshal.IsComObject(recorder)) try { Marshal.FinalReleaseComObject(recorder); } catch { }
        }
    }

    private static string FirstArrayText(object? value, string fallback)
    {
        if (value is not System.Array array || array.Length == 0) return fallback;
        return array.GetValue(0)?.ToString() ?? fallback;
    }

    private static string IsoPreflightMediaName(int value) => value switch
    {
        1 => "CD-ROM", 2 => "CD-R", 3 => "CD-RW", 4 => "DVD-ROM", 5 => "DVD-RAM",
        6 => "DVD+R", 7 => "DVD+RW", 8 => "DVD+R DL", 9 => "DVD-R", 10 => "DVD-RW",
        11 => "DVD-R DL", 12 => "Wiederbeschreibbarer Datenträger", 16 => "BD-ROM", 17 => "BD-R", 18 => "BD-RE",
        _ => "Unbekanntes Medium"
    };

    private static string IsoPreflightStateName(int status, bool heuristicBlank, bool physicalBlank)
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
}
