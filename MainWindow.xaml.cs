using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiscForgeStudio.Models;
using DiscForgeStudio.Services;
using Microsoft.Win32;

namespace DiscForgeStudio;

public partial class MainWindow : Window
{
    private readonly OpticalDriveService _service = new();
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly ObservableCollection<CompilationItem> _items = [];
    private readonly BurnService _burnService = new();
    private readonly VerifyService _verifyService = new();
    private readonly SnapshotService _snapshotService = new();
    private readonly MultisessionInspectorService _multisessionInspector = new();
    private readonly MultisessionWriterService _multisessionWriter = new();
    private readonly SafetyScanService _safetyScan = new();
    private readonly MultisessionVerifyService _multisessionVerify = new();
    private readonly SessionHistoryService _sessionHistory = new();
    private SessionHistoryEntry? _selectedHistoryEntry;
    private MultisessionSnapshot? _appendSnapshot;
    private MultisessionVerifyResult? _lastAppendVerification;
    private CancellationTokenSource? _appendCts;
    private bool _appendPreflightPassed;
    private MultisessionInspection? _lastInspection;
    private BurnSnapshot? _burnSnapshot;
    private VerificationResult? _lastVerification;
    private CancellationTokenSource? _burnCts;
    private bool _preflightPassed;
    private readonly CompletionReportTextService _completionReportTextService = new();
    private DateTime _standardOperationStarted;
    private string? _lastStandardCompletionReportPath;
    private string _lastSignature = string.Empty;
    private OpticalDriveInfo? Selected => DriveList.SelectedItem as OpticalDriveInfo;

    public MainWindow()
    {
        InitializeComponent();
        OpenSourceSoftwareIntegration.Attach(this);
        UpdateCenterIntegration.Attach(this);
        Title = ApplicationVersionService.WindowTitle;
        HeaderVersionText.Text = $"{ApplicationVersionService.VersionLabel} · Laufwerke prüfen und eigene Datei-Zusammenstellungen planen";
        CompilationVersionText.Text = ApplicationVersionService.VersionLabel;
        CompilationList.ItemsSource = _items;
        InitializeUnifiedCompilation();
        Loaded += (_, _) => { RefreshDrives(); UpdateCapacity(); _mediaTimer.Start(); };
        Closed += (_, _) => _mediaTimer.Stop();
        _mediaTimer.Tick += (_, _) => RefreshDrives(true);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(
            "Was moechtest du hinzufuegen?\n\nJA: Eine oder mehrere Dateien\nNEIN: Einen kompletten Ordner\nABBRECHEN: Nichts aendern\n\nTipp: Dateien und Ordner koennen auch gemeinsam auf die Liste gezogen werden.",
            "Dateien und Ordner hinzufuegen",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (choice == MessageBoxResult.Cancel) return;
        if (choice == MessageBoxResult.No)
        {
            AddFolderFromDialog();
            return;
        }

        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Dateien auswaehlen",
            Filter = "Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var before = _items.Count;
        foreach (var path in dialog.FileNames)
            AddFile(path, Path.GetFileName(path));

        UpdateCapacity();
        ShowAddResult(_items.Count - before, 0);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e) => AddFolderFromDialog();

    private void InitializeUnifiedCompilation()
    {
        CompilationList.AllowDrop = true;
        CompilationList.PreviewDragOver += CompilationList_PreviewDragOver;
        CompilationList.Drop += CompilationList_Drop;
        CompilationList.ToolTip = "Dateien und Ordner gemeinsam hierher ziehen";
    }

    private void CompilationList_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void CompilationList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var before = _items.Count;
        var folders = 0;
        var warnings = new List<string>();

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                AddFile(path, Path.GetFileName(path));
                continue;
            }

