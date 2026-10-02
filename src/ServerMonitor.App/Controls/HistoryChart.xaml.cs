using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Controls;

/// <summary>
/// A minimal, glass-friendly line chart for one 0–100% metric over time (ADR-015 §10). It renders an
/// already-downsampled <see cref="HistorySeries"/> — never SQL, never raw samples — as polylines in the neutral chart line colour
/// (D-UI3-1, SaChartLineBrush) over a vertical gradient area, with three dashed grid lines (100/50/0), an end marker on the last point
/// and the axes laid out OUTSIDE the plot canvases (UI.3, Figma 112:2298). Gaps (null values, offline periods,
/// app-closed windows) break the line; nothing is interpolated across them (spec §38/§91). The fixed 0–100 scale keeps
/// the charts comparable (spec §45). Purely presentational: no view-model knowledge, no domain logic.
/// </summary>
public sealed partial class HistoryChart : UserControl
{
    // Figma 112:2299: area gradient alpha .18 -> .01, end marker 7, grid dashes 3/5 at 12 % of the text colour.
    private const double AreaTopOpacity = 0.18;
    private const double AreaBottomOpacity = 0.01;
    private const double EndMarkerSize = 7;
    private const double GridLineOpacity = 0.12;
    private const double GridDashOn = 3;
    private const double GridDashOff = 5;

    public HistoryChart()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(HistorySeries), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty RangeStartProperty = DependencyProperty.Register(
        nameof(RangeStart), typeof(DateTimeOffset), typeof(HistoryChart), new PropertyMetadata(default(DateTimeOffset), OnChanged));

