using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 mini metric bar (Figma "Barra contínua" 112:1040 / 112:1252): track h5 radius 3 in the empty-bar colour, fill
/// in the Manual metric colour (CPU / memory / disk), and the percentage beside it.
/// <para>
/// UNKNOWN IS NOT ZERO: <see cref="Value"/> defaults to <see cref="double.NaN"/>, which renders an EMPTY track and the
/// text "—" - never a 0% bar. A real 0 renders "0%". The page maps "no reading" to NaN when it adopts the primitive.
/// </para>
/// </summary>
[TemplatePart(Name = FillPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = TrackPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = TextPartName, Type = typeof(TextBlock))]
[TemplateVisualState(Name = "Cpu", GroupName = "MetricStates")]
[TemplateVisualState(Name = "Memory", GroupName = "MetricStates")]
[TemplateVisualState(Name = "Disk", GroupName = "MetricStates")]
public sealed class SaMetricBar : Control
{
    /// <summary>What an unknown reading shows. Never "0".</summary>
    public const string UnknownText = "—";

    private const string FillPartName = "PART_Fill";
    private const string TrackPartName = "PART_Track";
    private const string TextPartName = "PART_Text";

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SaMetricBar), new PropertyMetadata(double.NaN, (d, _) => ((SaMetricBar)d).Update()));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SaMetricBar), new PropertyMetadata(100d, (d, _) => ((SaMetricBar)d).Update()));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(SaMetricKind), typeof(SaMetricBar), new PropertyMetadata(SaMetricKind.Cpu, (d, _) => ((SaMetricBar)d).Update()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SaMetricBar), new PropertyMetadata(string.Empty, (d, _) => ((SaMetricBar)d).Update()));

    private FrameworkElement? _fill;
    private FrameworkElement? _track;
    private TextBlock? _text;

    public SaMetricBar()
    {
        DefaultStyleKey = typeof(SaMetricBar);
        IsTabStop = false;
    }

    /// <summary>The reading; <see cref="double.NaN"/> (the default) = unknown.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public SaMetricKind Metric
    {
        get => (SaMetricKind)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    /// <summary>What is measured (e.g. "CPU"); with the value text it is the automation name.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Filled share of the track in [0, 1]; 0 for an unknown reading (the TEXT says unknown, not the bar).</summary>
    public static double FillFraction(double value, double maximum) =>
        double.IsNaN(value) || !double.IsFinite(maximum) || maximum <= 0 ? 0 : Math.Clamp(value / maximum, 0, 1);

    /// <summary>"42%" for a reading, "—" for an unknown one.</summary>
    public static string FormatValue(double value, double maximum) =>
        double.IsNaN(value) || !double.IsFinite(maximum) || maximum <= 0
            ? UnknownText
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(value / maximum, 0, 1) * 100):0}%");

    protected override void OnApplyTemplate()
    {
        if (_track is not null)
        {
            _track.SizeChanged -= OnTrackSizeChanged;
        }

        base.OnApplyTemplate();
        _fill = GetTemplateChild(FillPartName) as FrameworkElement;
        _track = GetTemplateChild(TrackPartName) as FrameworkElement;
        _text = GetTemplateChild(TextPartName) as TextBlock;
        if (_track is not null)
        {
            _track.SizeChanged += OnTrackSizeChanged;
        }

        Update();
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => Update();

    private void Update()
    {
        var text = FormatValue(Value, Maximum);
        if (_text is not null)
        {
            _text.Text = text;
        }

        if (_fill is not null && _track is not null)
        {
            var fraction = FillFraction(Value, Maximum);
            _fill.Width = _track.ActualWidth * fraction;
            _fill.Visibility = fraction > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(Label) ? text : $"{Label} {text}");
        VisualStateManager.GoToState(this, Enum.IsDefined(Metric) ? Metric.ToString() : nameof(SaMetricKind.Cpu), useTransitions: false);
    }
}
