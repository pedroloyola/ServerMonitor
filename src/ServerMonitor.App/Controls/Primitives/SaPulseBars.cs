using System.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.5 CPU pulse (Figma "CPU pulse / 30 samples" 112:1818: 30 bars 6 wide r3 gap 4, bottom-aligned in a 40-high track,
/// opacity ramping .30 → 1.0 from the oldest slot to the newest). The slots share the width equally.
/// <para>
/// REAL SAMPLES ONLY (H-UI5-3): <see cref="Samples"/> are percentages, oldest first. Fewer than <see cref="Capacity"/>
/// fill the RIGHTMOST slots and the others stay empty — nothing is padded or invented; no samples draw no bars.
/// <para>
/// Scale (Boss decision, fix round 1): ABSOLUTE - 0–100 % maps to the full track height, because an auto-ranged track
/// misrepresents load. DERIVED (no Figma rule): a sample above 0 keeps at least <see cref="MinimumBarHeight"/> so it reads
/// as measured, not missing; a measured 0 draws no bar in its slot. Decorative for UI Automation: the card's text carries
/// the value.
/// </para>
/// </para>
/// </summary>
[TemplatePart(Name = HostPartName, Type = typeof(Grid))]
[TemplatePart(Name = BarBrushPartName, Type = typeof(Border))]
public sealed class SaPulseBars : Control
{
    /// <summary>Figma ramp: the oldest slot is drawn at this opacity, the newest at 1.</summary>
    public const double OldestOpacity = 0.30;

    private const string HostPartName = "PART_Host";
    private const string BarBrushPartName = "PART_BarBrush";

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IEnumerable), typeof(SaPulseBars), new PropertyMetadata(null, (d, _) => ((SaPulseBars)d).Rebuild()));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(SaPulseBars), new PropertyMetadata(30, (d, _) => ((SaPulseBars)d).Rebuild()));

    public static readonly DependencyProperty BarSpacingProperty = DependencyProperty.Register(
        nameof(BarSpacing), typeof(double), typeof(SaPulseBars), new PropertyMetadata(4d, (d, _) => ((SaPulseBars)d).Rebuild()));

    public static readonly DependencyProperty MinimumBarHeightProperty = DependencyProperty.Register(
        nameof(MinimumBarHeight), typeof(double), typeof(SaPulseBars), new PropertyMetadata(3d, (d, _) => ((SaPulseBars)d).Rebuild()));

    private Grid? _host;
    private Border? _barBrush;

    public SaPulseBars()
    {
        DefaultStyleKey = typeof(SaPulseBars);
        IsTabStop = false;
    }

    /// <summary>The measured CPU percentages, oldest first (any <see cref="IEnumerable"/> of numbers).</summary>
    public IEnumerable? Samples
    {
        get => (IEnumerable?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public double BarSpacing
    {
        get => (double)GetValue(BarSpacingProperty);
        set => SetValue(BarSpacingProperty, value);
    }

    public double MinimumBarHeight
    {
        get => (double)GetValue(MinimumBarHeightProperty);
        set => SetValue(MinimumBarHeightProperty, value);
    }

    /// <summary>The bars to draw: (slot, height, opacity) for each kept sample, right-aligned (pure, tested).</summary>
    public static IReadOnlyList<(int Slot, double Height, double Opacity)> Layout(
        IReadOnlyList<double> samples, int capacity, double trackHeight, double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (capacity <= 0 || samples.Count == 0 || trackHeight <= 0)
        {
            return [];
        }

        var kept = samples.Count > capacity ? samples.Skip(samples.Count - capacity).ToList() : samples.ToList();
        var first = capacity - kept.Count;
        var floor = Math.Min(minimumHeight, trackHeight);
        var bars = new List<(int, double, double)>(kept.Count);
        for (var index = 0; index < kept.Count; index++)
        {
            var value = double.IsNaN(kept[index]) ? 0 : Math.Clamp(kept[index], 0, 100);
            var height = value <= 0 ? 0 : Math.Max(floor, trackHeight * value / 100);
            var slot = first + index;
            var opacity = capacity == 1 ? 1 : OldestOpacity + ((1 - OldestOpacity) * slot / (capacity - 1));
            bars.Add((slot, Math.Round(height, 2), Math.Round(opacity, 3)));
        }

        return bars;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _host = GetTemplateChild(HostPartName) as Grid;
        _barBrush = GetTemplateChild(BarBrushPartName) as Border;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_host is null || _barBrush is null)
        {
            return;
        }

        var capacity = Math.Max(0, Capacity);
        var samples = Samples?.Cast<object>().Select(Convert.ToDouble).ToList() ?? [];
        var track = double.IsNaN(Height) || Height <= 0 ? 40 : Height;
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.ColumnSpacing = BarSpacing;
        for (var slot = 0; slot < capacity; slot++)
        {
            _host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        foreach (var (slot, height, opacity) in Layout(samples, capacity, track, MinimumBarHeight))
        {
            var bar = new Border
            {
                Height = height,
                Opacity = opacity,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            // Theme-aware: bound to the template's SaCpuBrush element (never resolved once in code).
            bar.SetBinding(Border.BackgroundProperty, new Binding { Source = _barBrush, Path = new PropertyPath(nameof(Border.Background)) });
            Grid.SetColumn(bar, slot);
            _host.Children.Add(bar);
        }
    }
}
