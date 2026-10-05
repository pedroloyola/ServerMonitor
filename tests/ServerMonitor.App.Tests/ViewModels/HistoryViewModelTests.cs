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

        public List<Guid> RequestedIds { get; } = [];
        public bool Available { get; set; } = true;

        public bool ThrowOnQuery { get; set; }

        public Func<HistoryTimeRange, ServerHistoryResult>? Immediate { get; set; }

        public bool IsAvailable => Available;

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken)
        {
            RequestedIds.Add(serverId);
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

    [Fact]
    public async Task Ui6_NoServer_NeverQueriesEmptyGuid_EvenAfterRangeChange()
    {
        var (vm, query, _, _) = New();
        using (vm)
        {
            query.ThrowOnQuery = true;
            vm.Load(null, string.Empty, fromDetail: false);
            await vm.LoadRangeAsync(HistoryTimeRange.Last30Days);
            Assert.False(vm.HasServer);
            Assert.False(vm.ShowDetailBack);
            Assert.True(vm.ShowEmpty);
            Assert.Equal(HistoryEmptyKind.NoServer, vm.EmptyKind);
            Assert.False(vm.IsUnavailable);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ui6_SidebarHistory_UsesLastVisibleDetailOtherwiseCreatedOrder(bool rememberLast)
    {
        var fleet = new Ui4TestKit.Fleet().Add("first", ServerMonitor.Core.Enums.ServerHealth.Healthy)
            .Add("second", ServerMonitor.Core.Enums.ServerHealth.Healthy)
            .Add("hidden", ServerMonitor.Core.Enums.ServerHealth.Healthy, hidden: true);
        var kit = Ui4TestKit.Create(fleet);
        using var dashboard = kit.Dashboard;
        var query = new ControllableHistoryQueryService { Immediate = range => Result(range, empty: true) };
        using var vm = new HistoryViewModel(query, kit.Metrics, kit.States, kit.Servers,
            kit.Navigation, kit.Localization, NullLogger<HistoryViewModel>.Instance, new FakeTimeProvider());
        await vm.LoadSidebarAsync(rememberLast ? fleet.IdOf("second") : fleet.IdOf("hidden"));
        Assert.Equal(rememberLast ? fleet.IdOf("second") : fleet.IdOf("first"), vm.SelectedServer!.Id);
        Assert.False(vm.ShowDetailBack);
        Assert.DoesNotContain(Guid.Empty, query.RequestedIds);
        Assert.NotEmpty(query.RequestedIds);
        vm.Load(fleet.IdOf("first"), "first", fromDetail: true);
        Assert.True(vm.ShowDetailBack);
    }

    [Fact]
    public async Task Ui6_SidebarListFailure_ShowsUnavailable_NotNoServers()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Servers.GetAllOverride = _ => throw new InvalidOperationException("synthetic");
        var query = new ControllableHistoryQueryService();
        using var vm = new HistoryViewModel(query, kit.Metrics, kit.States, kit.Servers,
            kit.Navigation, kit.Localization, NullLogger<HistoryViewModel>.Instance, new FakeTimeProvider());
        await vm.LoadSidebarAsync(null);
        Assert.True(vm.ShowUnavailable);
        Assert.False(vm.ShowEmpty);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasServer);
        Assert.False(vm.ShowDetailBack);
        Assert.Empty(query.RequestedIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ui6_SupersededSidebarRead_DoesNotOverwriteNewerPresentation(bool oldFails)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("current", ServerMonitor.Core.Enums.ServerHealth.Healthy));
        using var dashboard = kit.Dashboard;
        var pending = new TaskCompletionSource<IReadOnlyList<Server>>();
        kit.Servers.GetAllOverride = _ => pending.Task;
        var query = new ControllableHistoryQueryService { Immediate = range => Result(range, empty: true) };
        using var vm = new HistoryViewModel(query, kit.Metrics, kit.States, kit.Servers,
            kit.Navigation, kit.Localization, NullLogger<HistoryViewModel>.Instance, new FakeTimeProvider());
        var old = vm.LoadSidebarAsync(null);
        kit.Servers.GetAllOverride = null;
        await vm.LoadSidebarAsync(null);
        var selected = vm.SelectedServer;
        var requests = query.RequestedIds.Count;
        if (oldFails) pending.SetException(new IOException("superseded")); else pending.SetResult([]);
        await old;
        Assert.Same(selected, vm.SelectedServer);
        Assert.True(vm.HasServer);
        Assert.False(vm.IsUnavailable);
        Assert.Equal(requests, query.RequestedIds.Count);
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

    private static (HistoryViewModel vm, ControllableHistoryQueryService query, FakeServerMetricsStore metrics, FakeNavigationService nav) New(bool loadServer = true)
    {
        var query = new ControllableHistoryQueryService();
        var metrics = new FakeServerMetricsStore();
        var nav = new FakeNavigationService();
        var vm = new HistoryViewModel(
            query,
            metrics,
            new ServerMonitoringStateStore(),
            new FakeServerService(),
            nav,
            new FakeLocalizationService(),
            NullLogger<HistoryViewModel>.Instance,
            new FakeTimeProvider());
        if (loadServer)
        {
            query.Immediate = range => Result(range, empty: true);
            vm.Load(Guid.Parse("d8c9b70e-1c06-4010-9857-991b9f60eab1"), "test");
            query.Immediate = null;
        }
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
    public void BackCommand_ReturnsToTheServersInterimPage()
    {
        var (vm, _, _, nav) = New(loadServer: false);

        vm.BackCommand.Execute(null);

        // UI.4 (Boss, Beacon r1 SHOULD-4): back leads to the interim page of the shown server (none loaded here).
        Assert.Equal([Guid.Empty], nav.ServerDetailReturns);
        Assert.Equal(0, nav.DashboardCount);
    }

    [Fact]
    public async Task Dispose_InvalidatesNonCooperativeInFlightQuery()
    {
        var (vm, query, _, _) = New();
        var previousSeries = vm.CpuSeries;
        var previousStart = vm.RangeStartUtc;
        var pending = vm.LoadRangeAsync(HistoryTimeRange.Last30Days);

        vm.Dispose();
        query.Complete(HistoryTimeRange.Last30Days, Result(HistoryTimeRange.Last30Days, empty: false));
        await pending;

        Assert.Same(previousSeries, vm.CpuSeries);
        Assert.Equal(previousStart, vm.RangeStartUtc);
        Assert.False(vm.IsLoading);
    }
}
