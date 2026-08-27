using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.Services;

public sealed class WorkloadCollectorServiceTests
{
    private static DockerSnapshot AvailableDocker(params string[] names) => new()
    {
        Availability = DockerAvailability.Available,
        Containers = names.Select(n => new ContainerInfo
        {
            ContainerId = n,
            Name = n,
            Image = "img",
            State = ContainerState.Running,
            StatusText = "Up",
            Health = ContainerHealth.None
        }).ToArray()
    };

    private static ServerWorkloadSnapshot Fresh(Guid id, DockerSnapshot docker, DateTimeOffset at) => new()
    {
        ServerId = id,
        CapturedAtUtc = at,
        Docker = docker,
        Services = new ServiceSnapshot { Manager = ServiceManager.Systemd, Availability = WorkloadServiceAvailability.Available }
    };

    /// <summary>Configurable collector: optional gate to hold a collection in flight, plus a scripted result.</summary>
    private sealed class FakeWorkloadCollector : IWorkloadCollector
    {
        private int _callCount;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Gated { get; init; }

        public Func<Server, int, ServerWorkloadSnapshot> ResultFactory { get; init; } =
            (server, _) => Fresh(server.Id, AvailableDocker("c1"), new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero));

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>The token passed to the most recent collection — used to prove it is cancellable (not None).</summary>
        public CancellationToken LastToken { get; private set; }

        public async Task<ServerWorkloadSnapshot> CollectAsync(Server server, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            LastToken = cancellationToken;
            Entered.TrySetResult();
            if (Gated)
            {
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return ResultFactory(server, index);
        }
    }

    private static (
        WorkloadCollectorService service,
        FakeServerService servers,
        InMemoryServerWorkloadStore store,
        WorkloadRequestQueue queue)
        New(IWorkloadCollector collector, Server server, FakeTimeProvider? time = null)
    {
        var servers = new FakeServerService();
        servers.Servers.Add(server);
        var store = new InMemoryServerWorkloadStore();
        var queue = new WorkloadRequestQueue();
        var service = new WorkloadCollectorService(
            queue,
            collector,
            store,
            servers,
            NullLogger<WorkloadCollectorService>.Instance,
            WorkloadOptions.Default,
            time ?? new FakeTimeProvider());
        return (service, servers, store, queue);
    }

