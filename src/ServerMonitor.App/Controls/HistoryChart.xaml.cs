using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Controls;

/// <summary>
/// A minimal, glass-friendly line chart for one 0–100% metric over time (ADR-015 §10). It renders an
/// already-downsampled <see cref="HistorySeries"/> — never SQL, never raw samples — as brand-coloured
/// polylines with a subtle area fill and fixed 0/25/50/75/100 gridlines. Gaps (null values, offline
/// periods, app-closed windows) break the line; nothing is interpolated across them (spec §38/§91).
/// </summary>
public sealed partial class HistoryChart : UserControl
{
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

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

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

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HistoryChart)d).Render();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        if (GridCanvas is null || PlotCanvas is null)
        {
            return;
        }

        GridCanvas.Children.Clear();
        PlotCanvas.Children.Clear();

        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
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
    }

    private void DrawGridlines(double width, double height)
    {
        var brush = GridLineBrush;
        if (brush is null)
        {
            return;
        }

        // 0 / 25 / 50 / 75 / 100 % — fixed axis so charts are visually comparable (spec §45).
        for (var i = 0; i <= 4; i++)
        {
            var y = height - (i / 4.0 * height);
            var line = new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = 1,
                Opacity = i == 0 ? 0.7 : 0.35
            };
            GridCanvas.Children.Add(line);
        }
    }

    private void DrawSegment(IReadOnlyList<ChartPoint> segment, double height)
    {
        if (segment.Count == 0)
        {
            return;
        }

        if (segment.Count == 1)
        {
            // A lone point (surrounded by gaps) is shown as a dot rather than an invisible line.
            var dot = new Ellipse { Width = 3, Height = 3, Fill = LineBrush };
            Canvas.SetLeft(dot, segment[0].X - 1.5);
            Canvas.SetTop(dot, segment[0].Y - 1.5);
            PlotCanvas.Children.Add(dot);
            return;
        }

        if (FillBrush is not null)
        {
            var fill = new Polygon { Fill = FillBrush };
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
            StrokeThickness = 2,
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
