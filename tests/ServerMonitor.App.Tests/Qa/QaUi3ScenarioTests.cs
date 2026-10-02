using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.3 phase 2 QA scenarios (Debug-only, like the harness; excluded from Release with the rest of tests/Qa). They are
/// selected by server - the History page's own server selector switches between them, so no new launch flag or
/// modifier exists (the strict harness parser and Start-QaApp are unchanged). Every scenario is synthetic: .local hosts,
/// in-memory data, no path, credential or network.
/// </summary>
public sealed class QaUi3ScenarioTests
{
    private static QaHistoryScenario History(QaHistoryKind kind) => QaHistoryCatalog.Scenarios.Single(s => s.Kind == kind);

    private static QaWorkloadScenario Workload(string label) => QaWorkloadsCatalog.Scenarios.Single(s => s.Label == label);

    [Fact]
    public void EmptyPeriod_IsEmptyForShortRanges_AndHasDataIn30Days()
    {
        var scenario = History(QaHistoryKind.EmptyPeriod);
        var end = QaHistoryCatalog.Now;

        Assert.Empty(QaHistoryCatalog.Generate(scenario, end - HistoryTimeRange.Last24Hours.ToDuration(), end));
        Assert.Empty(QaHistoryCatalog.Generate(scenario, end - HistoryTimeRange.LastHour.ToDuration(), end));
        var month = QaHistoryCatalog.Generate(scenario, end - HistoryTimeRange.Last30Days.ToDuration(), end);
        Assert.NotEmpty(month);
        Assert.All(month, sample => Assert.True(sample.CapturedAtUtc <= end.AddDays(-2)));
    }

    [Fact]
    public async Task EmptyPeriod_DrivesTheRealViewModelToThePeriodState()
    {
        var vm = new HistoryViewModel(
            new QaServerHistoryQueryService(),
            new FakeServerMetricsStore(),
            new ServerMonitoringStateStore(),
            new FakeServerService(),
            new FakeNavigationService(),
            new FakeLocalizationService(),
            NullLogger<HistoryViewModel>.Instance,
            new FakeTimeProvider(QaHistoryCatalog.Now));
        vm.Load(History(QaHistoryKind.EmptyPeriod).Server.Id, "QA");
        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(HistoryEmptyKind.Period, vm.EmptyKind);

        vm.Load(History(QaHistoryKind.Empty).Server.Id, "QA");
        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);
        Assert.Equal(HistoryEmptyKind.NeverRecorded, vm.EmptyKind);
    }

    [Fact]
    public void LoadingScenario_NeverCompletes()
    {
        var task = new QaServerHistoryQueryService().GetHistoryAsync(History(QaHistoryKind.Loading).Server.Id, HistoryTimeRange.Last24Hours);

        Assert.False(task.IsCompleted);
    }

    [Fact]
    public void ExtremeAndSparseScenarios_HaveTheIntendedShape()
    {
        var end = QaHistoryCatalog.Now;
        var start = end.AddHours(-24);

        var extremes = QaHistoryCatalog.Generate(History(QaHistoryKind.Extremes), start, end);
        Assert.Contains(extremes, s => s.CpuPercent == 0);
        Assert.Contains(extremes, s => s.CpuPercent == 100);
        Assert.All(extremes, s => Assert.Equal(100, s.MemoryPercent));
        Assert.All(extremes, s => Assert.Equal(0, s.DiskPercent));

        Assert.Single(QaHistoryCatalog.Generate(History(QaHistoryKind.SinglePoint), start, end));
        Assert.Equal(6, QaHistoryCatalog.Generate(History(QaHistoryKind.FewPoints), start, end).Count);
    }

    [Fact]
    public void WorkloadsFigmaScenario_MatchesTheFrame()
    {
        var store = new InMemoryServerWorkloadStore();
        var scenario = Workload("Figma (problems)");
        store.Set(scenario.Workload);
        using var vm = new WorkloadsViewModel(
            store,
            new NoOpCoordinator(),
            new FakeServerMetricsStore { InitialSnapshot = scenario.Metrics },
            new FakeNavigationService(),
            new FakeLocalizationService(),
            NullLogger<WorkloadsViewModel>.Instance,
            new FakeTimeProvider(QaWorkloadsCatalog.Now));

        vm.Load(scenario.Server.Id, scenario.Server.Name);

        Assert.Equal(12, vm.AllCount);
        Assert.Equal(2, vm.ProblemCount);
        Assert.True(vm.ShowSectionCards);
        Assert.Contains("Ubuntu 24.04 LTS", vm.ContextDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkloadsPageStates_HaveAScenarioEach()
    {
        Assert.Null(Workload("Loading (no attempt yet)").Workload.LastAttemptAtUtc);
        var nothing = Workload("Nothing (both empty)").Workload;
        Assert.Empty(nothing.Docker.Containers);
        Assert.Empty(nothing.Services.Services);
        Assert.All(QaWorkloadsCatalog.Servers, s => Assert.EndsWith(".local", s.Host, StringComparison.Ordinal));
        Assert.All(QaHistoryCatalog.Servers, s => Assert.EndsWith(".local", s.Host, StringComparison.Ordinal));
    }

    private sealed class NoOpCoordinator : IWorkloadRefreshCoordinator
    {
        public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
