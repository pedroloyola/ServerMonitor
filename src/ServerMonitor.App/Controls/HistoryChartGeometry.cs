using ServerMonitor.Core.History;

namespace ServerMonitor.App.Controls;

/// <summary>A plotted point in control pixels (UI-free so it is unit-testable without WinUI).</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>
/// Maps an already-downsampled <see cref="HistorySeries"/> to pixel polyline segments for the chart
/// (ADR-015 §10). Pure and deterministic: the Y axis is fixed at 0–100% (spec §45); a <c>null</c>
/// value breaks the line (offline/unmeasured — never drawn as 0, spec §38/§48); a time delta larger
/// than <see cref="HistorySeries.MaxConnectGap"/> breaks the line (data missing while the app was
/// closed, spec §91). Output is bounded by the number of input points.
/// </summary>
public static class HistoryChartGeometry
{
    public static IReadOnlyList<IReadOnlyList<ChartPoint>> BuildSegments(
        HistorySeries series,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        double width,
        double height)
    {
        var segments = new List<IReadOnlyList<ChartPoint>>();
        if (series is null || width <= 0 || height <= 0)
        {
            return segments;
        }

        var totalTicks = (endUtc - startUtc).Ticks;
        if (totalTicks <= 0)
        {
            return segments;
        }

        List<ChartPoint>? current = null;
        HistoryChartPoint? previous = null;
        foreach (var point in series.Points)
        {
            if (point.Value is null)
            {
                // Break the line: present-but-unmeasured (offline). Never plotted as zero.
                current = null;
                previous = null;
                continue;
            }

            if (previous is not null && point.TimestampUtc - previous.TimestampUtc > series.MaxConnectGap)
            {
                // Break the line across a data gap (app was closed): do not connect over emptiness.
                current = null;
            }

            var x = (double)(point.TimestampUtc - startUtc).Ticks / totalTicks * width;
            var value = Math.Clamp(point.Value.Value, 0, 100);
            var y = height - (value / 100.0 * height);

            if (current is null)
            {
                current = [];
                segments.Add(current);
            }

            current.Add(new ChartPoint(x, y));
            previous = point;
        }

        return segments;
    }
}
