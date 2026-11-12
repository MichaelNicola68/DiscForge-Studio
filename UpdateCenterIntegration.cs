using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DiscForgeStudio.Models;
using DiscForgeStudio.Services;
using Microsoft.Win32;

namespace DiscForgeStudio;

internal static class UpdateCenterIntegration
{
    private static readonly Brush WindowBackground = new SolidColorBrush(Color.FromRgb(7, 21, 36));
    private static readonly Brush PanelBackground = new SolidColorBrush(Color.FromRgb(11, 30, 48));
    private static readonly Brush CardBackground = new SolidColorBrush(Color.FromRgb(17, 43, 65));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(53, 209, 225));
    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(100, 220, 150));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(255, 202, 92));
    private static readonly Brush Failure = new SolidColorBrush(Color.FromRgb(255, 125, 125));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(180, 200, 216));

    public static void Attach(MainWindow owner)
    {
        owner.Loaded += (_, _) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (owner.Content is not Grid root || Find(root, "UpdateCenterPrototypeButton") is not null) return;
            var openSource = FindButton(root, "Open Source Software");
            if (openSource is null || VisualTreeHelper.GetParent(openSource) is not Panel buttonPanel) return;
            var button = new Button
            {
                Name = "UpdateCenterPrototypeButton", Content = "UPDATE-CENTER",
                Padding = openSource.Padding, Margin = openSource.Margin,
                MinWidth = openSource.MinWidth, MinHeight = openSource.MinHeight,
                HorizontalAlignment = openSource.HorizontalAlignment,
                VerticalAlignment = openSource.VerticalAlignment,
                Background = new SolidColorBrush(Color.FromRgb(19, 66, 84)),
                Foreground = Brushes.White, BorderBrush = Accent
            };
            if (openSource.Style is not null) button.Style = openSource.Style;
            if (!double.IsNaN(openSource.Width)) button.Width = openSource.Width;
            if (!double.IsNaN(openSource.Height)) button.Height = openSource.Height;
            button.Click += (_, _) => Show(owner);
            buttonPanel.Children.Insert(buttonPanel.Children.IndexOf(openSource), button);
        }));
    }

    private static FrameworkElement? Find(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            var match = Find(child, name); if (match is not null) return match;
        }
        return null;
    }

    private static Button? FindButton(DependencyObject parent, string text)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && string.Equals(button.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) return button;
            var match = FindButton(child, text); if (match is not null) return match;
        }
        return null;
    }

    private static void Show(MainWindow owner)
    {
        var window = new Window
        {
            Title = "DiscForge Update-Center · Sicherer Prüfprototyp",
            Owner = owner, Width = 980, Height = 760, MinWidth = 760, MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBackground, Foreground = Brushes.White
        };
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock { Text = "UPDATE-CENTER", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Accent });
        var subtitle = new TextBlock { Text = "Sicherer Prüfprototyp · erklärt Updates verständlich · installiert keine Dateien", Margin = new Thickness(0, 4, 0, 16), Foreground = Muted };
        Grid.SetRow(subtitle, 1); root.Children.Add(subtitle);
        var pathBox = new TextBox { IsReadOnly = true, Text = "Noch kein Patch-Paket gewählt", Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 7, 10, 7) };
        Grid.SetRow(pathBox, 2); root.Children.Add(pathBox);
        var choose = new Button { Content = "PATCH-ZIP AUSWÄHLEN UND SICHER PRÜFEN", Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12), Background = Accent, Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold };
        Grid.SetRow(choose, 3); root.Children.Add(choose);
        var resultHost = new StackPanel(); ResetResult(resultHost);
        var scroll = new ScrollViewer { Content = resultHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4); root.Children.Add(scroll);
        var locked = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(52, 42, 22)), BorderBrush = Warning,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12),
            Child = new TextBlock { Text = "INSTALLATION GESPERRT · Dieses Fenster prüft ausschließlich. Es verändert keine Programmdateien.", Foreground = Warning, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap }
        };
        Grid.SetRow(locked, 5); root.Children.Add(locked);

        choose.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "DiscForge-Patch (*.zip)|*.zip", Title = "DiscForge-Patch auswählen" };
            if (dialog.ShowDialog(window) != true) return;
            pathBox.Text = dialog.FileName; choose.IsEnabled = false; resultHost.Children.Clear();
            AddStatus(resultHost, "PRÜFUNG LÄUFT", "Das Patch-Paket wird ausschließlich gelesen und kontrolliert.", Warning);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            try { RenderResult(resultHost, new UpdatePackageService().Validate(dialog.FileName, ApplicationVersionService.Current), dialog.FileName); }
            catch (Exception ex) { resultHost.Children.Clear(); AddStatus(resultHost, "PRÜFUNG FEHLGESCHLAGEN", ex.Message, Failure); }
            finally { choose.IsEnabled = true; }
        };
        window.Content = root; window.ShowDialog();
    }

    private static void ResetResult(Panel host)
    {
        host.Children.Clear();
        AddStatus(host, "BEREIT", "Wähle ein DiscForge-Patch-ZIP aus. Vor der Prüfung werden keine Dateien entpackt oder installiert.", Accent);
    }

    private static void RenderResult(Panel host, UpdateValidationResult result, string packagePath)
    {
        host.Children.Clear();
        AddStatus(host, result.Success ? "PAKET TECHNISCH GÜLTIG" : "PAKET NICHT FREIGEGEBEN",
            result.Success ? "Alle technischen Paketprüfungen wurden bestanden. Die Installation bleibt trotzdem gesperrt."
                           : "Mindestens eine Sicherheits- oder Paketprüfung ist fehlgeschlagen. Es wurden keine Änderungen vorgenommen.",
            result.Success ? Success : Failure);

        if (result.Manifest is not { } manifest)
        {
            var errorsOnly = AddSection(host, $"FEHLER UND SPERRGRÜNDE ({result.Errors.Count})");
            foreach (var error in result.Errors) AddBullet(errorsOnly, error, Failure);
            return;
        }

        var purpose = AddSection(host, "WAS MACHT DIESES UPDATE?");
        purpose.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(manifest.Description)
                ? "Für dieses Update wurde noch keine verständliche Beschreibung hinterlegt."
                : manifest.Description,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 8)
        });
        AddListGroup(purpose, "NEUE FUNKTIONEN", manifest.NewFeatures, Success);
        AddListGroup(purpose, "VERBESSERUNGEN", manifest.Improvements, Accent);

        var summary = AddSection(host, "KURZÜBERSICHT");
        AddValue(summary, "Titel", EmptyFallback(manifest.Title));
        AddValue(summary, "Version", $"{manifest.SourceVersion}  →  {manifest.TargetVersion}");
        AddValue(summary, "Risiko", EmptyFallback(manifest.RiskLevel));
        AddValue(summary, "Betroffene Bereiche", manifest.AffectedModules is { Count: > 0 } ? string.Join(", ", manifest.AffectedModules) : "Keine angegeben");
        AddValue(summary, "Änderungen", $"{manifest.Files?.Count ?? 0} Datei(en)");
        AddValue(summary, "Prüfstatus", result.Success ? $"{result.Checks.Count} bestanden · 0 Fehler" : $"{result.Checks.Count} bestanden · {result.Errors.Count} Fehler");

        var simulationHost = new StackPanel();
        if (result.Success)
        {
            var simulateButton = new Button
            {
                Content = "INSTALLATION SIMULIEREN", Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(35, 120, 92)), Foreground = Brushes.White,
                BorderBrush = Success, FontWeight = FontWeights.Bold
            };
            host.Children.Add(simulateButton);
            host.Children.Add(simulationHost);
            simulateButton.Click += (_, _) =>
            {
                simulateButton.IsEnabled = false;
                simulationHost.Children.Clear();
                AddStatus(simulationHost, "SIMULATION LÄUFT", "Vorhandene Dateien, SHA-256-Werte, Sicherungsplan und Rücksetzplan werden nur lesend geprüft.", Warning);
                try
                {
                    var simulation = new UpdateInstallationSimulationService().Simulate(packagePath, manifest);
                    RenderSimulation(simulationHost, simulation);
                    simulateButton.Content = simulation.Success ? "SIMULATION ERNEUT AUSFÜHREN" : "SIMULATION WIEDERHOLEN";
                }
                catch (Exception ex)
                {
                    simulationHost.Children.Clear();
                    AddStatus(simulationHost, "SIMULATION FEHLGESCHLAGEN", ex.Message, Failure);
                }
                finally { simulateButton.IsEnabled = true; }
            };
        }

        if (result.Success)
        {
            var installButton = new Button
            {
                Content = "KONTROLLIERTE TESTINSTALLATION AUSFÜHREN", Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(150, 78, 35)), Foreground = Brushes.White,
                BorderBrush = Warning, FontWeight = FontWeights.Bold
            };
            host.Children.Add(installButton);
            installButton.Click += (_, _) =>
            {
                var answer = MessageBox.Show("Das freigegebene DEV16-Testpaket wird jetzt wirklich installiert. Vorhandene Testdateien werden gesichert. Bei einem Fehler erfolgt automatisch ein Rollback. Fortfahren?", "Kontrollierte Testinstallation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                installButton.IsEnabled = false;
                try
                {
                    var installed = new ControlledUpdateInstallationService().Install(packagePath, manifest);
                    RenderInstallation(simulationHost, installed);
                    installButton.Content = installed.Success ? "TESTINSTALLATION ERFOLGREICH" : "TESTINSTALLATION FEHLGESCHLAGEN";
                }
                finally { installButton.IsEnabled = true; }
            };
        }

        if (result.Success && string.Equals(manifest.TestProfile, "ExternalExecutableRollbackTest", StringComparison.Ordinal))
        {
            var externalButton = new Button { Content = "EXTERNEN UPDATER STARTEN UND DISCFORGE NEU STARTEN", Padding = new Thickness(11), Margin = new Thickness(0,0,0,10), Background = new SolidColorBrush(Color.FromRgb(120,68,170)), Foreground=Brushes.White, FontWeight=FontWeights.Bold };
            host.Children.Add(externalButton);
            externalButton.Click += (_,_) => { if(MessageBox.Show("DiscForge wird beendet. Der externe Updater installiert das freigegebene Testupdate und startet DiscForge danach neu.","Externer Updater",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes) ExternalUpdaterLauncher.Launch(packagePath,manifest); };
        }

        var detailsButton = new Button
        {
            Content = "TECHNISCHE DETAILS ANZEIGEN", Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromRgb(19, 66, 84)), Foreground = Brushes.White, BorderBrush = Accent,
            FontWeight = FontWeights.SemiBold
        };
        host.Children.Add(detailsButton);
        var details = new StackPanel { Visibility = Visibility.Collapsed };
        host.Children.Add(details);
        detailsButton.Click += (_, _) =>
        {
            var show = details.Visibility != Visibility.Visible;
            details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            detailsButton.Content = show ? "TECHNISCHE DETAILS AUSBLENDEN" : "TECHNISCHE DETAILS ANZEIGEN";
        };

        var overview = AddSection(details, "PATCH-DETAILS");
        AddValue(overview, "Patch-ID", EmptyFallback(manifest.PatchId));
        AddValue(overview, "Vollständiger Test", manifest.RequiresFullTest ? "Ja" : "Nein");
        AddValue(overview, "Testprofil", EmptyFallback(manifest.TestProfile));

        var files = AddSection(details, "DATEIÄNDERUNGEN");
        if (manifest.Files is { Count: > 0 }) foreach (var file in manifest.Files)
        {
            var color = file.Operation.Equals("Delete", StringComparison.OrdinalIgnoreCase) ? Failure
                : file.Operation.Equals("Add", StringComparison.OrdinalIgnoreCase) ? Success : Warning;
            AddBullet(files, $"{file.Operation.ToUpperInvariant(),-7}  {file.Path}", color);
        }
        else AddBullet(files, "Keine Dateien angegeben", Muted);

        var tests = AddSection(details, "ERFORDERLICHE TESTS");
        foreach (var test in new UpdateTestProfileService().Resolve(manifest)) AddBullet(tests, test, Warning);
        var checks = AddSection(details, $"BESTANDENE PRÜFUNGEN ({result.Checks.Count})");
        if (result.Checks.Count == 0) AddBullet(checks, "Keine bestandene Prüfung gemeldet", Muted);
        else foreach (var check in result.Checks) AddBullet(checks, check, Success);
        var errors = AddSection(details, $"FEHLER UND SPERRGRÜNDE ({result.Errors.Count})");
        if (result.Errors.Count == 0) AddBullet(errors, "Keine Fehler gefunden", Success);
        else foreach (var error in result.Errors) AddBullet(errors, error, Failure);
    }

    private static void RenderInstallation(Panel host, ControlledInstallationResult result)
    {
        host.Children.Clear();
        AddStatus(host, result.Success ? "KONTROLLIERTE TESTINSTALLATION ERFOLGREICH" : "TESTINSTALLATION FEHLGESCHLAGEN",
            result.Success ? "Alle freigegebenen Testdateien wurden installiert und per SHA-256 bestätigt." :
            (result.RolledBack ? "Die Installation ist fehlgeschlagen. Alle begonnenen Änderungen wurden zurückgesetzt." : "Installation und Rollback prüfen."),
            result.Success ? Success : Failure);
        var info=AddSection(host,"INSTALLATIONSERGEBNIS");
        AddValue(info,"Sicherungsordner",result.BackupPath);
        AddValue(info,"Rollback",result.RolledBack ? "Ausgeführt" : "Nicht erforderlich");
        var checks=AddSection(host,$"BESTANDENE INSTALLATIONSPRÜFUNGEN ({result.Checks.Count})");
        foreach(var check in result.Checks) AddBullet(checks,check,Success);
        var errors=AddSection(host,$"INSTALLATIONSFEHLER ({result.Errors.Count})");
        if(result.Errors.Count==0) AddBullet(errors,"Keine Fehler",Success); else foreach(var error in result.Errors) AddBullet(errors,error,Failure);
    }

    private static void RenderSimulation(Panel host, UpdateSimulationResult simulation)
    {
        host.Children.Clear();
        AddStatus(host,
            simulation.Success ? "INSTALLATIONSSIMULATION BESTANDEN" : "INSTALLATIONSSIMULATION GESPERRT",
            simulation.Success
                ? "Alle Voraussetzungen wurden simuliert. Es wurden keine Programmdateien verändert."
                : "Mindestens eine Voraussetzung ist fehlgeschlagen. Es wurden keine Programmdateien verändert.",
            simulation.Success ? Success : Failure);

        var plan = AddSection(host, "SIMULATIONSPLAN");
        AddValue(plan, "Dateioperationen", $"{simulation.AddCount} ADD · {simulation.ReplaceCount} REPLACE · {simulation.DeleteCount} DELETE");
        AddValue(plan, "Zu sichernde Dateien", simulation.BackupFileCount.ToString());
        AddValue(plan, "Sicherungsumfang", FormatSimulationBytes(simulation.BackupBytes));
        AddValue(plan, "Geplanter Sicherungsordner", simulation.PlannedBackupPath);
        AddValue(plan, "Ausführung", "Nur Prüfung · keine Dateiänderung");

        var checks = AddSection(host, $"BESTANDENE SIMULATIONSPRÜFUNGEN ({simulation.Checks.Count})");
        foreach (var check in simulation.Checks) AddBullet(checks, check, Success);
        var errors = AddSection(host, $"SIMULATIONSSPERRGRÜNDE ({simulation.Errors.Count})");
        if (simulation.Errors.Count == 0) AddBullet(errors, "Keine Sperrgründe gefunden", Success);
        else foreach (var error in simulation.Errors) AddBullet(errors, error, Failure);
    }

    private static string FormatSimulationBytes(long value) => value < 1024 ? $"{value} B" : value < 1024 * 1024 ? $"{value / 1024d:0.0} KiB" : $"{value / 1024d / 1024d:0.0} MiB";

    private static void AddListGroup(Panel host, string title, IReadOnlyList<string>? items, Brush color)
    {
        if (items is not { Count: > 0 }) return;
        host.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = color, Margin = new Thickness(0, 7, 0, 3) });
        foreach (var item in items) AddBullet(host, item, color);
    }

    private static void AddStatus(Panel host, string title, string message, Brush color)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = color });
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White });
        host.Children.Add(new Border { Background = CardBackground, BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), Child = panel });
    }

    private static StackPanel AddSection(Panel host, string title)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 8) });
        host.Children.Add(new Border { Background = PanelBackground, BorderBrush = new SolidColorBrush(Color.FromRgb(42, 76, 102)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), Child = content });
        return content;
    }

    private static void AddValue(Panel host, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new TextBlock { Text = label, Foreground = Muted, FontWeight = FontWeights.SemiBold };
        var right = new TextBlock { Text = value, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(right, 1); row.Children.Add(left); row.Children.Add(right); host.Children.Add(row);
    }

    private static void AddBullet(Panel host, string text, Brush color) => host.Children.Add(new TextBlock { Text = "●  " + text, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
    private static string EmptyFallback(string? value) => string.IsNullOrWhiteSpace(value) ? "Nicht angegeben" : value;
}
