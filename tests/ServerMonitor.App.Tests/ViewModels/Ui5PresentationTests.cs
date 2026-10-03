using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.5: the pure rules behind the Server Detail (shared status copy, A-4 segments, H-UI5-3 pulse, A-13 address).</summary>
public sealed class Ui5PresentationTests
{
    [Theory]
    [InlineData(ServerHealth.Healthy, "ServerStatusHealthy")]
    [InlineData(ServerHealth.Warning, "ServerStatusWarning")]
    [InlineData(ServerHealth.Critical, "ServerStatusCritical")]
    [InlineData(ServerHealth.Offline, "ServerStatusOffline")]
    [InlineData(ServerHealth.Unknown, "ServerStatusUnknown")]
    [InlineData((ServerHealth)99, "ServerStatusUnknown")]
    public void TheStatusKey_IsTheRowsTable(ServerHealth health, string key) =>
        Assert.Equal(key, ServerStatusPresentation.StatusKey(health));

    [Fact]
    public void TheVisibleCopy_NeverSaysOffline_InAnyCulture()
    {
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var localization = new ResWLocalizationService(culture);
            foreach (var health in Enum.GetValues<ServerHealth>())
            {
                Assert.DoesNotContain("Offline", ServerStatusPresentation.StatusText(health, localization), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData(ServerHealth.Offline, true)]
    [InlineData(ServerHealth.Unknown, false)]
    [InlineData(ServerHealth.Healthy, false)]
    public void OnlyOffline_HidesTheRowsRetainedMetrics(ServerHealth health, bool hides) =>
        Assert.Equal(hides, ServerStatusPresentation.RowHidesRetainedMetrics(health));

    [Theory]
    [InlineData(ServerHealth.Offline, false, true, true)]
    [InlineData(ServerHealth.Healthy, true, true, true)]
    [InlineData(ServerHealth.Healthy, false, true, false)]
    [InlineData(ServerHealth.Offline, true, false, false)] // nothing retained → nothing to mark
    public void H_UI5_2_ARetainedReadingIsMarkedStale(ServerHealth health, bool isStale, bool hasMetrics, bool expected) =>
        Assert.Equal(expected, ServerStatusPresentation.ShowsRetainedReadingAsStale(health, isStale, hasMetrics));

    /// <summary>A-4 (Boss): round, minimum 1 when p &gt; 0; 0, 1, 99, 100 and unknown are mandatory cases.</summary>
    [Theory]
    [InlineData(0d, 28, 0)]
    [InlineData(1d, 28, 1)]       // 0.28 rounds to 0 → minimum 1
    [InlineData(0.01, 14, 1)]
    [InlineData(99d, 28, 28)]     // 27.72 → 28
    [InlineData(99d, 14, 14)]     // 13.86 → 14
    [InlineData(100d, 28, 28)]
    [InlineData(100d, 14, 14)]
    [InlineData(62d, 28, 17)]     // Figma 112:1855
    [InlineData(48d, 14, 7)]      // Figma 112:1890
    [InlineData(50d, 14, 7)]
    [InlineData(-5d, 14, 0)]
    [InlineData(140d, 14, 14)]
    public void A4_LitSegments(double percent, int total, int expected) =>
        Assert.Equal(expected, MetricVisualPresentation.LitSegments(percent, total));

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    public void A4_Unknown_IsNoSegmentCount_NotZero(double? percent)
    {
        Assert.Null(MetricVisualPresentation.LitSegments(percent, MetricVisualPresentation.MemorySegmentCount));
        Assert.Null(MetricVisualPresentation.LitSegments(percent, MetricVisualPresentation.DiskSegmentCount));
    }

    [Fact]
    public void TheSegmentCounts_AreTheFigmas() =>
        Assert.Equal((28, 14, 30), (MetricVisualPresentation.MemorySegmentCount, MetricVisualPresentation.DiskSegmentCount, MetricVisualPresentation.CpuPulseSampleCount));

    [Fact]
    public void ThePulse_KeepsOrder_DropsUnmeasured_AndNeverPads()
    {
        Assert.Empty(MetricVisualPresentation.CpuPulse(null));
        Assert.Empty(MetricVisualPresentation.CpuPulse(HistorySeries.Empty));

        var series = new HistorySeries
        {
            Points =
            [
                Point(0, 10), Point(30, null), Point(60, double.NaN), Point(90, 120), Point(120, 5)
            ]
        };
        Assert.Equal(new double[] { 10, 100, 5 }, MetricVisualPresentation.CpuPulse(series));
    }

    [Fact]
    public void ThePulse_IsAtMost30_TheMostRecent()
    {
        // One hour at the 30 s sampling policy: 120 raw samples (values 0.5 … 60).
        var series = new HistorySeries { Points = Enumerable.Range(1, 120).Select(i => Point(i * 30, i * 0.5)).ToList() };

        var pulse = MetricVisualPresentation.CpuPulse(series);

        Assert.Equal(30, pulse.Count);
        Assert.Equal(45.5, pulse[0]);
        Assert.Equal(60, pulse[^1]);
    }

    [Theory]
    [InlineData("web.local", 22, "web.local:22")]
    [InlineData("192.0.2.10", 2222, "192.0.2.10:2222")]
    [InlineData("::1", 22, "[::1]:22")]
    [InlineData("2001:db8::10", 2222, "[2001:db8::10]:2222")]
    [InlineData("[::1]", 22, "[::1]:22")]
    public void A13_TheAddress_IsTheEndpointFormat(string host, int port, string expected) =>
        Assert.Equal(expected, OverviewPresentation.Endpoint(host, port));

    /// <summary>One function, two consumers: neither the rows nor the Detail build the status key themselves.</summary>
    [Fact]
    public void RowsAndDetail_ReadTheSameStatusFunction()
    {
        foreach (var file in new[] { "ViewModels/ServerDirectoryRowViewModel.cs", "ViewModels/ServerDetailViewModel.cs" })
        {
            var source = File.ReadAllText(Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.App", file));
            Assert.Contains("ServerStatusPresentation.StatusText(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("$\"ServerStatus{", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ServerHealth{", source, StringComparison.Ordinal);
        }
    }

    private static HistoryChartPoint Point(int seconds, double? value) =>
        new() { TimestampUtc = Ui4TestKit.Now.AddSeconds(seconds), Value = value };
}