    public static readonly DependencyProperty RangeEndProperty = DependencyProperty.Register(
        nameof(RangeEnd), typeof(DateTimeOffset), typeof(HistoryChart), new PropertyMetadata(default(DateTimeOffset), OnChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty GridLineBrushProperty = DependencyProperty.Register(
        nameof(GridLineBrush), typeof(Brush), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
        nameof(LineThickness), typeof(double), typeof(HistoryChart), new PropertyMetadata(2.4, OnChanged));

    public static readonly DependencyProperty ShowEndMarkerProperty = DependencyProperty.Register(
        nameof(ShowEndMarker), typeof(bool), typeof(HistoryChart), new PropertyMetadata(true, OnChanged));

    public static readonly DependencyProperty XTicksProperty = DependencyProperty.Register(
        nameof(XTicks), typeof(IReadOnlyList<HistoryAxisTick>), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty XTicksCompactProperty = DependencyProperty.Register(
        nameof(XTicksCompact), typeof(IReadOnlyList<HistoryAxisTick>), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty YLabelsProperty = DependencyProperty.Register(
        nameof(YLabels), typeof(IReadOnlyList<string>), typeof(HistoryChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty CompactLabelsBelowProperty = DependencyProperty.Register(
        nameof(CompactLabelsBelow), typeof(double), typeof(HistoryChart), new PropertyMetadata(480d, OnChanged));

    public HistorySeries? Series
    {
        get => (HistorySeries?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public DateTimeOffset RangeStart
    {
        get => (DateTimeOffset)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public DateTimeOffset RangeEnd
    {
        get => (DateTimeOffset)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    /// <summary>Line, area and marker colour (History: the neutral SaChartLineBrush). A solid colour also derives the area gradient.</summary>
    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>Area fill used only when <see cref="LineBrush"/> is not a solid colour.</summary>
    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? GridLineBrush
    {
        get => (Brush?)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    /// <summary>Stroke of the line (SaChartLineThickness, 2.4).</summary>
    public double LineThickness
    {
        get => (double)GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    public bool ShowEndMarker
    {
        get => (bool)GetValue(ShowEndMarkerProperty);
        set => SetValue(ShowEndMarkerProperty, value);
    }

    /// <summary>X marks on round boundaries, each at its real position (D-UI3-4 revised), left to right.</summary>
    public IReadOnlyList<HistoryAxisTick>? XTicks
    {
        get => (IReadOnlyList<HistoryAxisTick>?)GetValue(XTicksProperty);
        set => SetValue(XTicksProperty, value);
    }

    /// <summary>At most three X marks, used when the plot is narrower than <see cref="CompactLabelsBelow"/>.</summary>
    public IReadOnlyList<HistoryAxisTick>? XTicksCompact
    {
        get => (IReadOnlyList<HistoryAxisTick>?)GetValue(XTicksCompactProperty);
        set => SetValue(XTicksCompactProperty, value);
    }

    /// <summary>Y labels top to bottom (100 / 50 / 0), one per grid line.</summary>
    public IReadOnlyList<string>? YLabels
    {
        get => (IReadOnlyList<string>?)GetValue(YLabelsProperty);
        set => SetValue(YLabelsProperty, value);
    }

    public double CompactLabelsBelow
    {
        get => (double)GetValue(CompactLabelsBelowProperty);
        set => SetValue(CompactLabelsBelowProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HistoryChart)d).Render();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        if (GridCanvas is null || PlotCanvas is null || YAxisCanvas is null || XAxisCanvas is null)
        {
            return;
        }

        GridCanvas.Children.Clear();
        PlotCanvas.Children.Clear();
        YAxisCanvas.Children.Clear();
        XAxisCanvas.Children.Clear();

        // Axis sizes come from the labels, so they are laid out first (independent of the plot size).
        var axisStyle = AxisTextStyle();
        var yLabels = YLabels ?? [];
        var yBlocks = yLabels.Select(text => MeasuredLabel(text, axisStyle)).ToList();
        YAxisCanvas.Width = yBlocks.Count == 0 ? 0 : yBlocks.Max(block => block.DesiredSize.Width);

        var width = PlotHost.ActualWidth;
        var height = PlotHost.ActualHeight;
        var xTicks = HistoryChartAxisLayout.ChooseXLabels(width, XTicks, XTicksCompact, CompactLabelsBelow);
        var xBlocks = xTicks.Select(tick => MeasuredLabel(tick.Label, axisStyle)).ToList();
        XAxisCanvas.Height = xBlocks.Count == 0 ? 0 : xBlocks.Max(block => block.DesiredSize.Height);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (var i = 0; i < yBlocks.Count; i++)
        {
            Canvas.SetTop(yBlocks[i], HistoryChartAxisLayout.YLabelTop(i, yBlocks.Count, yBlocks[i].DesiredSize.Height, height));
            YAxisCanvas.Children.Add(yBlocks[i]);
        }

        for (var i = 0; i < xBlocks.Count; i++)
        {
            Canvas.SetLeft(xBlocks[i], HistoryChartAxisLayout.XLabelLeft(xTicks[i].Fraction, xBlocks[i].DesiredSize.Width, width));
            XAxisCanvas.Children.Add(xBlocks[i]);
        }

        DrawGridlines(width, height);

        if (Series is not { } series)
        {
            return;
        }

        var segments = HistoryChartGeometry.BuildSegments(series, RangeStart, RangeEnd, width, height);
        foreach (var segment in segments)
        {
            DrawSegment(segment, height);
        }

        if (ShowEndMarker && segments.LastOrDefault(s => s.Count > 0) is { } last)
        {
            var end = last[^1];
            var marker = new Ellipse { Width = EndMarkerSize, Height = EndMarkerSize, Fill = LineBrush };
            Canvas.SetLeft(marker, end.X - EndMarkerSize / 2);
            Canvas.SetTop(marker, end.Y - EndMarkerSize / 2);
            PlotCanvas.Children.Add(marker);
        }
    }

    private static Style? AxisTextStyle() =>
        Application.Current?.Resources.TryGetValue("SaChartAxisTextStyle", out var style) == true ? style as Style : null;

    private static TextBlock MeasuredLabel(string text, Style? style)
    {
        var block = new TextBlock { Text = text };
        if (style is not null)
        {
            block.Style = style;
        }

        // The chart's accessible name carries the numbers; the axis text is decorative for UI Automation.
        AutomationProperties.SetAccessibilityView(block, AccessibilityView.Raw);
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block;
    }

    private void DrawGridlines(double width, double height)
    {
        var brush = GridLineBrush;
        if (brush is null)
        {
            return;
        }

        foreach (var y in HistoryChartAxisLayout.GridLineYs(height))
        {
            GridCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { GridDashOn, GridDashOff },
                Opacity = GridLineOpacity
            });
        }
    }

    private Brush? AreaBrush()
    {
        if (LineBrush is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = WithAlpha(color, AreaTopOpacity), Offset = 0 },
                    new GradientStop { Color = WithAlpha(color, AreaBottomOpacity), Offset = 1 }
                }
            };
        }

        return FillBrush;
    }

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B);

    private void DrawSegment(IReadOnlyList<ChartPoint> segment, double height)
    {
        if (segment.Count == 0)
        {
            return;
        }

        if (segment.Count == 1)
        {
            // A lone point (surrounded by gaps) is shown as a dot rather than an invisible line.
            var size = Math.Max(3, LineThickness * 1.5);
            var dot = new Ellipse { Width = size, Height = size, Fill = LineBrush };
            Canvas.SetLeft(dot, segment[0].X - size / 2);
            Canvas.SetTop(dot, segment[0].Y - size / 2);
            PlotCanvas.Children.Add(dot);
            return;
        }

        if (AreaBrush() is { } area)
        {
            var fill = new Polygon { Fill = area };
            var points = new PointCollection();
            foreach (var p in segment)
            {
                points.Add(new Point(p.X, p.Y));
            }

            points.Add(new Point(segment[^1].X, height));
            points.Add(new Point(segment[0].X, height));
            fill.Points = points;
            PlotCanvas.Children.Add(fill);
        }

        var line = new Polyline
        {
            Stroke = LineBrush,
            StrokeThickness = LineThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        var linePoints = new PointCollection();
        foreach (var p in segment)
        {
            linePoints.Add(new Point(p.X, p.Y));
        }

        line.Points = linePoints;
        PlotCanvas.Children.Add(line);
    }
}
