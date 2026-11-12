using System.IO;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows;
using DiscForgeStudio.Services;
using Microsoft.Win32;

namespace DiscForgeStudio;

public partial class MainWindow
{
    private string? _discImageOutputPath;
    private readonly DiscImageReadService _discImageReadService = new();
    private CancellationTokenSource? _discImageCts;
    private DiscImageReadResult? _lastDiscImageResult;
    private bool _discImagePreflightPassed;
    private readonly OperationReportService _operationReportService = new();
    private DateTime _discImageOperationStarted;
    private string? _lastDiscImageReportPath;

    private void DiscToImageTab_Loaded(object sender, RoutedEventArgs e) { RefreshDiscImageSource(); InitializeDiscImageDev2Actions(); UpdateDiscImageActionButtons(); }
    private void RefreshDiscImageSource_Click(object sender, RoutedEventArgs e) { RefreshDrives(); RefreshDiscImageSource(); }

    private void RefreshDiscImageSource()
    {
        DiscImageProgressBar.Visibility = Visibility.Visible;
        _discImagePreflightPassed=false; DiscImageStartButton.IsEnabled=false; DiscImageVerifyButton.IsEnabled=false; UpdateDiscImageActionButtons();
        if (Selected is not { } drive) { DiscImageDriveText.Text="Kein Laufwerk ausgewaehlt.";DiscImageStatusText.Text="Bitte zuerst ein Laufwerk auswaehlen.";return; }
        DiscImageDriveText.Text=drive.IsReady?$"{drive.Name}  |  {drive.MediaType}  |  {drive.VolumeLabel}":$"{drive.Name}  |  Bitte eine Daten-Disc einlegen.";
        DiscImageStatusText.Text=drive.IsReady?"Disc erkannt. Waehle den ISO-Speicherort.":"Keine lesbare Disc erkannt.";
        DiscImageProgressBar.Value=drive.IsReady?20:5;
        DiscImageDetailsTextBox.Text=$"Laufwerk: {drive.Name}\nMedium: {drive.MediaType}\nDateisystem: {drive.FileSystem}\nLesbare Groesse: {FormatBytes(drive.TotalBytes)}\nSchutzstatus: {drive.ProtectionStatus}";
    }

