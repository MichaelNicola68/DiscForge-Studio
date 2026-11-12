using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiscForgeStudio;
internal static class OpenSourceSoftwareIntegration
{
    public static void Attach(Window window)
    {
        window.Loaded += (_, _) =>
        {
            if (window.Content is not Grid root || FindNamed(root, "OpenSourceSoftwareButton") != null) return;
            var button = new Button
            {
                Name = "OpenSourceSoftwareButton",
                Content = "Open Source Software",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 26, 18),
                Padding = new Thickness(16, 8, 16, 8),
                MinWidth = 176,
                Background = new SolidColorBrush(Color.FromRgb(19, 66, 84)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(53, 209, 225)),
                BorderThickness = new Thickness(1),
                ToolTip = "Projektinformation, Bedienungsanleitung und GPL-Lizenz"
            };
            Panel.SetZIndex(button, 10000);
            button.Click += (_, _) => ShowDialog(window);
            // Fix 1.2.4: Button in die rechte Kopfspalte einordnen statt als Overlay auf das Root-Grid zu legen.
            if (root.Children.Count > 0 && root.Children[0] is Grid header && header.ColumnDefinitions.Count >= 3)
            {
                var rightPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };

                UIElement? existingRightElement = null;
                for (var index = 0; index < header.Children.Count; index++)
                {
                    var child = header.Children[index];
                    if (Grid.GetColumn(child) == 2)
                    {
                        existingRightElement = child;
                        break;
                    }
                }

                if (existingRightElement != null)
                {
                    header.Children.Remove(existingRightElement);
                    rightPanel.Children.Add(existingRightElement);
                }

                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.VerticalAlignment = VerticalAlignment.Center;
                button.Margin = new Thickness(0, 8, 0, 0);
                button.MinWidth = 176;
                rightPanel.Children.Add(button);
                Grid.SetColumn(rightPanel, 2);
                header.Children.Add(rightPanel);
            }
            else
            {
                button.VerticalAlignment = VerticalAlignment.Top;
                button.Margin = new Thickness(0, 94, 26, 0);
                root.Children.Add(button);
            }
        };
    }

    private static FrameworkElement? FindNamed(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement e && e.Name == name) return e;
            var nested = FindNamed(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void ShowDialog(Window owner)
    {
        var window = new Window
        {
            Title = "Open Source Software",
            Owner = owner,
            Width = 680,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(7, 21, 36)),
            Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = DiscForgeStudio.Services.ApplicationVersionService.ProductLabel, FontSize = 25, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(53, 209, 225)), Margin = new Thickness(0,0,0,14) });
        panel.Children.Add(Text("Projektidee, Konzeption und Projektleitung: Michael Nicola"));
        panel.Children.Add(Text("Kontakt: NicolaMichael@gmx.de"));
        panel.Children.Add(Text("Copyright (C) 2026 Michael Nicola"));
        panel.Children.Add(Text("SPDX-License-Identifier: GPL-3.0-or-later"));
        panel.Children.Add(Text("Technische Umsetzung: KI-gestuetzt mit Microsoft 365 Copilot, unter menschlicher Vorgabe, Pruefung, Auswahl, Erprobung und Weiterentwicklung durch Michael Nicola."));
        panel.Children.Add(Text("Microsoft 365 Copilot wird nicht als Urheber oder Copyright-Inhaber genannt. Das Programm wird ohne Gewaehrleistung bereitgestellt."));
        panel.Children.Add(MakeButton("Bedienungsanleitung oeffnen", () => OpenLocal("DiscForge-Studio-1.5-Bedienungsanleitung.pdf")));
        panel.Children.Add(MakeButton("GPL-3.0-Lizenz oeffnen", () => OpenLocal("LICENSE.txt")));
        panel.Children.Add(MakeButton("Open-Source-Information oeffnen", () => OpenLocal("OPEN-SOURCE-SOFTWARE.txt")));
        panel.Children.Add(MakeButton("E-Mail an Michael Nicola", () => OpenExternal("mailto:NicolaMichael@gmx.de")));
        // DEV14-SAAROSS-HOMEPAGE-LINK-V1
        // DEV14-OPENSOURCE-VERSION-UTF8-FIX-V3
        panel.Children.Add(Text("Offizielle Projekt- und Downloadseite: www.saaross.de. Updates werden dort bei Bedarf manuell heruntergeladen und anschlie\u00DFend im Update-Center sicher gepr\u00FCft."));
        panel.Children.Add(MakeButton("SaarOSS-Homepage und Updates \u00F6ffnen", () => OpenExternal("https://www.saaross.de")));
        panel.Children.Add(MakeButton("Offizielle GPL-Webseite", () => OpenExternal("https://www.gnu.org/licenses/gpl-3.0.html")));
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.ShowDialog();
    }

    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0,0,0,12) };
    private static Button MakeButton(string label, Action action)
    {
        var b = new Button { Content = label, Margin = new Thickness(0,5,0,5), Padding = new Thickness(12,9,12,9), HorizontalContentAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => action(); return b;
    }
    private static string DocumentationPath(string file) => Path.Combine(AppContext.BaseDirectory, "Documentation", file);
    private static void OpenLocal(string file)
    {
        var path = DocumentationPath(file);
        if (!File.Exists(path)) { MessageBox.Show("Datei nicht gefunden:\n" + path, "DiscForge Studio", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private static void OpenExternal(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}


