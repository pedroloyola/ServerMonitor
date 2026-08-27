using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class HistoryViewModelTests
{
    private sealed class ControllableHistoryQueryService : IServerHistoryQueryService
    {
        private readonly Dictionary<HistoryTimeRange, TaskCompletionSource<ServerHistoryResult>> _pending = new();

        public bool Available { get; set; } = true;

        public bool ThrowOnQuery { get; set; }

        public Func<HistoryTimeRange, ServerHistoryResult>? Immediate { get; set; }

        public bool IsAvailable => Available;

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken)
        {
            if (ThrowOnQuery)
            {
                throw new InvalidOperationException("QA boom");
            }

            if (Immediate is not null)
            {
                return Task.FromResult(Immediate(range));
            }

            var tcs = new TaskCompletionSource<ServerHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[range] = tcs;
            return tcs.Task;
        }

        public void Complete(HistoryTimeRange range, ServerHistoryResult result) => _pending[range].SetResult(result);
    }

    private static ServerHistoryResult Result(HistoryTimeRange range, bool empty)
    {
        var end = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var start = end - range.ToDuration();
        var series = empty
            ? HistorySeries.Empty
            : new HistorySeries
            {
                Points = [new HistoryChartPoint { TimestampUtc = start, Value = 10 }],
                MaxConnectGap = TimeSpan.FromMinutes(1),
                Latest = 10,
                Maximum = 10
            };

        return new ServerHistoryResult
        {
            ServerId = Guid.Empty,
            Range = range,
            StartUtc = start,
            EndUtc = end,
            Cpu = series,
            Memory = series,
            Disk = series
        };
    }

    private static (HistoryViewModel vm, ControllableHistoryQueryService query, FakeServerMetricsStore metrics, FakeNavigationService nav) New()
    {
        var query = new ControllableHistoryQueryService();
        var metrics = new FakeServerMetricsStore();
        var nav = new FakeNavigationService();
        var vm = new HistoryViewModel(
            query,
            metrics,
            new ServerMonitoringStateStore(),
            nav,
            new FakeLocalizationService(),
            NullLogger<HistoryViewModel>.Instance,
            new FakeTimeProvider());
        return (vm, query, metrics, nav);
    }

    [Fact]
    public async Task LateSupersededResponse_DoesNotOverwriteNewerRange()
    {
        // §80: 30d slow → user picks 1h → 1h finishes → the late 30d response must NOT win.
        var (vm, query, _, _) = New();

        var slow30d = vm.LoadRangeAsync(HistoryTimeRange.Last30Days);
        var fast1h = vm.LoadRangeAsync(HistoryTimeRange.LastHour);

        query.Complete(HistoryTimeRange.LastHour, Result(HistoryTimeRange.LastHour, empty: false));
        await fast1h;
        Assert.Equal(TimeSpan.FromHours(1), vm.RangeEndUtc - vm.RangeStartUtc);

        // The stale 30d reply arrives late; it must be discarded by the generation guard.
        query.Complete(HistoryTimeRange.Last30Days, Result(HistoryTimeRange.Last30Days, empty: false));
        await slow30d;

        Assert.Equal(TimeSpan.FromHours(1), vm.RangeEndUtc - vm.RangeStartUtc);
        Assert.True(vm.ShowCharts);
    }

    [Fact]
    public async Task UnavailableStore_ShowsUnavailable()
    {
        var (vm, query, _, _) = New();
        query.Available = false;

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.True(vm.IsUnavailable);
        Assert.True(vm.ShowUnavailable);
        Assert.False(vm.ShowCharts);
        Assert.False(vm.ShowLoading);
    }

    [Fact]
    public async Task QueryThrows_ShowsUnavailable()
    {
        var (vm, query, _, _) = New();
        query.ThrowOnQuery = true;

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.True(vm.IsUnavailable);
        Assert.False(vm.ShowLoading);
    }

    [Fact]
    public async Task EmptyResult_ShowsEmptyState()
    {
        var (vm, query, _, _) = New();
        query.Immediate = range => Result(range, empty: true);

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.True(vm.IsEmpty);
        Assert.True(vm.ShowEmpty);
        Assert.False(vm.ShowCharts);
    }

    [Fact]
    public async Task DataResult_ShowsCharts()
    {
        var (vm, query, _, _) = New();
        query.Immediate = range => Result(range, empty: false);

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.False(vm.IsEmpty);
        Assert.True(vm.ShowCharts);
        Assert.NotNull(vm.CpuSeries);
    }

    [Fact]
    public async Task OfflineRange_ShowsNotice_AndUsesWordsInAccessibleSummary()
    {
        var (vm, query, _, _) = New();
        query.Immediate = range => Result(range, empty: false) with { ContainsOfflineSamples = true };

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.True(vm.ShowOfflineNotice);
        Assert.Contains("Unknown", vm.CpuSummary, StringComparison.Ordinal);
        Assert.Contains("offline period shown as a gap", vm.CpuSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullyOfflineRange_IsHistory_NotEmptyState()
    {
        var (vm, query, _, _) = New();
        query.Immediate = range =>
        {
            var result = Result(range, empty: true);
            return result with { ContainsOfflineSamples = true };
        };

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.False(vm.IsEmpty);
        Assert.False(vm.ShowEmpty);
        Assert.True(vm.ShowCharts);
        Assert.True(vm.ShowOfflineNotice);
    }

    [Fact]
    public async Task CurrentValue_ComesFromLiveMetrics_NotHistory()
    {
        var (vm, query, metrics, _) = New();
        metrics.InitialSnapshot = new ServerMetricsSnapshot
        {
            ServerId = Guid.NewGuid(),
            CollectedAt = DateTimeOffset.UtcNow,
            CpuUsagePercent = 33
        };
        query.Immediate = range => Result(range, empty: false);

        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal("33%", vm.CpuCurrentDisplay);
    }

    [Fact]
    public void BackCommand_NavigatesToDashboard()
    {
        var (vm, _, _, nav) = New();

        vm.BackCommand.Execute(null);

        Assert.Equal(1, nav.DashboardCount);
    }

    [Fact]
    public async Task Dispose_InvalidatesNonCooperativeInFlightQuery()
    {
        var (vm, query, _, _) = New();
        var pending = vm.LoadRangeAsync(HistoryTimeRange.Last30Days);

        vm.Dispose();
        query.Complete(HistoryTimeRange.Last30Days, Result(HistoryTimeRange.Last30Days, empty: false));
        await pending;

        Assert.Null(vm.CpuSeries);
        Assert.Equal(default, vm.RangeStartUtc);
        Assert.False(vm.IsLoading);
    }
}