    private void ChooseDiscImagePath_Click(object sender, RoutedEventArgs e)
    {
        var label=Selected?.VolumeLabel;var safe=string.IsNullOrWhiteSpace(label)?"DiscForge-Image":string.Concat(label.Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch));
        var dialog=new SaveFileDialog{Filter="ISO-Image (*.iso)|*.iso",FileName=$"{safe}-{DateTime.Now:yyyyMMdd-HHmm}.iso",Title="Speicherort fuer das Disc-Image waehlen"};
        if(dialog.ShowDialog()!=true)return;_discImageOutputPath=dialog.FileName;DiscImagePathTextBox.Text=dialog.FileName;DiscImageStatusText.Text="Speicherort gewaehlt. Starte die Disc-Pruefung.";DiscImageProgressBar.Value=40;_discImagePreflightPassed=false;DiscImageStartButton.IsEnabled=false;UpdateDiscImageActionButtons();
    }

    private void DiscImagePreflight_Click(object sender,RoutedEventArgs e)
    {
        var problems=new List<string>();if(Selected is not { } drive)problems.Add("Kein optisches Laufwerk ausgewaehlt.");else{if(!drive.IsReady)problems.Add("Keine lesbare Disc eingelegt.");if(drive.TotalBytes<=0)problems.Add("Die Disc-Groesse konnte nicht bestimmt werden.");if(drive.FileSystem=="-")problems.Add("Kein unterstuetztes Daten-Dateisystem erkannt.");}
        if(string.IsNullOrWhiteSpace(_discImageOutputPath))problems.Add("Noch kein ISO-Speicherort gewaehlt.");
        long free=0;if(!string.IsNullOrWhiteSpace(_discImageOutputPath)){try{var root=Path.GetPathRoot(_discImageOutputPath);if(string.IsNullOrWhiteSpace(root))problems.Add("Der Zielpfad ist ungueltig.");else{free=new DriveInfo(root).AvailableFreeSpace;if(Selected is { } s&&free<s.TotalBytes+16_777_216)problems.Add("Am Ziel ist nicht genug freier Speicher vorhanden.");}}catch(Exception ex){problems.Add("Zielspeicher konnte nicht geprueft werden: "+ex.Message);}}
        // DEV11-SCHUTZREAKTION-V2: Vor der Freigabe passiv pruefen, niemals umgehen.
        if (Selected is { } protectionDrive && IsDiscProtectionDetectedOrSuspected(protectionDrive.ProtectionStatus))
        {
            ApplyDiscProtectionReaction(protectionDrive);
            return;
        }
        DiscImageSpaceText.Text="Freier Zielspeicher: "+FormatBytes(free);
        if(problems.Count>0){_discImagePreflightPassed=false;DiscImageStartButton.IsEnabled=false;UpdateDiscImageActionButtons();DiscImageStatusText.Text="Pruefung nicht bestanden.";DiscImageStatusText.Foreground=System.Windows.Media.Brushes.Salmon;DiscImageProgressBar.Value=0;DiscImageDetailsTextBox.Text=string.Join(Environment.NewLine,problems.Select(p=>"- "+p));return;}
        _discImagePreflightPassed=true;DiscImageStartButton.IsEnabled=true;DiscImageStatusText.Foreground=(System.Windows.Media.Brush)FindResource("AccentBrush");DiscImageStatusText.Text="Vorpruefung bestanden. Image kann erstellt werden.";DiscImageProgressBar.Value=100;DiscImageDetailsTextBox.Text+=$"\nZiel: {_discImageOutputPath}\nFreier Zielspeicher: {FormatBytes(free)}\nErgebnis: VORPRUEFUNG BESTANDEN";
    }

    private async void DiscImageStart_Click(object sender,RoutedEventArgs e)
    {
        if(!_discImagePreflightPassed||Selected is not { } drive||string.IsNullOrWhiteSpace(_discImageOutputPath))return;
        // DEV11-SCHUTZREAKTION-V2: Schutzstatus unmittelbar vor dem Lesen erneut pruefen.
        if (IsDiscProtectionDetectedOrSuspected(drive.ProtectionStatus))
        {
            ApplyDiscProtectionReaction(drive);
            return;
        }
        if(MessageBox.Show("Disc jetzt ausschliesslich lesend als ISO sichern?", "DiscForge Disc zu Image",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        _discImageCts=new CancellationTokenSource();_discImageOperationStarted=DateTime.Now;SetDiscImageBusy(true);var progress=new Progress<(int Percent,string Message)>(u=>{DiscImageProgressBar.Value=u.Percent;DiscImageStatusText.Text=u.Message;});
        try{_lastDiscImageResult=await _discImageReadService.ReadAsync(drive.Name,_discImageOutputPath,drive.TotalBytes,progress,_discImageCts.Token); SavePersistentDiscImageState();DiscImageStatusText.Text="Image erfolgreich erstellt.";DiscImageProgressBar.Value=100;DiscImageVerifyButton.IsEnabled=true;DiscImageDetailsTextBox.Text+=$"\nImage: {_lastDiscImageResult.ImagePath}\nGroesse: {FormatBytes(_lastDiscImageResult.BytesRead)}\nSHA-256: {_lastDiscImageResult.Sha256}";
            _lastDiscImageReportPath=WriteDiscImageReport("ERFOLGREICH",drive,_lastDiscImageResult.ImagePath,_lastDiscImageResult.BytesRead,_lastDiscImageResult.Sha256,"Noch nicht ausgefuehrt","Image erfolgreich erstellt."); SavePersistentDiscImageState();
            DiscImageDetailsTextBox.Text+=$"\nBericht: {_lastDiscImageReportPath}";
            UpdateDiscImageActionButtons();
            MessageBox.Show($"Die ISO-Datei wurde erfolgreich erstellt.\n\nBericht: {_lastDiscImageReportPath}","DiscForge",MessageBoxButton.OK,MessageBoxImage.Information);}
        catch(OperationCanceledException){
            DiscImageStatusText.Text="Image-Erstellung abgebrochen. Unvollstaendige Datei wurde entfernt.";
            _lastDiscImageReportPath=WriteDiscImageReport("ABGEBROCHEN",drive,_discImageOutputPath,0,null,"Nicht ausgefuehrt","Image-Erstellung abgebrochen. Unvollstaendige Datei wurde entfernt."); SavePersistentDiscImageState();
            DiscImageDetailsTextBox.Text+=$"\nBericht: {_lastDiscImageReportPath}";
        }
        catch(Exception ex){
            DiscImageStatusText.Text="Image-Erstellung fehlgeschlagen.";
            DiscImageStatusText.Foreground=System.Windows.Media.Brushes.Salmon;
            _lastDiscImageReportPath=WriteDiscImageReport("FEHLGESCHLAGEN",drive,_discImageOutputPath,0,null,"Nicht ausgefuehrt",ex.Message); SavePersistentDiscImageState();
            DiscImageDetailsTextBox.Text+="\nFEHLER: "+ex.Message+$"\nBericht: {_lastDiscImageReportPath}";
            MessageBox.Show(ex.Message+$"\n\nBericht: {_lastDiscImageReportPath}","DiscForge Disc zu Image",MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally{SetDiscImageBusy(false);_discImageCts?.Dispose();_discImageCts=null;}
    }

    // DEV11-SCHUTZREAKTION-V2
    private const string DiscProtectionMessage = "Technische Schutzmaßnahme erkannt oder vermutet.\nDiscForge hat den Vorgang beendet und keine Umgehung vorgenommen.";

    private static bool IsDiscProtectionDetectedOrSuspected(string? protectionStatus)
    {
        if (string.IsNullOrWhiteSpace(protectionStatus)) return false;
        var status = protectionStatus.Trim();
        return !status.Equals("-", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("Kein Schutz erkannt", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("Keine Schutzmaßnahme erkannt", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("Nicht erkannt", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("Nicht vorhanden", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyDiscProtectionReaction(Models.OpticalDriveInfo drive)
    {
        _discImageCts?.Cancel();
        _discImagePreflightPassed = false;
        _lastDiscImageResult = null;
        DiscImageStartButton.IsEnabled = false;
        DiscImageVerifyButton.IsEnabled = false;
        DiscImageProgressBar.Value = 0;
        DiscImageProgressBar.Visibility = Visibility.Collapsed;
        DiscImageStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
        DiscImageStatusText.Text = "Technische Schutzmaßnahme erkannt oder vermutet.";
        var displayedProtectionStatus = drive.ProtectionStatus.Equals("Kein eindeutiger Schutz feststellbar", StringComparison.OrdinalIgnoreCase)
            ? "Schutzmaßnahme vermutet, aber nicht eindeutig bestimmbar"
            : drive.ProtectionStatus;
        DiscImageDetailsTextBox.Text = DiscProtectionMessage + $"\nSchutzstatus: {displayedProtectionStatus}";

        if (!string.IsNullOrWhiteSpace(_discImageOutputPath))
        {
            var partialPath = _discImageOutputPath + ".partial";
            try { if (File.Exists(partialPath)) File.Delete(partialPath); }
            catch (Exception ex) { DiscImageDetailsTextBox.Text += "\nHinweis: Die unvollständige Datei konnte nicht entfernt werden: " + ex.Message; }
        }

        _discImageOperationStarted = DateTime.Now;
        _lastDiscImageReportPath = WriteDiscImageReport(
            "ABGEBROCHEN", drive, _discImageOutputPath, 0, null, "Nicht ausgeführt", DiscProtectionMessage.Replace("\n", " "));
        DiscImageDetailsTextBox.Text += $"\nBericht: {_lastDiscImageReportPath}";
        UpdateDiscImageActionButtons();
        MessageBox.Show(DiscProtectionMessage, "DiscForge Disc zu Image", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void DiscImageCancel_Click(object sender,RoutedEventArgs e)=>_discImageCts?.Cancel();

    private async void DiscImageVerify_Click(object sender,RoutedEventArgs e)
    {
        if(_lastDiscImageResult is null||!File.Exists(_lastDiscImageResult.ImagePath))return;var cts=new CancellationTokenSource();DiscImageVerifyButton.IsEnabled=false;var progress=new Progress<(int Percent,string Message)>(u=>{DiscImageProgressBar.Value=u.Percent;DiscImageStatusText.Text=u.Message;});
        try{var hash=await DiscImageReadService.ComputeSha256Async(_lastDiscImageResult.ImagePath,progress,cts.Token);var ok=hash.Equals(_lastDiscImageResult.Sha256,StringComparison.OrdinalIgnoreCase);DiscImageStatusText.Text=ok?"Image-Pruefung bestanden.":"Image-Pruefung nicht bestanden.";DiscImageStatusText.Foreground=ok?System.Windows.Media.Brushes.LightGreen:System.Windows.Media.Brushes.Salmon;DiscImageDetailsTextBox.Text+=$"\nPruef-SHA-256: {hash}\nErgebnis: {(ok?"BESTANDEN":"NICHT BESTANDEN")}";
            if(Selected is { } drive)
            {
                _lastDiscImageReportPath=WriteDiscImageReport(ok?"ERFOLGREICH":"FEHLGESCHLAGEN",drive,_lastDiscImageResult.ImagePath,_lastDiscImageResult.BytesRead,_lastDiscImageResult.Sha256,ok?"BESTANDEN":"NICHT BESTANDEN",ok?"Image-Pruefung bestanden.":"Image-Pruefung nicht bestanden."); SavePersistentDiscImageState();
                DiscImageDetailsTextBox.Text+=$"\nPruefbericht erstellt: {_lastDiscImageReportPath}";
            }}
        catch(Exception ex){DiscImageStatusText.Text="Image-Pruefung fehlgeschlagen.";DiscImageDetailsTextBox.Text+="\nFEHLER: "+ex.Message;}finally{cts.Dispose();DiscImageVerifyButton.IsEnabled=true;UpdateDiscImageActionButtons();}
    }

    private Button? _discImageOpenFolderButton;
    private Button? _discImageOpenReportButton;
    private Button? _discImageUseInWorkshopButton;
    private bool _discImageDev2ActionsInitialized;

    private void InitializeDiscImageDev2Actions()
    {
        if (_discImageDev2ActionsInitialized) return;
        _discImageDev2ActionsInitialized=true;
        if (DiscImageVerifyButton.Parent is not Panel actionPanel || actionPanel.Parent is not Panel host) return;
        var panel=new UniformGrid { Columns=3, Margin=new Thickness(0,12,0,0) };
        _discImageOpenFolderButton=CreateDiscImageActionButton("ZIELORDNER OEFFNEN",DiscImageOpenFolder_Click);
        _discImageOpenReportButton=CreateDiscImageActionButton("LETZTEN BERICHT OEFFNEN",DiscImageOpenReport_Click);
        _discImageUseInWorkshopButton=CreateDiscImageActionButton("IN ISO-WERKSTATT OEFFNEN",DiscImageUseInWorkshop_Click);
        panel.Children.Add(_discImageOpenFolderButton);
        panel.Children.Add(_discImageOpenReportButton);
        panel.Children.Add(_discImageUseInWorkshopButton);
        host.Children.Add(panel);
    }

    private Button CreateDiscImageActionButton(string text, RoutedEventHandler handler)
    {
        var button=new Button { Content=text, Height=38, Margin=new Thickness(4), IsEnabled=true };
        button.Click+=handler;
        return button;
    }

        // ALPHA2-DEV3-PERSISTENZ
    private bool _discImageStateLoaded;
    private string? _persistedDiscImageSha256;
    private long _persistedDiscImageSize;
    private static string DiscImageStatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DiscForgeStudio","disc-image-state.txt");

    private void SavePersistentDiscImageState()
    {
        try
        {
            var image=_lastDiscImageResult?.ImagePath??_discImageOutputPath;
            if(string.IsNullOrWhiteSpace(image)||!File.Exists(image))return;
            var hash=_lastDiscImageResult?.Sha256??_persistedDiscImageSha256??string.Empty;
            var size=new FileInfo(image).Length;
            var dir=Path.GetDirectoryName(DiscImageStatePath)!;Directory.CreateDirectory(dir);
            File.WriteAllLines(DiscImageStatePath,new[]{image,_lastDiscImageReportPath??string.Empty,hash,size.ToString(System.Globalization.CultureInfo.InvariantCulture),DateTimeOffset.Now.ToString("O")});
            _persistedDiscImageSha256=hash;_persistedDiscImageSize=size;
        }
        catch(Exception ex){System.Diagnostics.Debug.WriteLine("Disc-Image-Status konnte nicht gespeichert werden: "+ex.Message);}
    }

    private void LoadPersistentDiscImageState()
    {
        if(_discImageStateLoaded)return;_discImageStateLoaded=true;
        try
        {
            if(!File.Exists(DiscImageStatePath))return;
            var lines=File.ReadAllLines(DiscImageStatePath);if(lines.Length<4)return;
            var image=lines[0];var report=lines[1];
            if(!File.Exists(image)){File.Delete(DiscImageStatePath);return;}
            _discImageOutputPath=image;DiscImagePathTextBox.Text=image;
            _lastDiscImageReportPath=File.Exists(report)?report:null;
            _persistedDiscImageSha256=lines[2];
            _persistedDiscImageSize=long.TryParse(lines[3],out var size)?size:new FileInfo(image).Length;
            // Status wird ueber das vorhandene Pfadfeld und die Aktionsbuttons sichtbar; kein separates TechnicalText-Steuerelement erforderlich.
        }
        catch(Exception ex){System.Diagnostics.Debug.WriteLine("Disc-Image-Status konnte nicht geladen werden: "+ex.Message);}
    }

    private void UpdateDiscImageActionButtons()
    {
        LoadPersistentDiscImageState();
        // ALPHA2-DEV2-BUTTONS-SICHER-V2
        if (_discImageOpenFolderButton is not null) _discImageOpenFolderButton.IsEnabled=true;
        if (_discImageOpenReportButton is not null) _discImageOpenReportButton.IsEnabled=true;
        if (_discImageUseInWorkshopButton is not null) _discImageUseInWorkshopButton.IsEnabled=true;
    }

    private string? ResolveDiscImageFolder()
    {
        var path=_lastDiscImageResult?.ImagePath;
        if(string.IsNullOrWhiteSpace(path))path=_discImageOutputPath;
        if(string.IsNullOrWhiteSpace(path))path=DiscImagePathTextBox.Text?.Trim();
        if(string.IsNullOrWhiteSpace(path)||path.Contains("Noch kein Speicherort",StringComparison.OrdinalIgnoreCase))return null;
        var folder=Directory.Exists(path)?path:Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(folder)&&Directory.Exists(folder)?folder:null;
    }

    private void DiscImageOpenFolder_Click(object sender,RoutedEventArgs e)
    {
        var folder=ResolveDiscImageFolder();
        if(folder is null){MessageBox.Show("Noch kein gueltiger ISO-Zielordner verfuegbar. Waehle zuerst einen ISO-Speicherort.","DiscForge",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        Process.Start(new ProcessStartInfo("explorer.exe",folder){UseShellExecute=true});
    }

    private void DiscImageOpenReport_Click(object sender,RoutedEventArgs e)
    {
        var report=_lastDiscImageReportPath;
        if(string.IsNullOrWhiteSpace(report)||!File.Exists(report))
        {
            var folder=ResolveDiscImageFolder();var reportFolder=folder is null?null:Path.Combine(folder,"DiscForge-Berichte");
            if(!string.IsNullOrWhiteSpace(reportFolder)&&Directory.Exists(reportFolder))report=Directory.GetFiles(reportFolder,"DiscForge-Disc-zu-Image-*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        if(string.IsNullOrWhiteSpace(report)||!File.Exists(report)){MessageBox.Show("Noch kein Disc-zu-Image-Bericht verfuegbar. Erstelle oder pruefe zuerst ein Image.","DiscForge",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        _lastDiscImageReportPath=report;Process.Start(new ProcessStartInfo(report){UseShellExecute=true});
    }

    private void DiscImageUseInWorkshop_Click(object sender,RoutedEventArgs e)
    {
        var path=_lastDiscImageResult?.ImagePath;
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))path=_discImageOutputPath;
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path)){MessageBox.Show("Noch keine fertige ISO verfuegbar. Erstelle zuerst ein Image.","DiscForge",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        _selectedIsoPath=path;SelectedIsoPathText.Text=path;SelectedIsoSizeText.Text=$"Groesse: {FormatIsoBytes(new FileInfo(path).Length)}";
        SelectedIsoHashText.Text=$"SHA-256: {_lastDiscImageResult?.Sha256 ?? _persistedDiscImageSha256 ?? "Wird bei der Vorpruefung berechnet"}";SelectedIsoStatusText.Text="Status: Aus Disc zu Image uebernommen";
        _isoBurnPreflightPassed=false;IsoBurnStartButton.IsEnabled=false;IsoWorkshopTab.IsSelected=true;SetIsoStatus("Disc-Image uebernommen","Bitte jetzt BRENNEN VORPRUEFEN ausfuehren.",0);
    }

    private string WriteDiscImageReport(string status, Models.OpticalDriveInfo drive, string? targetPath, long resultBytes, string? sha256, string verificationStatus, string message)
    {
        var started=_discImageOperationStarted==default?DateTime.Now:_discImageOperationStarted;
        var result=new Models.OperationResult(
            "Disc zu Image",
            status,
            started,
            DateTime.Now,
            drive.Name,
            drive.MediaType,
            drive.VolumeLabel,
            drive.FileSystem,
            drive.TotalBytes,
            targetPath,
            resultBytes,
            sha256,
            verificationStatus,
            message,
            OperationReportService.CurrentVersion());
        return _operationReportService.Write(result);
    }

    private void SetDiscImageBusy(bool busy){_mediaTimer.IsEnabled=!busy;DiscImagePreflightButton.IsEnabled=!busy;DiscImageStartButton.IsEnabled=!busy&&_discImagePreflightPassed;DiscImageCancelButton.IsEnabled=busy;DiscImageVerifyButton.IsEnabled=!busy&&_lastDiscImageResult is not null;UpdateDiscImageActionButtons();} // ALPHA2-DEV2-ACTIONFIX
}