            if (!Directory.Exists(path)) continue;
            folders++;
            AddFolderContents(path, warnings);
        }

        UpdateCapacity();
        ShowAddResult(_items.Count - before, folders);

        if (warnings.Count > 0)
        {
            MessageBox.Show(
                "Einige Inhalte konnten nicht gelesen werden:\n\n" + string.Join("\n", warnings.Take(8)),
                "Hinweis zur Zusammenstellung",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AddFolderFromDialog()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Ordner auswaehlen",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var before = _items.Count;
        var warnings = new List<string>();
        AddFolderContents(dialog.FolderName, warnings);
        UpdateCapacity();
        ShowAddResult(_items.Count - before, 1);

        if (warnings.Count > 0)
        {
            MessageBox.Show(
                "Der Ordner wurde hinzugefuegt, aber einige Inhalte konnten nicht gelesen werden:\n\n" + string.Join("\n", warnings.Take(8)),
                "Ordner teilweise gelesen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AddFolderContents(string root, List<string> warnings)
    {
        try
        {
            var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    AddFile(path, Path.Combine(rootName, Path.GetRelativePath(root, path)));
                }
                catch (Exception ex)
                {
                    warnings.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add(root + ": " + ex.Message);
        }
    }

    private void ShowAddResult(int addedFiles, int addedFolders)
    {
        if (addedFiles <= 0)
        {
            FooterStatus.Text = "Keine neuen Inhalte hinzugefuegt. Bereits vorhandene Eintraege wurden nicht doppelt aufgenommen.";
            return;
        }

        FooterStatus.Text = addedFolders > 0
            ? $"Fertig: {addedFiles} Datei(en) aus {addedFolders} Ordner(n) hinzugefuegt."
            : $"Fertig: {addedFiles} Datei(en) hinzugefuegt.";
    }
    private void AddFile(string path, string relative)
    {
        if (_items.Any(i => i.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        try { var info = new FileInfo(path); _items.Add(new(path, relative, info.Extension.TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "DATEI", info.Length)); }
        catch { }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (CompilationItem item in CompilationList.SelectedItems.Cast<CompilationItem>().ToList()) _items.Remove(item);
        UpdateCapacity();
    }
    private void ClearCompilation_Click(object sender, RoutedEventArgs e) { _items.Clear(); UpdateCapacity(); }
    private void TargetMedium_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateCapacity(); }

    private void UpdateCapacity()
    {
        var total = _items.Sum(i => i.Size);
        var capacity = GetTargetCapacity();
        var percent = capacity == 0 ? 0 : Math.Min(100, total * 100d / capacity);
        CapacityBar.Value = percent;
        UsageText.Text = $"{_items.Count} Datei(en) aus Dateien und Ordnern · {FormatBytes(total)} von {FormatBytes(capacity)}";
        CapacityStatus.Text = total > capacity ? $"Zu gross: {FormatBytes(total - capacity)} müssen entfernt oder auf weitere Discs verteilt werden." : $"Noch {FormatBytes(capacity - total)} verfügbar.";
        CapacityStatus.Foreground = total > capacity ? System.Windows.Media.Brushes.Salmon : (System.Windows.Media.Brush)FindResource("AccentBrush");
        FooterStatus.Text = _items.Count == 0 ? "Noch keine Dateien oder Ordner hinzugefuegt." : "Zusammenstellung wurde neu berechnet.";
        if (BurnProjectText is not null) BurnProjectText.Text = $"{_items.Count} Dateien · {FormatBytes(total)}";
        if (AppendProjectText is not null) AppendProjectText.Text = $"{_items.Count} neue Dateien · {FormatBytes(total)}";
        _appendPreflightPassed = false; if (AppendSessionButton is not null) AppendSessionButton.IsEnabled = false;
        _preflightPassed = false; if (BurnButton is not null) BurnButton.IsEnabled = false;
    }

    private long GetTargetCapacity() => TargetMedium.SelectedItem is ComboBoxItem item && long.TryParse(item.Tag?.ToString(), out var value) ? value : 734003200;

    private void LoadCompilation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "DiscForge-Projekt (*.dfproject)|*.dfproject", Title = "DiscForge-Projekt öffnen" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
            var root = document.RootElement;
            _items.Clear();
            if (root.TryGetProperty("Files", out var files))
            {
                foreach (var file in files.EnumerateArray())
                {
                    var fullPath = file.GetProperty("FullPath").GetString() ?? string.Empty;
                    var relativePath = file.GetProperty("RelativePath").GetString() ?? Path.GetFileName(fullPath);
                    var type = file.GetProperty("Type").GetString() ?? "DATEI";
                    var size = file.GetProperty("Size").GetInt64();
                    _items.Add(new(fullPath, relativePath, type, size));
                }
            }
            if (root.TryGetProperty("TargetCapacity", out var capacity)) SelectTargetCapacity(capacity.GetInt64());
            UpdateCapacity();
            FooterStatus.Text = $"Projekt geladen: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Das Projekt konnte nicht geöffnet werden.\n\n{ex.Message}", "DiscForge-Projekt", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectTargetCapacity(long capacity)
    {
        foreach (var entry in TargetMedium.Items.OfType<ComboBoxItem>())
            if (long.TryParse(entry.Tag?.ToString(), out var value) && value == capacity) { TargetMedium.SelectedItem = entry; return; }
    }

    private void SaveCompilation_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) { MessageBox.Show("Die Zusammenstellung ist leer."); return; }
        var dialog = new SaveFileDialog { Filter = "DiscForge-Projekt (*.dfproject)|*.dfproject", FileName = $"DiscForge-Projekt-{DateTime.Now:yyyyMMdd-HHmm}.dfproject" };
        if (dialog.ShowDialog() != true) return;
        var project = new { Version = ApplicationVersionService.Current, Created = DateTime.Now, TargetCapacity = GetTargetCapacity(), TotalSize = _items.Sum(i => i.Size), Files = _items };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(project, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        FooterStatus.Text = "Projektliste wurde gespeichert.";
    }


    private async void Preflight_Click(object sender, RoutedEventArgs e)
    {
        BurnLog.Clear();
        BurnProgress.Value = 0;
        _preflightPassed = false;
        BurnButton.IsEnabled = false;
        PreflightButton.IsEnabled = false;

        var problems = new List<string>();
        if (Selected is not { } drive)
            problems.Add("Kein optisches Laufwerk ausgewählt.");

        if (_items.Count == 0)
            problems.Add("Die Zusammenstellung ist noch leer. Bitte zuerst Dateien oder Ordner hinzufuegen.");

        foreach (var item in _items.Where(i => !File.Exists(i.FullPath)))
            problems.Add($"Datei fehlt: {item.FullPath}");

        if (_items.Sum(i => i.Size) > GetTargetCapacity())
            problems.Add("Das Projekt überschreitet die Kapazität des gewählten Zielmediums.");

        if (string.IsNullOrWhiteSpace(DiscLabelInput.Text))
            problems.Add("Der Datenträgername fehlt.");

        if (problems.Count == 0 && Selected is { } selectedDrive)
        {
            BurnStatusText.Text = "Prüfe leeres Medium über Windows IMAPI2...";
            BurnProgress.Value = 35;
            try
            {
                var media = await _burnService.ProbeMediaAsync(selectedDrive.Name, CancellationToken.None);
                BurnLog.AppendText($"OK: {media}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }
        }

        PreflightButton.IsEnabled = true;
        if (problems.Count > 0)
        {
            BurnStatusText.Text = "Vorprüfung fehlgeschlagen";
            BurnStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            BurnProgress.Value = 0;
            BurnLog.Text += string.Join(Environment.NewLine, problems.Select(problem => "- " + problem));
            return;
        }

        _preflightPassed = true;
        BurnButton.IsEnabled = true;
        BurnProgress.Value = 100;
        BurnStatusText.Text = "Vorprüfung bestanden. Brennen kann gestartet werden.";
        BurnStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        BurnLog.AppendText(string.Join(Environment.NewLine, new[]
        {
            "OK: Projektdateien vorhanden",
            "OK: Kapazität ausreichend",
            "OK: Leeres beschreibbares Medium über IMAPI2 erkannt",
            "OK: Datenträgername gültig"
        }));
    }

    private async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (!_preflightPassed || Selected is not { } drive)
            return;

        if (MessageBox.Show(
                "Der Brennvorgang schreibt jetzt dauerhaft auf das eingelegte Medium. Fortfahren?",
                "DiscForge Brennfreigabe",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _standardOperationStarted = DateTime.Now;
        SetBurnUi(true);
        _burnCts = new CancellationTokenSource();
        BurnProgress.Value = 0;
        BurnLog.Clear();

        var reporter = new Progress<(int Percent, string Message)>(update =>
        {
            if (update.Percent >= 0)
                BurnProgress.Value = update.Percent;
            BurnStatusText.Text = update.Message;
            BurnLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {update.Message}{Environment.NewLine}");
});

        try
        {
        var burnDriveName = drive.Name;
        var burnDiscLabel = DiscLabelInput.Text.Trim();
        var burnItems = _items.ToList();
        var closeMedia = CloseMediaCheck.IsChecked == true;
        var ejectAfterBurn = EjectAfterBurnCheck.IsChecked == true;
        var burnToken = _burnCts.Token;

        BurnStatusText.Text = "Erzeuge Verify+-Snapshot vor dem Brennen...";
            _burnSnapshot = await Task.Run(() => _snapshotService.CreateAsync(burnDriveName, burnDiscLabel, burnItems, reporter, burnToken));

            await Task.Run(() => _burnService.BurnAsync(burnDriveName, burnItems, burnDiscLabel, closeMedia, ejectAfterBurn, reporter, burnToken));

            _snapshotService.Save(_burnSnapshot);
            _lastStandardCompletionReportPath = WriteStandardCompletionReport(
                "Brennen & Verify+", ReportOutcome.Succeeded,
                "Daten-Disc erfolgreich geschrieben. Verify+ ist noch nicht ausgefuehrt.",
                ReportCheckOutcome.NotRun, null);
            BurnLog.AppendText($"Abschlussbericht: {_lastStandardCompletionReportPath}{Environment.NewLine}");
            BurnLog.AppendText($"Verify+-Snapshot gespeichert: {_burnSnapshot.Created:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");

            MessageBox.Show(
                "Die Daten-Disc wurde erfolgreich geschrieben.",
                "DiscForge",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            BurnStatusText.Text = "Brennvorgang abgebrochen.";
            _lastStandardCompletionReportPath = WriteStandardCompletionReport(
                "Brennen & Verify+", ReportOutcome.Canceled,
                "Brennvorgang wurde abgebrochen.", ReportCheckOutcome.NotRun, null);
            BurnLog.AppendText($"Abschlussbericht: {_lastStandardCompletionReportPath}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            BurnStatusText.Text = "Brennvorgang fehlgeschlagen.";
            BurnStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            BurnLog.AppendText("FEHLER: " + ex.Message);
            _lastStandardCompletionReportPath = WriteStandardCompletionReport(
                "Brennen & Verify+", ReportOutcome.Failed,
                ex.Message, ReportCheckOutcome.NotRun, ex.Message);
            BurnLog.AppendText($"{Environment.NewLine}Abschlussbericht: {_lastStandardCompletionReportPath}{Environment.NewLine}");
            MessageBox.Show(ex.Message, "DiscForge Brennfehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBurnUi(false);
            _burnCts?.Dispose();
            _burnCts = null;
            _lastSignature = string.Empty;
        }
    }

    private void CancelBurn_Click(object sender, RoutedEventArgs e)
    {
        _burnCts?.Cancel();
        _burnService.Cancel();
    }

    private void SetBurnUi(bool busy)
    {
        _mediaTimer.IsEnabled = !busy;
        BurnButton.IsEnabled = !busy && _preflightPassed;
        PreflightButton.IsEnabled = !busy;
        CancelBurnButton.IsEnabled = busy;
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } drive)
        {
            MessageBox.Show("Bitte zuerst ein Laufwerk auswählen.", "Verify+", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_items.Count == 0)
        {
            MessageBox.Show("Die Zusammenstellung ist leer.", "Verify+", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        VerifyButton.IsEnabled = false;
        SaveVerifyReportButton.IsEnabled = false;
        BurnLog.Clear();
        BurnProgress.Value = 0;
        BurnStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        BurnStatusText.Text = "Verify+ wird vorbereitet...";
        _standardOperationStarted = DateTime.Now;
        var verifyCts = new CancellationTokenSource();
        var reporter = new Progress<(int Percent, string Message)>(update =>
        {
            BurnProgress.Value = update.Percent;
            BurnStatusText.Text = update.Message;
            BurnLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {update.Message}{Environment.NewLine}");
});

        try
        {
            _burnSnapshot ??= _snapshotService.LoadLast();
            if (_burnSnapshot is null)
                throw new InvalidOperationException("Kein kompatibler Verify+-Snapshot vorhanden. Bitte die Disc mit der aktuellen DiscForge-Studio-Version brennen.");

            _lastVerification = await Task.Run(() => _verifyService.VerifyAsync(drive.Name, _burnSnapshot, reporter, verifyCts.Token));
            SaveVerifyReportButton.IsEnabled = true;
            _lastStandardCompletionReportPath = WriteStandardCompletionReport(
                "Verify+", _lastVerification.Success ? ReportOutcome.Succeeded : ReportOutcome.Failed,
                _lastVerification.Success
                    ? $"Verify+ bestanden: {_lastVerification.MatchingFiles}/{_lastVerification.TotalFiles} Dateien stimmen ueberein."
                    : $"Verify+ nicht bestanden: {_lastVerification.FailedFiles} Abweichung(en).",
                _lastVerification.Success ? ReportCheckOutcome.Passed : ReportCheckOutcome.Failed,
                null);
            BurnLog.AppendText($"Abschlussbericht: {_lastStandardCompletionReportPath}{Environment.NewLine}");
            if (_lastVerification.Success)
            {
                BurnStatusText.Text = $"Verify+ bestanden: {_lastVerification.MatchingFiles}/{_lastVerification.TotalFiles} Dateien stimmen überein.";
                BurnStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                BurnLog.AppendText($"ERGEBNIS: Alle {_lastVerification.TotalFiles} Dateien stimmen per SHA-256 überein.{Environment.NewLine}");
                foreach (var changed in _lastVerification.Files.Where(file => file.OriginalStatus != "Original unverändert"))
                    BurnLog.AppendText($"HINWEIS: {changed.RelativePath} - {changed.OriginalStatus}{Environment.NewLine}");
                MessageBox.Show("Verify+ erfolgreich: Alle Dateien stimmen mit den Originalen überein.", "DiscForge Verify+", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BurnStatusText.Text = $"Verify+ fehlgeschlagen: {_lastVerification.FailedFiles} Abweichung(en).";
                BurnStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
                foreach (var failed in _lastVerification.Files.Where(file => file.DiscStatus != "OK"))
                    BurnLog.AppendText($"DISC-FEHLER: {failed.RelativePath} - {failed.DiscStatus}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            BurnStatusText.Text = "Verify+ konnte nicht abgeschlossen werden.";
            BurnStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            BurnLog.AppendText("FEHLER: " + ex.Message);
            MessageBox.Show(ex.Message, "DiscForge Verify+", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            verifyCts.Dispose();
            VerifyButton.IsEnabled = true;
        }
    }

    private string WriteStandardCompletionReport(
        string operation,
        ReportOutcome outcome,
        string summary,
        ReportCheckOutcome verifyOutcome,
        string? error)
    {
        var finished = DateTime.Now;
        var started = _standardOperationStarted == default ? finished : _standardOperationStarted;
        var drive = Selected;
        var snapshot = _burnSnapshot;
        var verification = _lastVerification;
        var totalBytes = snapshot?.Files.Sum(file => file.Size) ?? _items.Sum(item => item.Size);
        var metrics = new List<ReportMetric>
        {
            new("Dateien", (snapshot?.Files.Count ?? _items.Count).ToString()),
            new("Datenmenge", FormatBytes(totalBytes)),
            new("Disc abgeschlossen", CloseMediaCheck.IsChecked == true ? "Ja" : "Nein"),
            new("Auswerfen angefordert", EjectAfterBurnCheck.IsChecked == true ? "Ja" : "Nein")
        };
        if (verification is not null)
        {
            metrics.Add(new("Verify+ uebereinstimmend", verification.MatchingFiles.ToString()));
            metrics.Add(new("Verify+ Abweichungen", verification.FailedFiles.ToString()));
            metrics.Add(new("Geaenderte Originale", verification.ChangedOriginalFiles.ToString()));
        }
        var checks = new List<ReportCheck>
        {
            new("Vorpruefung", _preflightPassed ? ReportCheckOutcome.Passed : ReportCheckOutcome.NotRun),
            new("Brennvorgang", operation == "Verify+" ? ReportCheckOutcome.NotRun :
                outcome == ReportOutcome.Succeeded ? ReportCheckOutcome.Passed :
                outcome == ReportOutcome.Canceled ? ReportCheckOutcome.Warning : ReportCheckOutcome.Failed),
            new("Verify+", verifyOutcome, verification is null ? "Keine Pruefung vorhanden." : summary)
        };
        var logs = new List<ReportLogEntry>();
        if (!string.IsNullOrWhiteSpace(error))
            logs.Add(new(finished, ReportEntryLevel.Error, "Vorgang fehlgeschlagen", error));
        var report = new CompletionReport(
            Guid.NewGuid(), operation, outcome, started, finished,
            ApplicationVersionService.Current,
            snapshot is null ? null : "Datei-Zusammenstellung",
            drive?.Name,
            drive?.MediaType,
            snapshot?.DiscLabel ?? DiscLabelInput.Text.Trim(),
            summary,
            new[] { new ReportSection("Ergebnis", metrics, checks, Array.Empty<string>()) },
            logs);
        return _completionReportTextService.Write(report);
    }
    private void SaveVerifyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastVerification is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "Verify+ Bericht (*.txt)|*.txt",
            FileName = $"DiscForge-Verify-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var report = new StringBuilder()
            .AppendLine(ApplicationVersionService.ReportTitle("Verify+ Bericht"))
            .AppendLine($"Start: {_lastVerification.Started:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Ende: {_lastVerification.Finished:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Laufwerk: {_lastVerification.Drive}")
            .AppendLine($"Dateien gesamt: {_lastVerification.TotalFiles}")
            .AppendLine($"Uebereinstimmend: {_lastVerification.MatchingFiles}")
            .AppendLine($"Disc-Abweichungen: {_lastVerification.FailedFiles}")
            .AppendLine($"Nachträglich geänderte Originale: {_lastVerification.ChangedOriginalFiles}")
            .AppendLine($"Snapshot vom: {_lastVerification.SnapshotCreated:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Ergebnis: {(_lastVerification.Success ? "BESTANDEN" : "NICHT BESTANDEN")}")
            .AppendLine();
        foreach (var file in _lastVerification.Files)
        {
            report.AppendLine($"[Disc: {file.DiscStatus}] [Original: {file.OriginalStatus}] {file.RelativePath}");
            report.AppendLine($"  Snapshot SHA-256: {file.SnapshotHash}");
            report.AppendLine($"  Aktuelles Original: {file.CurrentOriginalHash}");
            report.AppendLine($"  Disc SHA-256:       {file.DiscHash}");
        }
        File.WriteAllText(dialog.FileName, report.ToString(), Encoding.UTF8);
        FooterStatus.Text = "Verify+ Prüfbericht wurde gespeichert.";
    }

    private async void InspectMultisession_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } drive) { MessageBox.Show("Bitte zuerst ein Laufwerk auswählen."); return; }
        InspectMultisessionButton.IsEnabled = false; SaveInspectorReportButton.IsEnabled = false;
        InspectorStatusText.Text = "Analysiere..."; InspectorFileSystemText.Text = "-"; InspectorFreeText.Text = "-"; InspectorSessionCountText.Text = "-"; InspectorUsedText.Text = "-"; InspectorFileCountText.Text = "-"; InspectorFilesList.ItemsSource = null; InspectorLog.Clear();
        try
        {
            _lastInspection = await _multisessionInspector.InspectAsync(drive.Name, CancellationToken.None);
            InspectorStatusText.Text = _lastInspection.AppendCandidate ? "Anhängen grundsätzlich möglich" : _lastInspection.Status;
            InspectorStatusText.Foreground = _lastInspection.AppendCandidate ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Salmon;
            InspectorFileSystemText.Text = _lastInspection.FileSystem;
            InspectorFreeText.Text = FormatBytes(_lastInspection.FreeBytes);
            InspectorSessionCountText.Text = _lastInspection.SessionCount.ToString();
            InspectorUsedText.Text = FormatBytes(_lastInspection.UsedBytes);
            InspectorFileCountText.Text = _lastInspection.FileCount.ToString();
            InspectorFilesList.ItemsSource = _lastInspection.Files;
            InspectorLog.Text = $"Zeit: {_lastInspection.Inspected:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}Laufwerk: {_lastInspection.Drive}{Environment.NewLine}Medientyp: {_lastInspection.MediaType}{Environment.NewLine}Sitzungsschnittstellen: {_lastInspection.SessionCount}{Environment.NewLine}Gesamtkapazitaet: {FormatBytes(_lastInspection.TotalBytes)}{Environment.NewLine}Belegt: {FormatBytes(_lastInspection.UsedBytes)}{Environment.NewLine}Dateisystem: {_lastInspection.FileSystem}{Environment.NewLine}Dateien sichtbar: {_lastInspection.FileCount}{Environment.NewLine}Freier Speicher: {FormatBytes(_lastInspection.FreeBytes)}{Environment.NewLine}Freie Sektoren: {_lastInspection.FreeSectors:N0}{Environment.NewLine}Messquelle: {_lastInspection.FreeSpaceSource}{Environment.NewLine}{_lastInspection.Details}";
            SaveInspectorReportButton.IsEnabled = true;
            _appendPreflightPassed = false; AppendSessionButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            InspectorStatusText.Text = "Nicht für Multisession nutzbar"; InspectorStatusText.Foreground = System.Windows.Media.Brushes.Salmon; InspectorLog.Text = ex.Message;
        }
        finally { InspectMultisessionButton.IsEnabled = true; }
    }

    private void SaveInspectorReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastInspection is null) return;
        var dialog = new SaveFileDialog { Filter = "Multisession-Bericht (*.txt)|*.txt", FileName = $"DiscForge-Multisession-{DateTime.Now:yyyyMMdd-HHmm}.txt" };
        if (dialog.ShowDialog() != true) return;
        var report = new StringBuilder().AppendLine(ApplicationVersionService.ReportTitle("Multisession Inspector")).AppendLine($"Analyse: {_lastInspection.Inspected:yyyy-MM-dd HH:mm:ss}").AppendLine($"Laufwerk: {_lastInspection.Drive}").AppendLine($"Status: {_lastInspection.Status}").AppendLine($"Anhängen grundsätzlich möglich: {_lastInspection.AppendCandidate}").AppendLine($"Dateisystem: {_lastInspection.FileSystem}").AppendLine($"Medientyp: {_lastInspection.MediaType}").AppendLine($"Sitzungsschnittstellen: {_lastInspection.SessionCount}").AppendLine($"Gesamtkapazitaet: {FormatBytes(_lastInspection.TotalBytes)}").AppendLine($"Belegt: {FormatBytes(_lastInspection.UsedBytes)}").AppendLine($"Freier Speicher: {FormatBytes(_lastInspection.FreeBytes)}").AppendLine($"Freie Sektoren: {_lastInspection.FreeSectors:N0}").AppendLine($"Messquelle: {_lastInspection.FreeSpaceSource}").AppendLine($"Dateien: {_lastInspection.FileCount}").AppendLine().AppendLine("Vorhandene Dateien:");
        foreach (var file in _lastInspection.Files) report.AppendLine(file);
        File.WriteAllText(dialog.FileName, report.ToString(), Encoding.UTF8); FooterStatus.Text = "Multisession-Analysebericht gespeichert.";
    }

    private bool RunSafetyCheck()
    {
        var findings = _safetyScan.Scan(_items);
        if (findings.Count == 0) return true;

        var preview = string.Join(Environment.NewLine,
            findings.Take(12).Select(finding => $"- {finding.Item.RelativePath} ({finding.Reason})"));
        if (findings.Count > 12)
            preview += $"{Environment.NewLine}- ... und {findings.Count - 12} weitere";

        var message =
            $"DiscForge hat {findings.Count} verdaechtige Projekt- oder Builddatei(en) erkannt:{Environment.NewLine}{Environment.NewLine}" +
            preview +
            $"{Environment.NewLine}{Environment.NewLine}JA: Verdaechtige Dateien aus der Zusammenstellung entfernen" +
            $"{Environment.NewLine}NEIN: Bewusst beibehalten und fortfahren" +
            $"{Environment.NewLine}ABBRECHEN: Vorpruefung stoppen";

        var answer = MessageBox.Show(message, "DiscForge Sicherheitspruefung",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No)
        {
            var overrideAnswer = MessageBox.Show(
                "Die Zusammenstellung enthält weiterhin Projekt-, Build- oder Sicherungsdateien. " +
                "Diese Dateien können viel Speicher belegen und sind normalerweise nicht für die Disc bestimmt." +
                Environment.NewLine + Environment.NewLine +
                "Trotzdem bewusst mit allen verdächtigen Dateien fortfahren?",
                "Sicherheitsfilter wirklich übergehen?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            return overrideAnswer == MessageBoxResult.Yes;
        }

        foreach (var item in findings.Select(finding => finding.Item).Distinct().ToList())
            _items.Remove(item);

        UpdateCapacity();
        FooterStatus.Text = $"{findings.Count} verdaechtige Datei(en) entfernt.";
        return _items.Count > 0;
    }
    private void PreflightAppend_Click(object sender, RoutedEventArgs e)
    {
        if (!RunSafetyCheck()) return;
        _appendPreflightPassed = false; AppendSessionButton.IsEnabled = false;
        var problems = new List<string>();
        if (_lastInspection is null || !_lastInspection.AppendCandidate) problems.Add("Die Disc muss zuerst erfolgreich analysiert werden.");
        if (_items.Count == 0) problems.Add("Es wurden keine neuen Dateien zusammengestellt.");
        foreach (var item in _items.Where(item => !File.Exists(item.FullPath))) problems.Add($"Quelldatei fehlt: {item.FullPath}");
        var bytes = _items.Sum(item => item.Size);
        if (_lastInspection is not null && bytes > _lastInspection.FreeBytes) problems.Add("Die neuen Daten überschreiten den freien Speicher der Disc.");
        if (_lastInspection is not null)
        {
            var existing = new HashSet<string>(_lastInspection.Files.Select(path => path.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items.Where(item => existing.Contains(item.RelativePath.Replace('/', '\\')))) problems.Add($"Namenskonflikt: {item.RelativePath}");
        }
        if (problems.Count > 0) { InspectorLog.Text = string.Join(Environment.NewLine, problems.Select(problem => "- " + problem)); InspectorStatusText.Text = "Anhängen nicht freigegeben"; InspectorStatusText.Foreground = System.Windows.Media.Brushes.Salmon; return; }
        _appendPreflightPassed = true; AppendSessionButton.IsEnabled = true; InspectorStatusText.Text = "Neue Sitzung bereit"; InspectorStatusText.Foreground = System.Windows.Media.Brushes.LightGreen; InspectorLog.Text = $"Vorprüfung bestanden.{Environment.NewLine}Neue Dateien: {_items.Count}{Environment.NewLine}Datenmenge: {FormatBytes(bytes)}{Environment.NewLine}Danach endgültig abschliessen: {(FinalizeAfterAppendCheck.IsChecked == true ? "Ja" : "Nein")}";
    }

    private async void AppendSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_appendPreflightPassed || Selected is not { } drive) return;
        var warning = FinalizeAfterAppendCheck.IsChecked == true ? "Die neue Sitzung wird geschrieben und die Disc danach ENDGUELTIG abgeschlossen. Weitere Sitzungen sind dann nicht möglich." : "Die neue Sitzung wird geschrieben. Die Disc bleibt für weitere Sitzungen offen.";
        if (MessageBox.Show(warning + "\n\nFortfahren?", "Multisession schreiben", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _appendCts = new CancellationTokenSource(); SetAppendUi(true); InspectorLog.Clear();
        var reporter = new Progress<(int Percent,string Message)>(update => { InspectorStatusText.Text = update.Message; AppendLiveStatus.Update(update.Message, update.Percent, update.Message); InspectorLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {update.Message}{Environment.NewLine}"); });
        try
        {
            if (_lastInspection is null) throw new InvalidOperationException("Die Disc muss vor dem Schreiben analysiert werden.");
            _appendSnapshot = await _multisessionVerify.CreateSnapshotAsync(
                drive.Name,
                _lastInspection.FreeBytes,
                _lastInspection.Files,
                _items.ToList(),
                FinalizeAfterAppendCheck.IsChecked == true,
                reporter,
                _appendCts.Token);

            await _multisessionWriter.AppendAsync(drive.Name, _items.ToList(), FinalizeAfterAppendCheck.IsChecked == true, EjectAfterAppendCheck.IsChecked == true, reporter, _appendCts.Token);
            _multisessionVerify.Save(_appendSnapshot);
            _sessionHistory.AddWrittenSession(_appendSnapshot);
            RefreshSessionHistory();
            InspectorLog.AppendText($"Multisession Verify+-Snapshot gespeichert: {_appendSnapshot.Created:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            InspectorStatusText.Text = "Neue Sitzung erfolgreich geschrieben"; AppendLiveStatus.Update("Neue Sitzung erfolgreich geschrieben", 100, "Die neue Multisession-Sitzung wurde vollständig geschrieben."); InspectorStatusText.Foreground = System.Windows.Media.Brushes.LightGreen; _lastInspection = null; _appendPreflightPassed = false; AppendSessionButton.IsEnabled = false;
            MessageBox.Show("Die neue Sitzung wurde erfolgreich geschrieben.", "DiscForge Multisession", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { InspectorStatusText.Text = "Anhängen abgebrochen"; }
        catch (Exception ex) { InspectorStatusText.Text = "Anhängen fehlgeschlagen"; InspectorStatusText.Foreground = System.Windows.Media.Brushes.Salmon; InspectorLog.AppendText("FEHLER: " + ex.Message); MessageBox.Show(ex.Message, "DiscForge Multisession", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetAppendUi(false); _appendCts?.Dispose(); _appendCts=null; }
    }

    private void SetAppendUi(bool busy)
    {
        InspectMultisessionButton.IsEnabled = !busy; PreflightAppendButton.IsEnabled = !busy; AppendSessionButton.IsEnabled = !busy && _appendPreflightPassed; FinalizeAfterAppendCheck.IsEnabled = !busy; EjectAfterAppendCheck.IsEnabled = !busy;
    }

    private async void VerifyAppend_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } drive) { MessageBox.Show("Bitte zuerst ein Laufwerk auswählen."); return; }
        _appendSnapshot ??= _multisessionVerify.Load();
        if (_appendSnapshot is null) { MessageBox.Show("Kein kompatibler Multisession-Verify+-Snapshot vorhanden. Bitte zuerst mit der aktuellen DiscForge-Studio-Version eine Sitzung schreiben."); return; }
        VerifyAppendButton.IsEnabled=false; SaveAppendVerifyReportButton.IsEnabled=false; InspectorLog.Clear();
        var reporter=new Progress<(int Percent,string Message)>(u=>{InspectorStatusText.Text=u.Message;InspectorLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {u.Message}{Environment.NewLine}");InspectorLog.ScrollToEnd();});
        try
        {
            var inspection=await _multisessionInspector.InspectAsync(drive.Name,CancellationToken.None);
            _lastAppendVerification=await _multisessionVerify.VerifyAsync(drive.Name,_appendSnapshot,inspection.FreeBytes,reporter,CancellationToken.None);
            SaveAppendVerifyReportButton.IsEnabled=true;
            _sessionHistory.ApplyVerification(_appendSnapshot, _lastAppendVerification);
            RefreshSessionHistory();
            if(_lastAppendVerification.Success)
            {
                InspectorStatusText.Text=$"Multisession Verify+ bestanden: {_lastAppendVerification.NewFilesMatching}/{_lastAppendVerification.NewFilesTotal} neue Dateien korrekt";
            // RESPONSIVE-PATCH-3.1: eindeutiger Lesestatus
            FooterStatus.Text = "Multisession Verify+ bestanden. Nur Lesetest, keine Daten geschrieben.";
                InspectorStatusText.Foreground=System.Windows.Media.Brushes.LightGreen;
                InspectorLog.AppendText($"ERGEBNIS: Neue Dateien stimmen per SHA-256 überein. Frühere Dateien weiterhin sichtbar.{Environment.NewLine}Freier Speicher vorher: {FormatBytes(_lastAppendVerification.FreeBytesBefore)}{Environment.NewLine}Freier Speicher nachher: {FormatBytes(_lastAppendVerification.FreeBytesAfter)}{Environment.NewLine}");
            }
            else
            {
                InspectorStatusText.Text="Multisession Verify+ nicht bestanden";InspectorStatusText.Foreground=System.Windows.Media.Brushes.Salmon;
                foreach(var file in _lastAppendVerification.NewFiles.Where(file=>file.Status!="OK")) InspectorLog.AppendText($"FEHLER: {file.RelativePath} - {file.Status}{Environment.NewLine}");
                foreach(var file in _lastAppendVerification.MissingPreviousFiles) InspectorLog.AppendText($"FEHLER: Frühere Datei fehlt: {file}{Environment.NewLine}");
            }
        }
        catch(Exception ex){InspectorStatusText.Text="Multisession Verify+ fehlgeschlagen";InspectorStatusText.Foreground=System.Windows.Media.Brushes.Salmon;InspectorLog.AppendText("FEHLER: "+ex.Message);}
        finally{VerifyAppendButton.IsEnabled=true;}
    }

    private void SaveAppendVerifyReport_Click(object sender, RoutedEventArgs e)
    {
        if(_lastAppendVerification is null)return;
        var dialog=new SaveFileDialog{Filter="Multisession Verify+ Bericht (*.txt)|*.txt",FileName=$"DiscForge-Multisession-Verify-{DateTime.Now:yyyyMMdd-HHmm}.txt"};
        if(dialog.ShowDialog()!=true)return;
        var r=_lastAppendVerification;var report=new StringBuilder().AppendLine(ApplicationVersionService.ReportTitle("Multisession Verify+")).AppendLine($"Snapshot: {r.SnapshotCreated:yyyy-MM-dd HH:mm:ss}").AppendLine($"Prüfung: {r.Finished:yyyy-MM-dd HH:mm:ss}").AppendLine($"Laufwerk: {r.Drive}").AppendLine($"Neue Dateien: {r.NewFilesTotal}").AppendLine($"Korrekt: {r.NewFilesMatching}").AppendLine($"Fehlerhaft: {r.NewFilesFailed}").AppendLine($"Fehlende frühere Dateien: {r.PreviousFilesMissing}").AppendLine($"Freier Speicher vorher: {FormatBytes(r.FreeBytesBefore)}").AppendLine($"Freier Speicher nachher: {FormatBytes(r.FreeBytesAfter)}").AppendLine($"Ergebnis: {(r.Success?"BESTANDEN":"NICHT BESTANDEN")}").AppendLine();
        foreach(var file in r.NewFiles){report.AppendLine($"[{file.Status}] {file.RelativePath}");report.AppendLine($"  Erwartet: {file.ExpectedHash}");report.AppendLine($"  Disc:     {file.DiscHash}");}
        foreach(var file in r.MissingPreviousFiles)report.AppendLine($"[FRUEHERE DATEI FEHLT] {file}");
        File.WriteAllText(dialog.FileName,report.ToString(),Encoding.UTF8);FooterStatus.Text="Multisession Verify+-Bericht gespeichert.";
    }

    private void RefreshSessionHistory()
    {
        if (SessionHistoryList is null) return;
        var entries = _sessionHistory.Load();
        SessionHistoryList.ItemsSource = entries;
            if (SessionHistoryList.Items.Count > 0)
                SessionHistoryList.SelectedIndex = 0;
        if (entries.Count == 0)
        {
            SessionHistoryDetails.Text = "Noch keine Multisession-Sitzung gespeichert.";
            SaveSessionHistoryReportButton.IsEnabled = false;
        }
    }

    private void RefreshSessionHistory_Click(object sender, RoutedEventArgs e) => RefreshSessionHistory();

    private void SessionHistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedHistoryEntry = SessionHistoryList.SelectedItem as SessionHistoryEntry;
        if (_selectedHistoryEntry is null)
        {
            SessionHistoryDetails.Clear();
            SaveSessionHistoryReportButton.IsEnabled = false;
            return;
        }
        SessionHistoryDetails.Text = _sessionHistory.BuildReport(_selectedHistoryEntry);
        SaveSessionHistoryReportButton.IsEnabled = true;
    }

    private void SaveSessionHistoryReport_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedHistoryEntry is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "DiscForge-Sitzungsbericht (*.txt)|*.txt",
            FileName = $"DiscForge-Sitzung-{_selectedHistoryEntry.SessionNumber:000}-{_selectedHistoryEntry.WrittenAt:yyyyMMdd-HHmm}.txt"
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, _sessionHistory.BuildReport(_selectedHistoryEntry), Encoding.UTF8);
        FooterStatus.Text = "Sitzungsbericht gespeichert.";
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();
    private void RefreshDrives(bool automatic = false)
    {
        try
        {
            var selectedName = Selected?.Name; var drives = _service.Scan();
            var signature = string.Join("|", drives.Select(d => $"{d.Name}:{d.IsReady}:{d.VolumeLabel}:{d.TotalBytes}"));
            if (automatic && signature == _lastSignature) return;
            _lastSignature = signature; DriveList.ItemsSource = drives;
            DriveList.SelectedItem = drives.FirstOrDefault(d => d.Name == selectedName) ?? drives.FirstOrDefault();
            FooterStatus.Text = drives.Count == 0 ? "Kein optisches Laufwerk gefunden." : automatic ? "Medium automatisch neu erkannt." : $"{drives.Count} optische(s) Laufwerk(e) gefunden.";
        }
        catch (Exception ex) { if (!automatic) MessageBox.Show(ex.Message, "DiscForge Diagnose", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } d) return;
        BurnDriveText.Text = d.Name;
        InspectorDriveText.Text = d.Name;
        StatusText.Text=d.StatusMessage; MediaTypeText.Text=d.MediaType; FileSystemText.Text=d.FileSystem; ProtectionText.Text=d.ProtectionStatus; VolumeLabelText.Text=d.VolumeLabel; CapacityText.Text=FormatBytes(d.TotalBytes); FreeSpaceText.Text=d.FreeSpaceDisplay;
    }
    private void Eject_Click(object sender, RoutedEventArgs e) => RunTray(d => _service.Eject(d.Name), "Laufwerk wurde geöffnet.");
    private void Close_Click(object sender, RoutedEventArgs e) => RunTray(d => _service.Close(d.Name), "Schliessen wurde angefordert.");
    private void RunTray(Action<OpticalDriveInfo> action, string message) { if (Selected is not { } d) return; try { action(d); FooterStatus.Text=message; _lastSignature=string.Empty; } catch(Exception ex) { MessageBox.Show(ex.Message); } }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } d) { MessageBox.Show("Bitte zuerst ein Laufwerk auswählen."); return; }
        var now=DateTime.Now; var dialog=new SaveFileDialog { Filter="Textbericht (*.txt)|*.txt", FileName=$"DiscForge-Diagnose-{now:yyyyMMdd-HHmm}.txt" }; if(dialog.ShowDialog()!=true)return;
        var report=$"{ApplicationVersionService.ReportTitle("Diagnosebericht")}\nAnalysezeitpunkt: {now:yyyy-MM-dd HH:mm:ss}\nLaufwerk: {d.Name}\nMedium: {d.MediaType}\nDatenträgername: {d.VolumeLabel}\nDateisystem: {d.FileSystem}\nKapazität: {FormatBytes(d.TotalBytes)}\nFreier Speicher: {d.FreeSpaceDisplay}\nSchutzstatus: {d.ProtectionStatus}\nStatus: {d.StatusMessage}\nHinweis: Kopierschutz wird nicht umgangen.\n";
        File.WriteAllText(dialog.FileName,report,Encoding.UTF8); FooterStatus.Text="Diagnosebericht wurde gespeichert.";
    }

    private static string FormatBytes(long value) => value <= 0 ? "0 MB" : value >= 1_000_000_000 ? $"{value / 1_000_000_000d:0.00} GB" : $"{value / 1_000_000d:0.0} MB";
}



