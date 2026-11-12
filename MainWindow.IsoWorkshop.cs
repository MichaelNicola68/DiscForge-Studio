using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DiscForgeStudio.Models;
using DiscForgeStudio.Services;

namespace DiscForgeStudio;

public partial class MainWindow
{
    private readonly IsoImageService _isoImageService = new();
    private readonly IsoBurnService _isoBurnService = new();
    private readonly IsoHashService _isoHashService = new();
    private readonly IsoPreflightService _isoPreflightService = new();
    private CancellationTokenSource? _isoCancellation;
    private string? _selectedIsoPath;
    private string? _isoReport;
    private bool _isoBuildPreflightPassed;
    private bool _isoBurnPreflightPassed;

    private void ChooseIsoOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "ISO-Speicherort wählen", Filter = "ISO-Abbild (*.iso)|*.iso", DefaultExt = ".iso", AddExtension = true, OverwritePrompt = true, FileName = "DiscForge.iso" };
        if (dialog.ShowDialog(this) == true)
        {
            IsoOutputPathTextBox.Text = dialog.FileName;
            _isoBuildPreflightPassed = false;
            IsoBuildStartButton.IsEnabled = false;
            SetIsoStatus("Bereit", "ISO-Speicherort wurde gewählt.", 0);
        }
    }

    private async void ChooseIsoFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "ISO-Datei auswählen", Filter = "ISO-Abbild (*.iso)|*.iso", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _selectedIsoPath = dialog.FileName;
        SelectedIsoPathText.Text = _selectedIsoPath;
        _isoBurnPreflightPassed = false;
        IsoBurnStartButton.IsEnabled = false;
        SetIsoBusy(true, "ISO wird geprüft");
        try
        {
            var result = await _isoHashService.InspectAsync(_selectedIsoPath);
            SelectedIsoSizeText.Text = $"Größe: {FormatIsoBytes(result.Bytes)}";
            SelectedIsoHashText.Text = $"SHA-256: {result.Sha256}";
            SelectedIsoStatusText.Text = result.Success ? "Status: Lesbar und geprüft" : "Status: Prüfung fehlgeschlagen";
            _isoReport = BuildReport("ISO-Prüfung", result.Message, $"Datei: {result.Path}\r\nGröße: {FormatIsoBytes(result.Bytes)}\r\nSHA-256: {result.Sha256}");
            SaveIsoReportButton.IsEnabled = result.Success;
            SetIsoStatus(result.Success ? "Geprüft" : "Fehler", result.Message, result.Success ? 100 : 0);
        }
        catch (Exception ex) { ShowIsoError(ex); }
        finally { SetIsoBusy(false); }
    }

    private void IsoBuildPreflight_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = CreateBuildOptions();
            var errors = _isoPreflightService.ValidateBuild(options);
            _isoBuildPreflightPassed = errors.Count == 0;
            IsoBuildStartButton.IsEnabled = _isoBuildPreflightPassed;
            var text = _isoBuildPreflightPassed ? $"ISO-VORPRÜFUNG BESTANDEN\r\nDateien: {options.Items.Count}\r\nZiel: {options.OutputPath}\r\nDatenträgername: {options.VolumeName}\r\nDateisystem: {options.FileSystems}" : "ISO-VORPRÜFUNG NICHT BESTANDEN\r\n" + string.Join("\r\n", errors.Select(x => "- " + x));
            IsoLogTextBox.Text = text;
            SetIsoStatus(_isoBuildPreflightPassed ? "Vorprüfung bestanden" : "Vorprüfung fehlgeschlagen", text, _isoBuildPreflightPassed ? 100 : 0);
        }
        catch (Exception ex) { _isoBuildPreflightPassed = false; IsoBuildStartButton.IsEnabled = false; ShowIsoError(ex); }
    }

    private async void IsoBuildStart_Click(object sender, RoutedEventArgs e)
    {
        if (!_isoBuildPreflightPassed) { MessageBox.Show(this, "Bitte zuerst ISO VORPRÜFEN ausführen.", "DiscForge Studio", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var options = CreateBuildOptions();
        _isoCancellation = new CancellationTokenSource();
        SetIsoBusy(true, "ISO wird erstellt");
        var progress = new Progress<IsoProgress>(UpdateIsoProgress);
        try
        {
            var result = await _isoImageService.CreateAsync(options, progress, _isoCancellation.Token);
            _isoReport = BuildReport("ISO-Erstellung", result.Message, $"Datei: {result.OutputPath}\r\nGröße: {FormatIsoBytes(result.ImageBytes)}\r\nSHA-256: {result.Sha256 ?? "nicht berechnet"}\r\nStart: {result.StartedAt:yyyy-MM-dd HH:mm:ss}\r\nEnde: {result.EndedAt:yyyy-MM-dd HH:mm:ss}");
            IsoLogTextBox.Text = _isoReport;
            SaveIsoReportButton.IsEnabled = true;
            if (result.Success)
            {
                _selectedIsoPath = result.OutputPath;
                SelectedIsoPathText.Text = result.OutputPath;
                SelectedIsoSizeText.Text = $"Größe: {FormatIsoBytes(result.ImageBytes)}";
                SelectedIsoHashText.Text = $"SHA-256: {result.Sha256 ?? "nicht berechnet"}";
                SelectedIsoStatusText.Text = "Status: Erfolgreich erstellt";
                SetIsoStatus("ISO erstellt", result.Message, 100);
            }
            else SetIsoStatus("Fehler", result.Message, 0);
        }
        catch (Exception ex) { ShowIsoError(ex); }
        finally { SetIsoBusy(false); _isoCancellation.Dispose(); _isoCancellation = null; }
    }

    private void IsoBurnPreflight_Click(object sender, RoutedEventArgs e)
    {
        RunAdvancedIsoBurnPreflight();
    }

    private async void IsoBurnStart_Click(object sender, RoutedEventArgs e)
    {
        if (!_isoBurnPreflightPassed) { MessageBox.Show(this, "Bitte zuerst BRENNEN VORPRÜFEN ausführen.", "DiscForge Studio", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var options = CreateBurnOptions();
        if (MessageBox.Show(this, $"ISO wirklich auf das ausgewählte Laufwerk schreiben?\r\n\r\n{options.IsoPath}\r\n\r\nDieser Vorgang verändert das eingelegte Medium.", "ISO JETZT BRENNEN", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _isoCancellation = new CancellationTokenSource();
        SetIsoBusy(true, "ISO wird gebrannt");
        var progress = new Progress<IsoProgress>(UpdateIsoProgress);
        try
        {
            var result = await _isoBurnService.BurnAsync(options, progress, _isoCancellation.Token);
            _isoReport = BuildReport("ISO-Brennvorgang", result.Message, $"ISO: {result.IsoPath}\r\nGröße: {FormatIsoBytes(result.ImageBytes)}\r\nSHA-256: {result.ImageSha256 ?? "nicht berechnet"}\r\nStart: {result.StartedAt:yyyy-MM-dd HH:mm:ss}\r\nEnde: {result.EndedAt:yyyy-MM-dd HH:mm:ss}");
            IsoLogTextBox.Text = _isoReport;
            SaveIsoReportButton.IsEnabled = true;
            SetIsoStatus(result.Success ? "Brennen abgeschlossen" : "Brennfehler", result.Message, result.Success ? 100 : 0);
        }
        catch (Exception ex) { ShowIsoError(ex); }
        finally { SetIsoBusy(false); _isoCancellation.Dispose(); _isoCancellation = null; }
    }

    private void SaveIsoReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_isoReport)) return;
        var dialog = new SaveFileDialog { Title = "ISO-Bericht speichern", Filter = "Textbericht (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true, FileName = $"DiscForge-ISO-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, _isoReport, new UTF8Encoding(true));
    }

    private IsoBuildOptions CreateBuildOptions()
    {
        if (IsoOutputPathTextBox.Text == "Noch kein Speicherort gewählt") throw new InvalidOperationException("Bitte zuerst einen ISO-Speicherort wählen.");
        var items = ReadCompilationItems();
        return new IsoBuildOptions(items, IsoOutputPathTextBox.Text, IsoVolumeNameTextBox.Text.Trim(), SelectedFileSystems(), SelectedCapacity(), IsoHashAfterBuildCheckBox.IsChecked == true);
    }

    private IsoBurnOptions CreateBurnOptions()
    {
        if (string.IsNullOrWhiteSpace(_selectedIsoPath)) throw new InvalidOperationException("Bitte zuerst eine ISO-Datei auswählen.");
        var recorderId = ResolveImapiRecorderId(ReadDriveLetter(DriveList.SelectedItem));
        return new IsoBurnOptions(_selectedIsoPath, recorderId, IsoCloseMediaCheckBox.IsChecked == true, IsoEjectAfterBurnCheckBox.IsChecked == true, true);
    }

    private List<IsoSourceItem> ReadCompilationItems()
    {
        var result = new List<IsoSourceItem>();
        foreach (var item in (IEnumerable)CompilationList.Items)
        {
            var source = ReadString(item, "SourcePath", "FullPath", "Path", "FilePath");
            if (string.IsNullOrWhiteSpace(source)) continue;
            var relative = ReadString(item, "RelativePath", "TargetPath", "DiscPath", "Name");
            if (string.IsNullOrWhiteSpace(relative)) relative = System.IO.Path.GetFileName(source);
            if (File.Exists(source)) result.Add(new IsoSourceItem(source, relative));
            else if (Directory.Exists(source))
            {
                var rootName = System.IO.Path.GetFileName(source.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    result.Add(new IsoSourceItem(file, System.IO.Path.Combine(rootName, System.IO.Path.GetRelativePath(source, file))));
            }
        }
        if (result.Count == 0) throw new InvalidOperationException("Die Datei-Zusammenstellung enthält keine lesbaren Dateien.");
        return result;
    }

    private static string? ReadRecorderId(object? item) => ReadString(item, "RecorderUniqueId", "UniqueId", "RecorderId", "InternalUniqueId", "Id");
    private static string? ReadDriveLetter(object? item)
    {
        var value = ReadString(item, "DriveLetter", "VolumePath", "Path", "Name", "DisplayName") ?? item?.ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        for (var i = 0; i < value.Length - 1; i++)
            if (char.IsLetter(value[i]) && value[i + 1] == ':') return char.ToUpperInvariant(value[i]) + @":\";
        return null;
    }

    private static string ResolveImapiRecorderId(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            throw new InvalidOperationException("Bitte in der Laufwerksdiagnose ein Brennlaufwerk auswählen.");
        var requestedDrive = driveLetter.TrimEnd('\\') + @"\";
        dynamic? master = null;
        try
        {
            var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
                ?? throw new PlatformNotSupportedException("IMAPI2 DiscMaster ist nicht verfügbar.");
            master = Activator.CreateInstance(masterType)!;
            if (!(bool)master.IsSupportedEnvironment)
                throw new InvalidOperationException("Die Windows-Umgebung unterstützt IMAPI2 nicht.");
            var foundDrives = new List<string>();
            for (var index = 0; index < (int)master.Count; index++)
            {
                var uniqueId = (string)master.Item(index);
                dynamic? recorder = null;
                try
                {
                    var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
                        ?? throw new PlatformNotSupportedException("IMAPI2 Recorder ist nicht verfügbar.");
                    recorder = Activator.CreateInstance(recorderType)!;
                    recorder.InitializeDiscRecorder(uniqueId);
                    foreach (var path in (IEnumerable)recorder.VolumePathNames)
                    {
                        var volumePath = path?.ToString();
                        if (string.IsNullOrWhiteSpace(volumePath)) continue;
                        foundDrives.Add(volumePath);
                        if (string.Equals(volumePath.TrimEnd('\\'), requestedDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            return uniqueId;
                    }
                }
                finally
                {
                    if (recorder is not null && Marshal.IsComObject(recorder)) Marshal.FinalReleaseComObject(recorder);
                }
            }
            throw new InvalidOperationException($"Für Laufwerk {requestedDrive} wurde kein IMAPI-Brenner gefunden. Erkannte IMAPI-Laufwerke: {(foundDrives.Count == 0 ? "keine" : string.Join(", ", foundDrives))}");
        }
        finally
        {
            if (master is not null && Marshal.IsComObject(master)) Marshal.FinalReleaseComObject(master);
        }
    }
    private static string? ReadString(object? item, params string[] names)
    {
        if (item is null) return null;
        if (item is string text) return text;
        var type = item.GetType();
        foreach (var name in names)
        {
            var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (p?.GetValue(item)?.ToString() is { Length: > 0 } value) return value;
        }
        return null;
    }

    private IsoFileSystems SelectedFileSystems() => IsoFileSystemComboBox.SelectedIndex switch { 1 => IsoFileSystems.Iso9660 | IsoFileSystems.Joliet, 2 => IsoFileSystems.Udf, _ => IsoFileSystems.Iso9660 | IsoFileSystems.Joliet | IsoFileSystems.Udf };
    private long SelectedCapacity() => IsoTargetCapacityComboBox.SelectedIndex switch { 0 => 700L * 1024 * 1024, 2 => 8500L * 1000 * 1000, 3 => 25L * 1000 * 1000 * 1000, 4 => 50L * 1000 * 1000 * 1000, _ => 4700L * 1000 * 1000 };

    private void UpdateIsoProgress(IsoProgress value)
    {
        IsoPhaseText.Text = value.Phase;
        IsoProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
        IsoProgressText.Text = $"{value.Phase}: {value.Percent}% | {FormatIsoBytes(value.ProcessedBytes)} von {FormatIsoBytes(value.TotalBytes)}" + (string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : $" | {value.CurrentItem}");
    }

    private void SetIsoBusy(bool busy, string? phase = null)
    {
        ChooseIsoOutputButton.IsEnabled = !busy; ChooseIsoFileButton.IsEnabled = !busy;
        IsoBuildPreflightButton.IsEnabled = !busy; IsoBurnPreflightButton.IsEnabled = !busy;
        IsoBuildStartButton.IsEnabled = !busy && _isoBuildPreflightPassed;
        IsoBurnStartButton.IsEnabled = !busy && _isoBurnPreflightPassed;
        if (!string.IsNullOrWhiteSpace(phase)) IsoPhaseText.Text = phase;
    }

    private void SetIsoStatus(string phase, string text, int percent)
    {
        IsoPhaseText.Text = phase; IsoProgressText.Text = text; IsoProgressBar.Value = Math.Clamp(percent, 0, 100); FooterStatus.Text = text.Split('\r', '\n')[0];
    }

    private void ShowIsoError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        IsoLogTextBox.Text = "FEHLER\r\n" + message;
        SetIsoStatus("Fehler", message, 0);
        MessageBox.Show(this, message, "ISO-Werkstatt", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FormatIsoBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024 / 1024:N2} GB" : value >= 1024L * 1024 ? $"{value / 1024d / 1024:N1} MB" : $"{value / 1024d:N1} KB";
    private static string BuildReport(string title, string result, string details) => $"DiscForge Studio {ApplicationVersionService.Current} - {title}\r\n{new string('=', 62)}\r\nErstellt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nErgebnis: {result}\r\n\r\n{details}\r\n";
}
