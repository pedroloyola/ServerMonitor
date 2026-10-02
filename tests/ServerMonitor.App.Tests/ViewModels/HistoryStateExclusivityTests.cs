using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// Cortex R1 M-1: the History page shows EXACTLY ONE of loading / unavailable / empty / charts in every state - also
/// "loading after data" - and switching server never presents server A's series, peaks, summaries or period while B
/// loads. Deterministic: queries are completed by hand (TaskCompletionSource), no waiting.
/// </summary>
public sealed class HistoryStateExclusivityTests
{
    private static readonly DateTimeOffset End = new(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);

    private sealed class ManualQuery : IServerHistoryQueryService
    {
        public bool IsAvailable { get; set; } = true;

        public Queue<TaskCompletionSource<ServerHistoryResult>> Pending { get; } = new();

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<ServerHistoryResult>();
            Pending.Enqueue(tcs);
            return tcs.Task;
        }

        public void CompleteNext(ServerHistoryResult result) => Pending.Dequeue().SetResult(result);

        public void FailNext() => Pending.Dequeue().SetException(new InvalidOperationException("QA"));
    }

    private static ServerHistoryResult Data(Guid id, HistoryTimeRange range = HistoryTimeRange.Last24Hours, double peak = 78)
    {
        var series = new HistorySeries
        {
            Points = [new HistoryChartPoint { TimestampUtc = End.AddHours(-1), Value = 10 }],
            MaxConnectGap = TimeSpan.FromMinutes(1),
            Latest = 10,
            Maximum = peak
        };
        return new ServerHistoryResult
        {
            ServerId = id, Range = range, StartUtc = End - range.ToDuration(), EndUtc = End,
            Cpu = series, Memory = series, Disk = series
        };
    }

    private static ServerHistoryResult Empty(Guid id, HistoryTimeRange range = HistoryTimeRange.Last24Hours) =>
        ServerHistoryResult.Empty(id, range, End - range.ToDuration(), End);

    private static HistoryViewModel New(ManualQuery query, FakeServerService? servers = null) => new(
        query, new FakeServerMetricsStore(), new ServerMonitoringStateStore(), servers ?? new FakeServerService(),
        new FakeNavigationService(), new ResWLocalizationService("pt-PT"), NullLogger<HistoryViewModel>.Instance, new FakeTimeProvider(End));

    private static void AssertExactlyOne(HistoryViewModel vm, string expected)
    {
        var shown = new Dictionary<string, bool>
        {
            ["loading"] = vm.ShowLoading,
            ["unavailable"] = vm.ShowUnavailable,
            ["empty"] = vm.ShowEmpty,
            ["charts"] = vm.ShowCharts
        };
        Assert.Equal(new[] { expected }, shown.Where(s => s.Value).Select(s => s.Key));
    }

    [Fact]
    public void ExactlyOneState_ThroughLoadingDataReloadEmptyAndFailure()
    {
        var id = Guid.NewGuid();
        var query = new ManualQuery();
        var vm = New(query);

        vm.Load(id, "A");
        AssertExactlyOne(vm, "loading");

        query.CompleteNext(Data(id));
        AssertExactlyOne(vm, "charts");

        vm.SelectedRangeIndex = 3;                       // loading AFTER data: the charts must not stay on top
        AssertExactlyOne(vm, "loading");
        Assert.True(vm.IsLoading);

        query.CompleteNext(Empty(id, HistoryTimeRange.Last7Days));   // empty 7 days -> 30-day probe still loading
        AssertExactlyOne(vm, "loading");
        query.CompleteNext(Empty(id, HistoryTimeRange.Last30Days));
        AssertExactlyOne(vm, "empty");

        vm.SelectedRangeIndex = 2;
        AssertExactlyOne(vm, "loading");
        query.FailNext();
        AssertExactlyOne(vm, "unavailable");

        query.IsAvailable = false;
        vm.SelectedRangeIndex = 1;
        AssertExactlyOne(vm, "unavailable");
    }

    [Fact]
    public void SwitchingServer_NeverPresentsTheOldServersValuesWhileTheNewOneLoads()
    {
        var a = new Server { Id = Guid.NewGuid(), Name = "web-01", Host = "198.51.100.1", CreatedAt = End };
        var b = new Server { Id = Guid.NewGuid(), Name = "db-02", Host = "198.51.100.2", CreatedAt = End.AddSeconds(1) };
        var servers = new FakeServerService();
        servers.Servers.AddRange([a, b]);
        var query = new ManualQuery();
        var vm = New(query, servers);
        vm.Load(a.Id, a.Name);
        query.CompleteNext(Data(a.Id, peak: 91));
        Assert.Equal("Pico no período 91%", vm.CpuPeakDisplay);
        Assert.NotEmpty(vm.XAxisLabels);
        Assert.NotEqual(string.Empty, vm.CpuSummary);

        vm.SelectedServer = vm.Servers.Single(o => o.Id == b.Id);   // B's query is still pending

        AssertExactlyOne(vm, "loading");
        Assert.Equal("db-02", vm.Title);
        Assert.Null(vm.CpuSeries);
        Assert.Null(vm.MemorySeries);
        Assert.Null(vm.DiskSeries);
        Assert.Equal(string.Empty, vm.CpuPeakDisplay);
        Assert.Equal(string.Empty, vm.MemoryPeakDisplay);
        Assert.Equal(string.Empty, vm.DiskPeakDisplay);
        Assert.Equal(string.Empty, vm.CpuSummary);
        Assert.Equal(string.Empty, vm.PeriodFooter);
        Assert.Empty(vm.XAxisLabels);
        Assert.Equal(default, vm.RangeStartUtc);
        Assert.Equal(HistoryEmptyKind.None, vm.EmptyKind);

        query.CompleteNext(Data(b.Id, peak: 12));
        AssertExactlyOne(vm, "charts");
        Assert.Equal("Pico no período 12%", vm.CpuPeakDisplay);
    }
}
