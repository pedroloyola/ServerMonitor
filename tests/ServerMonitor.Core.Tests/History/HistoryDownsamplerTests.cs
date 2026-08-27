using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.Core.Tests.History;

public sealed class HistoryDownsamplerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    private static ServerHistorySample Sample(double offsetSeconds, double? cpu) => new()
    {
        ServerId = ServerId,
        CapturedAtUtc = Start + TimeSpan.FromSeconds(offsetSeconds),
        Health = ServerHealth.Healthy,
        CpuPercent = cpu
    };

    private static HistorySeries Build(IReadOnlyList<ServerHistorySample> samples, TimeSpan range, int target = 300) =>
        HistoryDownsampler.Build(samples, Start, Start + range, static s => s.CpuPercent, target);

    [Fact]
    public void ZeroPoints_ReturnsEmptySeries()
    {
        var series = Build(Array.Empty<ServerHistorySample>(), TimeSpan.FromHours(1));

        Assert.Empty(series.Points);
        Assert.Null(series.Latest);
        Assert.Null(series.Maximum);
        Assert.False(series.HasData);
    }

    [Fact]
    public void SinglePoint_ReturnedRaw()
    {
        var series = Build([Sample(0, 42)], TimeSpan.FromHours(1));

        Assert.Single(series.Points);
        Assert.Equal(42, series.Points[0].Value);
        Assert.Equal(42, series.Latest);
        Assert.Equal(42, series.Maximum);
    }

    [Fact]
    public void CountAtOrBelowTarget_ReturnedRawInOrder()
    {
        var samples = Enumerable.Range(0, 100).Select(i => Sample(i * 30, i)).ToList();

        var series = Build(samples, TimeSpan.FromHours(1), target: 300);

        Assert.Equal(100, series.Points.Count);
        Assert.True(series.Points.SequenceEqual(series.Points.OrderBy(p => p.TimestampUtc)));
    }

    [Fact]
    public void ManyPoints_OutputIsBounded()
    {
        var samples = Enumerable.Range(0, 10_000)
            .Select(i => Sample(i * 259.2, i % 100)) // spread across 30 days
            .ToList();

        var series = Build(samples, TimeSpan.FromDays(30), target: 300);

        Assert.True(series.Points.Count <= 300, $"expected ≤300, got {series.Points.Count}");
        Assert.True(series.Points.Count > 0);
    }

    [Fact]
    public void NullValues_Preserved_NotConvertedToZero()
    {
        var samples = new[] { Sample(0, 10), Sample(30, null), Sample(60, 20) };

        var series = Build(samples, TimeSpan.FromHours(1));

        Assert.Equal(3, series.Points.Count);
        Assert.Null(series.Points[1].Value);
        Assert.Equal(20, series.Latest); // last non-null
    }

    [Fact]
    public void Bucketed_KeepsPeak_WorstCasePerBucket()
    {
        // 100 low samples over an hour with one spike; a coarse target forces bucketing.
        var samples = Enumerable.Range(0, 100).Select(i => Sample(i * 36, 10)).ToList();
        samples[50] = Sample(50 * 36, 95); // the spike

        var series = Build(samples, TimeSpan.FromHours(1), target: 10);

        Assert.True(series.Points.Count <= 10);
        Assert.Contains(series.Points, p => p.Value == 95); // peak never hidden
        Assert.Equal(95, series.Maximum);
    }

    [Fact]
    public void Bucketed_AllNullBucket_EmitsNullPoint_MixedBucketEmitsMax()
    {
        // Two dense clusters: first all-null (offline), second with values. Force bucketing.
        var samples = new List<ServerHistorySample>();
        for (var i = 0; i < 50; i++)
        {
            samples.Add(Sample(i * 10, null)); // 0..490s offline
        }

        for (var i = 0; i < 50; i++)
        {
            samples.Add(Sample(1800 + i * 10, 30 + i)); // 1800..2290s with rising values
        }

        var series = Build(samples, TimeSpan.FromHours(1), target: 20);

        Assert.Contains(series.Points, p => p.Value is null);      // offline cluster → null point(s)
        Assert.Contains(series.Points, p => p.Value == 79);        // max of second cluster (30+49)
        // Temporal order preserved.
        Assert.True(series.Points.SequenceEqual(series.Points.OrderBy(p => p.TimestampUtc)));
    }

    [Fact]
    public void EmptyPeriod_ProducesNoPointsInTheGap()
    {
        // Data only in the first and last 5 minutes of a 1-hour range; nothing in the middle.
        var samples = new List<ServerHistorySample>();
        for (var i = 0; i < 200; i++)
        {
            samples.Add(Sample(i * 1.5, 10)); // 0..~300s
        }

        for (var i = 0; i < 200; i++)
        {
            samples.Add(Sample(3300 + i * 1.5, 20)); // 3300..3600s
        }

        var series = Build(samples, TimeSpan.FromHours(1), target: 60);

        // No representative point should fall in the empty middle (600s .. 3000s).
        Assert.DoesNotContain(
            series.Points,
            p => p.TimestampUtc > Start + TimeSpan.FromSeconds(600) &&
                 p.TimestampUtc < Start + TimeSpan.FromSeconds(3000));
    }

    [Fact]
    public void ShortRange_MaxConnectGap_FlooredForDenseSampling()
    {
        var series = Build([Sample(0, 10), Sample(30, 12)], TimeSpan.FromHours(1));

        // 30s-spaced samples must remain connectable ⇒ gap floor ≥ 90s.
        Assert.True(series.MaxConnectGap >= TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HistoryDownsampler.Build(Array.Empty<ServerHistorySample>(), Start, Start, static s => s.CpuPercent, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HistoryDownsampler.Build(Array.Empty<ServerHistorySample>(), Start, Start - TimeSpan.FromHours(1), static s => s.CpuPercent));
    }
}
