using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using DiscForgeStudio.Services;

namespace DiscForgeStudio;

public partial class MainWindow
{
    private readonly WinPeService _winPeService = new();
    private CancellationTokenSource? _winPeCts;
    private string? _winPeIsoPath;

    private void WinPeTab_Loaded(object sender, RoutedEventArgs e) => RefreshWinPeStatus();

    private void RefreshWinPeStatus()
    {
        var status = _winPeService.DetectTools();
        WinPeAdkStatusText.Text = status.Ready ? "BEREIT" : "NICHT GEFUNDEN";
        WinPeAdkStatusText.Foreground = status.Ready ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Salmon;
        WinPeToolDetailsText.Text = status.Message + (status.CopypePath is null ? string.Empty : $"\nCopyPE: {status.CopypePath}\nMakeWinPEMedia: {status.MakeWinPeMediaPath}");
        WinPePrepareButton.IsEnabled = status.Ready;
        WinPeCreateIsoButton.IsEnabled = status.Ready && !string.IsNullOrWhiteSpace(WinPeWorkFolderTextBox.Text) && !string.IsNullOrWhiteSpace(WinPeIsoPathTextBox.Text);
    }

    private void WinPeRefresh_Click(object sender, RoutedEventArgs e) => RefreshWinPeStatus();

    private void ChooseWinPeWorkFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Leeren WinPE-Arbeitsordner wählen", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            WinPeWorkFolderTextBox.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(WinPeIsoPathTextBox.Text))
                WinPeIsoPathTextBox.Text = Path.Combine(dialog.FolderName, "DiscForge-WinPE-amd64.iso");
            RefreshWinPeStatus();
        }
    }

    private void ChooseWinPeIsoPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Ziel für startfähige WinPE-ISO wählen",
            Filter = "ISO-Abbild (*.iso)|*.iso",
            DefaultExt = ".iso",
            AddExtension = true,
            FileName = "DiscForge-WinPE-amd64.iso"
        };
        if (dialog.ShowDialog(this) == true) { WinPeIsoPathTextBox.Text = dialog.FileName; RefreshWinPeStatus(); }
    }

    private void ChooseWinPeExtras_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Optionalen Zusatzordner wählen", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                WinPeService.ValidateSeparateDirectories(WinPeWorkFolderTextBox.Text, dialog.FolderName);
                WinPeExtrasTextBox.Text = dialog.FolderName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Windows PE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void WinPePrepare_Click(object sender, RoutedEventArgs e)
    {
        var work = WinPeWorkFolderTextBox.Text.Trim();
        var extras = WinPeExtrasTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(work)) { MessageBox.Show(this, "Bitte zuerst einen leeren WinPE-Arbeitsordner wählen.", "Windows PE", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            WinPeService.ValidateSeparateDirectories(work, extras);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Windows PE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _winPeCts = new CancellationTokenSource();
        SetWinPeBusy(true);
        WinPeLogTextBox.Clear();
        var progress = CreateWinPeProgress();
        try
        {
            await _winPeService.PrepareAsync(work, progress, _winPeCts.Token);
            if (!string.IsNullOrWhiteSpace(extras))
                await _winPeService.CopyExtrasAsync(work, extras, progress, _winPeCts.Token);
            WinPeCreateIsoButton.IsEnabled = !string.IsNullOrWhiteSpace(WinPeIsoPathTextBox.Text);
            WinPeLiveStatus.Update("WinPE-Vorbereitung abgeschlossen", 80, "Vorbereitung 100 % · ISO-Erstellung ist der verbleibende Schritt.");
        }
        catch (Exception ex) { ShowWinPeError(ex); }
        finally { SetWinPeBusy(false); _winPeCts?.Dispose(); _winPeCts = null; }
    }

    private async void WinPeCreateIso_Click(object sender, RoutedEventArgs e)
    {
        var work = WinPeWorkFolderTextBox.Text.Trim();
        var iso = WinPeIsoPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(work) || string.IsNullOrWhiteSpace(iso)) { MessageBox.Show(this, "Arbeitsordner und ISO-Ziel fehlen."); return; }
        _winPeCts = new CancellationTokenSource();
        SetWinPeBusy(true);
        var progress = CreateWinPeProgress();
        try
        {
            var result = await _winPeService.CreateIsoAsync(work, iso, progress, _winPeCts.Token);
            _winPeIsoPath = result.IsoPath;
            WinPeIsoResultText.Text = $"ISO: {result.IsoPath}\nGröße: {FormatIsoBytes(result.Bytes)}\nSHA-256: {result.Sha256}";
            WinPeUseIsoButton.IsEnabled = result.Success;
            WinPeOpenFolderButton.IsEnabled = result.Success;
            WinPeLiveStatus.Update("Startfähige WinPE-ISO bereit", 100, $"{FormatIsoBytes(result.Bytes)} · SHA-256 berechnet");
            WinPeLogTextBox.AppendText(result.Message + Environment.NewLine);
        }
        catch (Exception ex) { ShowWinPeError(ex); }
        finally { SetWinPeBusy(false); _winPeCts?.Dispose(); _winPeCts = null; }
    }

    private void WinPeUseIso_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_winPeIsoPath) || !File.Exists(_winPeIsoPath)) return;
        _selectedIsoPath = _winPeIsoPath;
        SelectedIsoPathText.Text = _winPeIsoPath;
        SelectedIsoSizeText.Text = $"Größe: {FormatIsoBytes(new FileInfo(_winPeIsoPath).Length)}";
        SelectedIsoStatusText.Text = "Status: Startfähige WinPE-ISO übernommen";
        _isoBurnPreflightPassed = false;
        IsoBurnStartButton.IsEnabled = false;
        IsoWorkshopTab.IsSelected = true;
        SetIsoStatus("WinPE-ISO übernommen", "Bitte jetzt BRENNEN VORPRÜFEN ausführen.", 0);
    }

    private void WinPeOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_winPeIsoPath)) return;
        var folder = Path.GetDirectoryName(_winPeIsoPath);
        if (folder is not null) Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    private void WinPeCancel_Click(object sender, RoutedEventArgs e)
    {
        _winPeCts?.Cancel();
        _winPeService.Cancel();
    }

    private Progress<(int Percent, string Message)> CreateWinPeProgress() => new(update =>
    {
        WinPeLiveStatus.Update(update.Message, update.Percent, update.Message);
        WinPeLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {update.Message}{Environment.NewLine}");
    });

    private void SetWinPeBusy(bool busy)
    {
        WinPeRefreshButton.IsEnabled = !busy;
        WinPeChooseWorkButton.IsEnabled = !busy;
        WinPeChooseIsoButton.IsEnabled = !busy;
        WinPeChooseExtrasButton.IsEnabled = !busy;
        WinPePrepareButton.IsEnabled = !busy && _winPeService.DetectTools().Ready;
        WinPeCreateIsoButton.IsEnabled = !busy && _winPeService.DetectTools().Ready;
        WinPeCancelButton.IsEnabled = busy;
    }

    private void ShowWinPeError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        WinPeLiveStatus.Update("WinPE-Fehler", 0, message);
        WinPeLogTextBox.AppendText("FEHLER: " + message + Environment.NewLine);
        MessageBox.Show(this, message, "DiscForge Windows PE", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
