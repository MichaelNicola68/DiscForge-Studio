using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DiscForgeStudio.Controls;

public partial class LiveBurnStatusControl : UserControl
{
    private readonly DispatcherTimer _clock;
    private DateTimeOffset? _startedAt;

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(string), typeof(LiveBurnStatusControl),
        new PropertyMetadata("Bereit", OnVisualPropertyChanged));

    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(LiveBurnStatusControl),
        new PropertyMetadata(0d, OnVisualPropertyChanged));

    public static readonly DependencyProperty DetailTextProperty = DependencyProperty.Register(
        nameof(DetailText), typeof(string), typeof(LiveBurnStatusControl),
        new PropertyMetadata("Noch kein Schreibvorgang gestartet.", OnVisualPropertyChanged));

    public static readonly DependencyProperty CurrentItemProperty = DependencyProperty.Register(
        nameof(CurrentItem), typeof(string), typeof(LiveBurnStatusControl),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public string Phase
    {
        get => (string)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public string DetailText
    {
        get => (string)GetValue(DetailTextProperty);
        set => SetValue(DetailTextProperty, value);
    }

    public string CurrentItem
    {
        get => (string)GetValue(CurrentItemProperty);
        set => SetValue(CurrentItemProperty, value);
    }

    public LiveBurnStatusControl()
    {
        InitializeComponent();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateElapsed();
        Loaded += (_, _) => RefreshVisuals();
        Unloaded += (_, _) => _clock.Stop();
    }

    public void Update(string phase, double percent, string? detail = null, string? currentItem = null)
    {
        Phase = phase;
        Percent = Math.Clamp(percent, 0d, 100d);
        if (detail is not null) DetailText = detail;
        if (currentItem is not null) CurrentItem = currentItem;
    }

    public void Reset(string phase = "Bereit", string detail = "Noch kein Schreibvorgang gestartet.")
    {
        _startedAt = null;
        _clock.Stop();
        ElapsedTextBlock.Text = "00:00";
        Phase = phase;
        Percent = 0;
        DetailText = detail;
        CurrentItem = string.Empty;
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiveBurnStatusControl control && control.IsLoaded)
            control.RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        var percent = Math.Clamp(Percent, 0d, 100d);
        LiveProgressBar.Value = percent;
        PercentTextBlock.Text = $"{percent:0} %";
        PhaseTextBlock.Text = string.IsNullOrWhiteSpace(Phase) ? "Bereit" : Phase;
        DetailTextBlock.Text = string.IsNullOrWhiteSpace(DetailText) ? "Vorgang wird vorbereitet." : DetailText;
        ItemTextBlock.Text = CurrentItem ?? string.Empty;
        ItemTextBlock.Visibility = string.IsNullOrWhiteSpace(CurrentItem) ? Visibility.Collapsed : Visibility.Visible;

        if (percent > 0 && percent < 100 && _startedAt is null)
        {
            _startedAt = DateTimeOffset.Now;
            _clock.Start();
        }
        else if (percent >= 100)
        {
            _clock.Stop();
        }

        var (color, glow) = SelectColor(Phase, percent);
        StatusDot.Fill = new SolidColorBrush(color);
        StatusGlow.Color = glow;
        PercentTextBlock.Foreground = new SolidColorBrush(color);
        PulseDot();
        UpdateElapsed();
    }

    private static (Color Color, Color Glow) SelectColor(string? phase, double percent)
    {
        var text = phase ?? string.Empty;
        if (text.Contains("Fehler", StringComparison.OrdinalIgnoreCase) || text.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase))
            return (Color.FromRgb(255, 91, 113), Color.FromRgb(255, 49, 87));
        if (text.Contains("abschlie", StringComparison.OrdinalIgnoreCase) || text.Contains("final", StringComparison.OrdinalIgnoreCase))
            return (Color.FromRgb(162, 116, 255), Color.FromRgb(123, 91, 255));
        if (text.Contains("schreib", StringComparison.OrdinalIgnoreCase) || text.Contains("brenn", StringComparison.OrdinalIgnoreCase))
            return (Color.FromRgb(255, 87, 116), Color.FromRgb(255, 49, 87));
        if (percent >= 100 || text.Contains("bestanden", StringComparison.OrdinalIgnoreCase) || text.Contains("fertig", StringComparison.OrdinalIgnoreCase))
            return (Color.FromRgb(88, 230, 166), Color.FromRgb(54, 211, 153));
        return (Color.FromRgb(77, 231, 243), Color.FromRgb(57, 214, 232));
    }

    private void PulseDot()
    {
        var animation = new DoubleAnimation
        {
            From = 0.45,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(650),
            AutoReverse = true,
            RepeatBehavior = Percent > 0 && Percent < 100 ? RepeatBehavior.Forever : new RepeatBehavior(1)
        };
        StatusDot.BeginAnimation(OpacityProperty, animation);
    }

    private void UpdateElapsed()
    {
        if (_startedAt is null) return;
        var elapsed = DateTimeOffset.Now - _startedAt.Value;
        ElapsedTextBlock.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
