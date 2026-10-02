namespace ServerMonitor.App.Controls;

/// <summary>
/// Pure layout for the History chart axes and grid (UI.3, Figma 112:2298), kept apart from rendering so it is
/// unit-testable. The plot itself is still mapped by <see cref="HistoryChartGeometry.BuildSegments"/> (unchanged).
/// </summary>
public static class HistoryChartAxisLayout
{
    /// <summary>Y of the three dashed grid lines (100 / 50 / 0 %), top to bottom.</summary>
    public static IReadOnlyList<double> GridLineYs(double plotHeight) =>
        plotHeight <= 0 ? [] : [0, plotHeight / 2, plotHeight];

    /// <summary>
    /// Left edge of X label <paramref name="index"/> of <paramref name="count"/>: centred on its quartile tick and
    /// clamped inside the plot, so the first label starts at the left edge and the last ends at the right edge.
    /// </summary>
    public static double XLabelLeft(int index, int count, double labelWidth, double plotWidth)
    {
        if (count <= 1 || plotWidth <= 0)
        {
            return 0;
        }

        var tick = plotWidth * index / (count - 1);
        var left = tick - labelWidth / 2;
        return Math.Clamp(left, 0, Math.Max(0, plotWidth - labelWidth));
    }

    /// <summary>Top of Y label <paramref name="index"/> of <paramref name="count"/>, vertically centred on its grid line.</summary>
    public static double YLabelTop(int index, int count, double labelHeight, double plotHeight)
    {
        if (count <= 1 || plotHeight <= 0)
        {
            return 0;
        }

        return plotHeight * index / (count - 1) - labelHeight / 2;
    }

    /// <summary>The compact (3-label) X axis below <paramref name="threshold"/> DIPs of plot width (D-UI3-4), else the full one.</summary>
    public static IReadOnlyList<string> ChooseXLabels(
        double plotWidth,
        IReadOnlyList<string>? full,
        IReadOnlyList<string>? compact,
        double threshold) =>
        plotWidth < threshold && compact is { Count: > 0 }
            ? compact
            : full ?? [];
}
