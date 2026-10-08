using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.8 Compact metric track (Figma 112:9450 / 112:9642): a 3 px track in the empty-bar colour with a fill of
/// <c>value% × track width</c>. Neutral fill unless <see cref="Tone"/> says attention / critical (Prism R-5: the colour is
/// the text colour of that severity, never a brand or metric hue).
/// <para>
/// UNKNOWN IS NOT ZERO: <see cref="Value"/> defaults to <see cref="double.NaN"/>, which draws NO fill. A real 0 draws no
/// fill either, and any value above 0 draws at least <see cref="MinimumFill"/> px (UI.5 rule) so a small reading stays
/// visible. Decorative for UI Automation: the row's accessible name carries the value and its cue.
/// </para>
/// </summary>
[TemplatePart(Name = FillPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = TrackPartName, Type = typeof(FrameworkElement))]
[TemplateVisualState(Name = "Neutral", GroupName = "ToneStates")]
[TemplateVisualState(Name = "Attention", GroupName = "ToneStates")]
[TemplateVisualState(Name = "Critical", GroupName = "ToneStates")]
public sealed class SaCompactMetricBar : Control
{
    /// <summary>The smallest fill a reading above 0 draws, in DIP.</summary>
    public const double MinimumFill = 3;

    private const string FillPartName = "PART_Fill";
    private const string TrackPartName = "PART_Track";

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SaCompactMetricBar), new PropertyMetadata(double.NaN, (d, _) => ((SaCompactMetricBar)d).Update()));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(SaMetricTone), typeof(SaCompactMetricBar), new PropertyMetadata(SaMetricTone.Neutral, (d, _) => ((SaCompactMetricBar)d).Update()));

    private FrameworkElement? _fill;
    private FrameworkElement? _track;

    public SaCompactMetricBar()
    {
        DefaultStyleKey = typeof(SaCompactMetricBar);
        IsTabStop = false;
    }

    /// <summary>0–100; <see cref="double.NaN"/> (the default) = unknown.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public SaMetricTone Tone
    {
        get => (SaMetricTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>The fill width for a reading on a track: 0 when unknown or 0, else at least <see cref="MinimumFill"/>. Pure.</summary>
    public static double FillWidth(double value, double trackWidth)
    {
        if (double.IsNaN(value) || !double.IsFinite(trackWidth) || trackWidth <= 0 || value <= 0)
        {
            return 0;
        }

        return Math.Min(trackWidth, Math.Max(MinimumFill, Math.Clamp(value, 0, 100) / 100 * trackWidth));
    }

    protected override void OnApplyTemplate()
    {
        if (_track is not null)
        {
            _track.SizeChanged -= OnTrackSizeChanged;
        }

        base.OnApplyTemplate();
        _fill = GetTemplateChild(FillPartName) as FrameworkElement;
        _track = GetTemplateChild(TrackPartName) as FrameworkElement;
        if (_track is not null)
        {
            _track.SizeChanged += OnTrackSizeChanged;
        }

        Update();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => Update();

    private void Update()
    {
        if (_fill is not null && _track is not null)
        {
            var width = FillWidth(Value, _track.ActualWidth);
            _fill.Width = width;
            _fill.Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        VisualStateManager.GoToState(this, Enum.IsDefined(Tone) ? Tone.ToString() : nameof(SaMetricTone.Neutral), useTransitions: false);
    }
}