    [Fact]
    public async Task SingleFlight_ConcurrentRequests_CoalesceIntoOneCollection()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector { Gated = true };
        var (service, _, store, _) = New(collector, server);
        await service.StartAsync(default);
        try
        {
            var first = service.RefreshNowAsync(server.Id);
            // Collection #1 is in flight and parked at the gate; a second manual request must join it.
            await collector.Entered.Task;
            var second = service.RefreshNowAsync(server.Id);

            collector.Release.TrySetResult();
            await Task.WhenAll(first, second);

            Assert.Equal(1, collector.CallCount);        // single-flight: both requests, one collection
            var stored = store.Get(server.Id);
            Assert.NotNull(stored);
            Assert.Equal(DockerAvailability.Available, stored!.Docker.Availability);
            Assert.False(stored.IsStale);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task SingleFlight_ScheduledAndManualOverlap_CoalesceIntoOneCollection()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector { Gated = true };
        var (service, _, store, queue) = New(collector, server);
        await service.StartAsync(default);
        try
        {
            Assert.True(queue.TryEnqueueScheduled(new WorkloadRequest
            {
                ServerId = server.Id,
                Reason = WorkloadRefreshReason.Scheduled
            }));
            await collector.Entered.Task;

            var manual = service.RefreshNowAsync(server.Id);
            collector.Release.TrySetResult();
            await manual;

            Assert.Equal(1, collector.CallCount);
            Assert.NotNull(store.Get(server.Id));
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task ManualRefresh_Success_StoresFreshSnapshot()
    {
        var server = TestData.LinuxServer();
        var time = new FakeTimeProvider();
        var collector = new FakeWorkloadCollector();
        var (service, _, store, _) = New(collector, server, time);
        await service.StartAsync(default);
        try
        {
            await service.RefreshNowAsync(server.Id);

            var stored = store.Get(server.Id);
            Assert.NotNull(stored);
            Assert.Single(stored!.Docker.Containers);
            Assert.Equal(time.GetUtcNow(), stored.CapturedAtUtc);
            Assert.False(stored.IsStale);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task HiddenServer_RemainsEligibleForCollection()
    {
        // Hidden is a presentation state, not a monitoring opt-out (ADR-011): workload collection
        // must continue so restoring the server can show a current snapshot.
        var server = TestData.LinuxServer() with { IsHidden = true };
        var collector = new FakeWorkloadCollector();
        var (service, _, store, _) = New(collector, server);
        await service.StartAsync(default);
        try
        {
            await service.RefreshNowAsync(server.Id);

            Assert.Equal(1, collector.CallCount);
            Assert.NotNull(store.Get(server.Id));
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task FailedAttemptAfterSuccess_CarriesOverPrevious_MarksStale()
    {
        var server = TestData.LinuxServer();
        var time = new FakeTimeProvider();
        var collector = new FakeWorkloadCollector
        {
            // Call 0: Available with a container. Call 1: total failure (both parts Unknown).
            ResultFactory = (srv, index) => index == 0
                ? Fresh(srv.Id, AvailableDocker("c1"), time.GetUtcNow())
                : new ServerWorkloadSnapshot
                {
                    ServerId = srv.Id,
                    CapturedAtUtc = time.GetUtcNow(),
                    Docker = DockerSnapshot.Unknown,
                    Services = ServiceSnapshot.Unknown
                }
        };
        var (service, _, store, _) = New(collector, server, time);
        await service.StartAsync(default);
        try
        {
            await service.RefreshNowAsync(server.Id);
            var firstCapture = store.Get(server.Id)!.CapturedAtUtc;

            time.Advance(TimeSpan.FromMinutes(1));
            await service.RefreshNowAsync(server.Id);

            var stored = store.Get(server.Id);
            Assert.NotNull(stored);
            Assert.True(stored!.IsStale);                       // failed attempt → carried over
            Assert.Single(stored.Docker.Containers);            // previous list preserved, not zeroed
            Assert.Equal(DockerAvailability.Available, stored.Docker.Availability);
            Assert.Equal(firstCapture, stored.CapturedAtUtc);   // capture time never moved forward
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task CollectionFailure_IsIsolatedPerServer()
    {
        var failing = TestData.LinuxServer() with { Id = Guid.Parse("10000000-0000-0000-0000-000000000001") };
        var healthy = TestData.LinuxServer() with { Id = Guid.Parse("10000000-0000-0000-0000-000000000002") };
        var time = new FakeTimeProvider();
        var collector = new FakeWorkloadCollector
        {
            ResultFactory = (server, _) => server.Id == failing.Id
                ? throw new InvalidOperationException("one server failed")
                : Fresh(server.Id, AvailableDocker("healthy"), time.GetUtcNow())
        };
        var (service, servers, store, _) = New(collector, failing, time);
        servers.Servers.Add(healthy);
        await service.StartAsync(default);
        try
        {
            await Task.WhenAll(
                service.RefreshNowAsync(failing.Id),
                service.RefreshNowAsync(healthy.Id));

            Assert.Null(store.Get(failing.Id));
            Assert.Equal("healthy", Assert.Single(store.Get(healthy.Id)!.Docker.Containers).Name);
            Assert.Equal(2, collector.CallCount);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task RefreshNow_UnknownServer_CompletesWithoutHanging()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector();
        var (service, _, store, _) = New(collector, server);
        await service.StartAsync(default);
        try
        {
            // A server id that is not configured must not hang the caller.
            await service.RefreshNowAsync(Guid.NewGuid());
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task ServerRemoval_AfterKnownSetWasReconciled_ClearsTransientSnapshot()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector();
        var (service, servers, store, _) = New(collector, server);
        store.Set(Fresh(server.Id, AvailableDocker("old"), DateTimeOffset.UnixEpoch));
        await service.StartAsync(default);
        try
        {
            // Both fake GetAll calls complete synchronously, so each async-void event handler finishes
            // before RaiseChanged returns; no polling or scheduler timing is involved.
            servers.RaiseChanged();
            servers.Servers.Clear();
            servers.RaiseChanged();

            Assert.Null(store.Get(server.Id));
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task Shutdown_CancelsInFlightCollection_AndCompletesManualWaiterWithoutObjectDisposedException()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector { Gated = true };
        var (service, _, _, _) = New(collector, server);
        await service.StartAsync(default);

        var refresh = service.RefreshNowAsync(server.Id);
        await collector.Entered.Task;

        var exception = await Record.ExceptionAsync(() => service.StopAsync(default));
        await refresh;

        Assert.Null(exception);
        Assert.Equal(1, collector.CallCount);
    }

    [Fact]
    public async Task AfterStop_RefreshIsNoOp()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector();
        var (service, _, _, _) = New(collector, server);
        await service.StartAsync(default);
        await service.StopAsync(default);

        await service.RefreshNowAsync(server.Id);

        Assert.Equal(0, collector.CallCount);
    }

    [Fact]
    public async Task ColdStart_CollectThenRemove_ClearsSnapshotOnFirstChange()
    {
        // M-01: the very first ServersChanged after startup — with no prior reconcile to warm any known
        // set — must still drop the orphaned snapshot of a server that was collected then removed.
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector();
        var (service, servers, store, _) = New(collector, server);
        await service.StartAsync(default);
        try
        {
            await service.RefreshNowAsync(server.Id); // cold-start collection populates the store
            Assert.NotNull(store.Get(server.Id));

            // First and only reconcile: the server is gone. FakeServerService.GetAllAsync completes
            // synchronously, so the async-void handler finishes before RaiseChanged returns — no timing.
            servers.Servers.Clear();
            servers.RaiseChanged();

            Assert.Null(store.Get(server.Id));
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task Shutdown_InFlightCollection_UsesCancellableTokenThatStopCancels()
    {
        // H-02: the in-flight collection must run under the real (cancellable) engine token, not a
        // fallback CancellationToken.None, and shutdown must cancel and drain it before disposing the CTS.
        var server = TestData.LinuxServer();
        var collector = new FakeWorkloadCollector { Gated = true };
        var (service, _, _, _) = New(collector, server);
        await service.StartAsync(default);

        var refresh = service.RefreshNowAsync(server.Id);
        await collector.Entered.Task;

        Assert.True(collector.LastToken.CanBeCanceled); // a real token, never CancellationToken.None

        // Stop cancels the token (unblocking the gated wait) and drains the task before disposing the CTS;
        // no ObjectDisposedException, and the manual waiter completes.
        var exception = await Record.ExceptionAsync(() => service.StopAsync(default));
        await refresh;

        Assert.Null(exception);
        Assert.True(collector.LastToken.IsCancellationRequested);
    }
}
