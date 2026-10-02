using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.3 phase 2 view-model additions the new pages bind to.</summary>
public sealed class Ui3Phase2ViewModelTests
{
    private sealed class GatedQuery : IServerHistoryQueryService
    {
        public TaskCompletionSource<ServerHistoryResult> Gate { get; } = new();

        public bool IsAvailable => true;

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken) => Gate.Task;
    }

    [Fact]
    public void SelectorShowsTheLoadingLineOnTheSelectedItem_AndRestoresItAfterwards()
    {
        var servers = new FakeServerService();
        var server = new Server { Id = Guid.NewGuid(), Name = "web-01", Host = "198.51.100.1" };
        servers.Servers.Add(server);
        var query = new GatedQuery();
        var vm = new HistoryViewModel(query, new FakeServerMetricsStore(), new ServerMonitoringStateStore(), servers,
            new FakeNavigationService(), new ResWLocalizationService("pt-PT"), NullLogger<HistoryViewModel>.Instance, new FakeTimeProvider());

        vm.Load(server.Id, server.Name);

        var option = Assert.Single(vm.Servers);
        Assert.Same(option, vm.SelectedServer);
        Assert.Equal("A carregar histórico…", option.Subtitle);   // Figma 112:16322: the closed selector renders the item
        Assert.Equal(string.Empty, option.BaseSubtitle);

        query.Gate.SetResult(ServerHistoryResult.Empty(server.Id, HistoryTimeRange.Last24Hours, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1)));

        Assert.False(vm.IsLoading);
        Assert.Equal(string.Empty, option.Subtitle);
        Assert.False(option.HasSubtitle);
    }

    [Fact]
    public void ServerOption_TransientLine_RaisesChangeAndNeverReplacesTheData()
    {
        var option = new HistoryServerOptionViewModel(Guid.NewGuid(), "db-02", "Ubuntu 24.04 · Ligado");
        var raised = new List<string?>();
        option.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        option.SetTransientSubtitle("A carregar histórico…");
        Assert.Equal("A carregar histórico…", option.Subtitle);
        Assert.Contains(nameof(HistoryServerOptionViewModel.Subtitle), raised);
        Assert.Equal("db-02, A carregar histórico…", option.AutomationName);

        option.SetTransientSubtitle(null);
        Assert.Equal("Ubuntu 24.04 · Ligado", option.Subtitle);
        Assert.Equal("Ubuntu 24.04 · Ligado", option.BaseSubtitle);
    }

    [Theory]
    [InlineData("loading", true, false)]
    [InlineData("unavailable", false, false)]
    [InlineData("nothing", false, false)]
    [InlineData("list", false, true)]
    public void WorkloadsPagePanels_AreMutuallyExclusiveWithTheCards(string shape, bool loading, bool cards)
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryServerWorkloadStore();
        if (shape != "loading")
        {
            store.Set(new ServerWorkloadSnapshot
            {
                ServerId = id,
                CapturedAtUtc = now,
                LastAttemptAtUtc = now,
                Docker = shape switch
                {
                    "unavailable" => DockerSnapshot.Unknown,
                    "nothing" => new DockerSnapshot { Availability = DockerAvailability.Available },
                    _ => new DockerSnapshot
                    {
                        Availability = DockerAvailability.Available,
                        Containers = [new ContainerInfo { ContainerId = "a", Name = "a", Image = "i", State = ContainerState.Running, StatusText = "Up", Health = ContainerHealth.None }]
                    }
                },
                Services = shape == "unavailable"
                    ? new ServiceSnapshot { Manager = ServiceManager.Systemd, Availability = WorkloadServiceAvailability.Unknown }
                    : new ServiceSnapshot { Manager = ServiceManager.Systemd, Availability = WorkloadServiceAvailability.Available }
            });
        }

        using var vm = new WorkloadsViewModel(store, new NoOp(), new FakeServerMetricsStore(), new FakeNavigationService(),
            new FakeLocalizationService(), NullLogger<WorkloadsViewModel>.Instance, new FakeTimeProvider(now));
        vm.Load(id, "web-01");

        Assert.Equal(loading, vm.ShowPageLoading);
        Assert.Equal(cards, vm.ShowSectionCards);
        var panels = new[] { vm.ShowPageLoading, vm.ShowAllUnavailable, vm.ShowNothingToShow, vm.ShowGlobalNoResults, vm.ShowSectionCards };
        Assert.Equal(1, panels.Count(p => p));
    }

    [Fact]
    public void GlobalNoResults_ReplacesTheCards()
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryServerWorkloadStore();
        store.Set(new ServerWorkloadSnapshot
        {
            ServerId = id,
            CapturedAtUtc = now,
            LastAttemptAtUtc = now,
            Docker = new DockerSnapshot
            {
                Availability = DockerAvailability.Available,
                Containers = [new ContainerInfo { ContainerId = "a", Name = "nginx", Image = "nginx", State = ContainerState.Running, StatusText = "Up", Health = ContainerHealth.None }]
            },
            Services = new ServiceSnapshot { Manager = ServiceManager.Systemd, Availability = WorkloadServiceAvailability.Available }
        });
        using var vm = new WorkloadsViewModel(store, new NoOp(), new FakeServerMetricsStore(), new FakeNavigationService(),
            new FakeLocalizationService(), NullLogger<WorkloadsViewModel>.Instance, new FakeTimeProvider(now));
        vm.Load(id, "web-01");

        vm.SearchText = "zzz";
        Assert.True(vm.ShowGlobalNoResults);
        Assert.False(vm.ShowSectionCards);

        vm.ClearSearchCommand.Execute(null);
        Assert.True(vm.ShowSectionCards);
    }

    private sealed class NoOp : IWorkloadRefreshCoordinator
    {
        public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
