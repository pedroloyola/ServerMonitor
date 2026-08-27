using ServerMonitor.App.Controls;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.Controls;

public sealed class HistoryChartGeometryTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start + TimeSpan.FromHours(1);

    private static HistorySeries Series(TimeSpan maxGap, params (double offsetMinutes, double? value)[] points) => new()
    {
        Points = points
            .Select(p => new HistoryChartPoint { TimestampUtc = Start + TimeSpan.FromMinutes(p.offsetMinutes), Value = p.value })
            .ToList(),
        MaxConnectGap = maxGap
    };

    [Fact]
    public void ContinuousData_ProducesSingleSegment()
    {
        var series = Series(TimeSpan.FromMinutes(15), (0, 10), (10, 20), (20, 30));

        var segments = HistoryChartGeometry.BuildSegments(series, Start, End, 600, 100);

        var segment = Assert.Single(segments);
        Assert.Equal(3, segment.Count);
        Assert.True(segment[0].X < segment[1].X && segment[1].X < segment[2].X);
    }

    [Fact]
    public void NullValue_BreaksTheLine()
    {
        var series = Series(TimeSpan.FromMinutes(5), (0, 10), (10, null), (20, 30));

        var segments = HistoryChartGeometry.BuildSegments(series, Start, End, 600, 100);

        Assert.Equal(2, segments.Count);
        Assert.Single(segments[0]);
        Assert.Single(segments[1]);
    }

    [Fact]
    public void TimeGapBeyondMaxConnectGap_BreaksTheLine()
    {
        // Two points 30 min apart with a 5-min connect gap → not joined (app-closed gap).
        var series = Series(TimeSpan.FromMinutes(5), (0, 10), (30, 20));

        var segments = HistoryChartGeometry.BuildSegments(series, Start, End, 600, 100);

        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public void SmallTimeGapWithinConnectGap_StaysConnected()
    {
        var series = Series(TimeSpan.FromMinutes(5), (0, 10), (3, 20));

        var segments = HistoryChartGeometry.BuildSegments(series, Start, End, 600, 100);

        Assert.Single(segments);
    }

    [Fact]
    public void YAxis_IsFixedZeroToHundred()
    {
        var series = Series(TimeSpan.FromMinutes(5), (0, 100), (30, 0), (60, 50));

        var segments = HistoryChartGeometry.BuildSegments(series, Start, End, 600, 200);
        var points = segments.SelectMany(s => s).ToList();

        Assert.Equal(0, points[0].Y, 3);     // 100% → top (y = 0)
        Assert.Equal(200, points[1].Y, 3);   // 0% → bottom (y = height)
        Assert.Equal(100, points[2].Y, 3);   // 50% → middle
    }

    [Fact]
    public void EmptySeries_ProducesNoSegments()
    {
        Assert.Empty(HistoryChartGeometry.BuildSegments(HistorySeries.Empty, Start, End, 600, 100));
    }

    [Fact]
    public void NonPositiveSize_ProducesNoSegments()
    {
        var series = Series(TimeSpan.FromMinutes(5), (0, 10), (10, 20));

        Assert.Empty(HistoryChartGeometry.BuildSegments(series, Start, End, 0, 100));
        Assert.Empty(HistoryChartGeometry.BuildSegments(series, Start, End, 600, 0));
    }

    [Fact]
    public void XCoordinates_MapAcrossFullWidth()
    {
        var series = Series(TimeSpan.FromHours(2), (0, 10), (60, 20)); // start and midpoint of a 1h range

        var segment = Assert.Single(HistoryChartGeometry.BuildSegments(series, Start, End, 600, 100));

        Assert.Equal(0, segment[0].X, 3);     // at range start → x = 0
        Assert.Equal(600, segment[1].X, 3);   // at range end → x = width
    }
}
