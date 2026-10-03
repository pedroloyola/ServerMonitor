using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.5 segmented metric track (Figma "Memory segments" 112:1855: 28 segments 6×29 r3 gap 4; "Disk segmented capacity"
/// 112:1890: 14 segments 16×18 r5 gap 5). The segments share the width equally (the Figma's 6.43 / 15.93 widths are the
/// 288-wide track divided by the count) and the lit ones take the Manual metric colour.
/// <para>
/// UNKNOWN IS NOT ZERO: <see cref="LitCount"/> defaults to -1 (unknown), which draws an EMPTY track — the page shows "—"
/// beside it. The lit count itself is the page's rule (A-4); this control only draws it. Decorative for UI Automation:
/// the value is announced by the card's text.
/// </para>
/// </summary>
[TemplatePart(Name = HostPartName, Type = typeof(Grid))]
[TemplatePart(Name = LitBrushPartName, Type = typeof(Border))]
[TemplatePart(Name = EmptyBrushPartName, Type = typeof(Border))]
[TemplateVisualState(Name = "Cpu", GroupName = "MetricStates")]
[TemplateVisualState(Name = "Memory", GroupName = "MetricStates")]
[TemplateVisualState(Name = "Disk", GroupName = "MetricStates")]
public sealed class SaSegmentMeter : Control
{
    private const string HostPartName = "PART_Host";
    private const string LitBrushPartName = "PART_LitBrush";
    private const string EmptyBrushPartName = "PART_EmptyBrush";

    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount), typeof(int), typeof(SaSegmentMeter), new PropertyMetadata(28, (d, _) => ((SaSegmentMeter)d).Rebuild()));

    public static readonly DependencyProperty LitCountProperty = DependencyProperty.Register(
        nameof(LitCount), typeof(int), typeof(SaSegmentMeter), new PropertyMetadata(-1, (d, _) => ((SaSegmentMeter)d).Rebuild()));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(SaMetricKind), typeof(SaSegmentMeter), new PropertyMetadata(SaMetricKind.Memory, (d, _) => ((SaSegmentMeter)d).UpdateMetricState()));

    public static readonly DependencyProperty SegmentHeightProperty = DependencyProperty.Register(
        nameof(SegmentHeight), typeof(double), typeof(SaSegmentMeter), new PropertyMetadata(29d, (d, _) => ((SaSegmentMeter)d).Rebuild()));

    public static readonly DependencyProperty SegmentSpacingProperty = DependencyProperty.Register(
        nameof(SegmentSpacing), typeof(double), typeof(SaSegmentMeter), new PropertyMetadata(4d, (d, _) => ((SaSegmentMeter)d).Rebuild()));

    public static readonly DependencyProperty SegmentCornerRadiusProperty = DependencyProperty.Register(
        nameof(SegmentCornerRadius), typeof(CornerRadius), typeof(SaSegmentMeter), new PropertyMetadata(new CornerRadius(3), (d, _) => ((SaSegmentMeter)d).Rebuild()));

    private Grid? _host;
    private Border? _litBrush;
    private Border? _emptyBrush;

    public SaSegmentMeter()
    {
        DefaultStyleKey = typeof(SaSegmentMeter);
        IsTabStop = false;
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    /// <summary>Lit segments; any negative value (the default -1) = unknown = none lit.</summary>
    public int LitCount
    {
        get => (int)GetValue(LitCountProperty);
        set => SetValue(LitCountProperty, value);
    }

    public SaMetricKind Metric
    {
        get => (SaMetricKind)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public double SegmentHeight
    {
        get => (double)GetValue(SegmentHeightProperty);
        set => SetValue(SegmentHeightProperty, value);
    }

    public double SegmentSpacing
    {
        get => (double)GetValue(SegmentSpacingProperty);
        set => SetValue(SegmentSpacingProperty, value);
    }

    public CornerRadius SegmentCornerRadius
    {
        get => (CornerRadius)GetValue(SegmentCornerRadiusProperty);
        set => SetValue(SegmentCornerRadiusProperty, value);
    }

    /// <summary>How many of <paramref name="count"/> segments are drawn lit for <paramref name="lit"/> (pure, tested).</summary>
    public static int EffectiveLit(int lit, int count) => count <= 0 || lit < 0 ? 0 : Math.Min(lit, count);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild(HostPartName) as Grid;
        _litBrush = GetTemplateChild(LitBrushPartName) as Border;
        _emptyBrush = GetTemplateChild(EmptyBrushPartName) as Border;
        UpdateMetricState();
        Rebuild();
    }

    private void UpdateMetricState() => VisualStateManager.GoToState(this, Metric.ToString(), useTransitions: false);

    private void Rebuild()
    {
        if (_host is null || _litBrush is null || _emptyBrush is null)
        {
            return;
        }

        var count = Math.Max(0, SegmentCount);
        var lit = EffectiveLit(LitCount, count);
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.ColumnSpacing = SegmentSpacing;
        for (var index = 0; index < count; index++)
        {
            _host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var segment = new Border
            {
                Height = SegmentHeight,
                CornerRadius = SegmentCornerRadius,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Theme-aware: bound to the template's ThemeResource brushes, so a theme change repaints (never resolved once).
            segment.SetBinding(Border.BackgroundProperty, new Binding
            {
                Source = index < lit ? _litBrush : _emptyBrush,
                Path = new PropertyPath(nameof(Border.Background))
            });
            Grid.SetColumn(segment, index);
            _host.Children.Add(segment);
        }
    }
}
