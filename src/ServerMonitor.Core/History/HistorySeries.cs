namespace ServerMonitor.Core.History;

/// <summary>One point of a downsampled chart series. A <c>null</c> <see cref="Value"/> marks a
/// present-but-unmeasured instant (e.g. offline) and renders as a break in the line — never as 0.</summary>
public sealed record HistoryChartPoint
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required double? Value { get; init; }
}

/// <summary>
/// A downsampled, chart-ready series for one metric over a range. <see cref="MaxConnectGap"/> is the
/// largest time delta between two consecutive points that the renderer may join with a line; a
/// bigger delta means data is missing (app was closed) and the line must break (spec §38, §91).
/// </summary>
public sealed record HistorySeries
{
    public static readonly HistorySeries Empty = new()
    {
        Points = Array.Empty<HistoryChartPoint>(),
        MaxConnectGap = TimeSpan.Zero,
        Latest = null,
        Maximum = null
    };

    // Defaulted (not 'required') so the WinUI XAML type-info generator can activate this type when it
    // appears as a DependencyProperty type; the downsampler and Empty always set both explicitly.
    public IReadOnlyList<HistoryChartPoint> Points { get; init; } = Array.Empty<HistoryChartPoint>();

    public TimeSpan MaxConnectGap { get; init; }

    /// <summary>Most recent non-null value in the window (for the accessible summary; the live
    /// "current value" shown on the chart comes from current state, not history — spec §47).</summary>
    public double? Latest { get; init; }

    /// <summary>Highest non-null value in the window (accessible summary, spec §67).</summary>
    public double? Maximum { get; init; }

    public bool HasData => Points.Any(point => point.Value is not null);
}
