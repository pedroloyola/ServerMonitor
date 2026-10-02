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
    /// Left edge of an X label centred on its mark at <paramref name="fraction"/> of the plot width (D-UI3-4 revised: the
    /// REAL position of a round time), clamped inside the plot so edge labels never overflow it.
    /// </summary>
    public static double XLabelLeft(double fraction, double labelWidth, double plotWidth)
    {
        if (plotWidth <= 0)
        {
            return 0;
        }

        var left = plotWidth * Math.Clamp(fraction, 0, 1) - labelWidth / 2;
        return Math.Clamp(left, 0, Math.Max(0, plotWidth - labelWidth));
    }

    /// <summary>Top of Y label <paramref name="index"/> of <paramref name="count"/>, vertically centred on its grid line.</summary>
    public static double YLabelTop(int index, int count, double labelHeight, double plotHeight)
    {
        if (count <= 1 || plotHeight <= 0)
        {
            return 0;
        }

        // Figma 112:2306: "100" sits BELOW the top grid line (inside the plot); "50" and "0" are centred on theirs.
        var top = plotHeight * index / (count - 1) - labelHeight / 2;
        return index == 0 ? Math.Max(0, top) : top;
    }

    /// <summary>The compact (3-label) X axis below <paramref name="threshold"/> DIPs of plot width (D-UI3-4), else the full one.</summary>
    public static IReadOnlyList<T> ChooseXLabels<T>(
        double plotWidth,
        IReadOnlyList<T>? full,
        IReadOnlyList<T>? compact,
        double threshold) =>
        plotWidth < threshold && compact is { Count: > 0 }
            ? compact
            : full ?? [];
}
