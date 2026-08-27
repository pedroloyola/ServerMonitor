using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class WorkloadsViewModelReliabilityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Load_large_500_and_2000_datasets_projects_every_bounded_row()
    {
        var serverId = Guid.NewGuid();
        var store = new InMemoryServerWorkloadStore();
        store.Set(Snapshot(serverId, containerCount: 500, serviceCount: 2000));
        using var viewModel = New(store);

        viewModel.Load(serverId, "large");

        Assert.Equal(DockerViewState.Containers, viewModel.DockerState);
        Assert.Equal(ServicesViewState.List, viewModel.ServicesState);
        Assert.Equal(500, viewModel.Containers.Count);
        Assert.Equal(2000, viewModel.Services.Count);
        Assert.False(viewModel.ShowDockerTruncatedNotice);
        Assert.False(viewModel.ShowServicesTruncatedNotice);
    }

    [Fact]
    public void Late_store_event_for_previous_server_does_not_replace_current_generation()
    {
        var previous = Guid.NewGuid();
        var current = Guid.NewGuid();
        var store = new InMemoryServerWorkloadStore();
        store.Set(Snapshot(previous, containerCount: 1, serviceCount: 1, namePrefix: "old"));
        store.Set(Snapshot(current, containerCount: 1, serviceCount: 1, namePrefix: "current"));
        using var viewModel = New(store);

        viewModel.Load(previous, "previous");
        viewModel.Load(current, "current");
        store.Set(Snapshot(previous, containerCount: 2, serviceCount: 2, namePrefix: "late"));

        Assert.Equal("current", viewModel.Title);
        Assert.Equal("current-container-0000", Assert.Single(viewModel.Containers).Name);
        Assert.Equal("current-service-0000", Assert.Single(viewModel.Services).Name);
    }

    [Fact]
    public async Task InFlight_refresh_from_previous_generation_cannot_restore_spinner()
    {
        var previous = Guid.NewGuid();
        var current = Guid.NewGuid();
        var store = new InMemoryServerWorkloadStore();
        var coordinator = new GatedRefreshCoordinator();
        using var viewModel = new WorkloadsViewModel(
            store, coordinator, new FakeNavigationService(), new FakeLocalizationService(),
            NullLogger<WorkloadsViewModel>.Instance, new FakeTimeProvider(Now));

        viewModel.Load(previous, "previous");
        viewModel.RefreshCommand.Execute(null);
        await coordinator.Entered.Task;
        Assert.True(viewModel.IsRefreshing);

        viewModel.Load(current, "current");
        Assert.False(viewModel.IsRefreshing);

        coordinator.Release.TrySetResult();
        await coordinator.Completed.Task;
        Assert.False(viewModel.IsRefreshing);
        Assert.Equal("current", viewModel.Title);
    }

    private static WorkloadsViewModel New(InMemoryServerWorkloadStore store) => new(
        store,
        new NoOpWorkloadRefreshCoordinator(),
        new FakeNavigationService(),
        new FakeLocalizationService(),
        NullLogger<WorkloadsViewModel>.Instance,
        new FakeTimeProvider(Now));

    private static ServerWorkloadSnapshot Snapshot(
        Guid serverId,
        int containerCount,
        int serviceCount,
        string namePrefix = "item") => new()
    {
        ServerId = serverId,
        CapturedAtUtc = Now,
        LastAttemptAtUtc = Now,
        Docker = new DockerSnapshot
        {
            Availability = DockerAvailability.Available,
            Containers = Enumerable.Range(0, containerCount)
                .Select(i => new ContainerInfo
                {
                    ContainerId = $"{i:x12}",
                    Name = $"{namePrefix}-container-{i:D4}",
                    Image = "example/image",
                    State = ContainerState.Running,
                    StatusText = "Up",
                    Health = ContainerHealth.None
                })
                .ToArray()
        },
        Services = new ServiceSnapshot
        {
            Manager = ServiceManager.Systemd,
            Availability = WorkloadServiceAvailability.Available,
            Services = Enumerable.Range(0, serviceCount)
                .Select(i => new ServiceInfo
                {
                    Id = $"{namePrefix}-service-{i:D4}.service",
                    Name = $"{namePrefix}-service-{i:D4}",
                    State = ServiceState.Running
                })
                .ToArray()
        }
    };

    private sealed class NoOpWorkloadRefreshCoordinator : IWorkloadRefreshCoordinator
    {
        public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class GatedRefreshCoordinator : IWorkloadRefreshCoordinator
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task;
            }
            finally
            {
                Completed.TrySetResult();
            }
        }
    }
}
