using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Services;

public sealed class RefreshAllCoordinatorTests
{
    [Fact]
    public async Task RefreshAll_IncludesVisibleAndHiddenConfiguredServers()
    {
        var servers = new FakeServerService();
        var visible = TestData.LinuxServer();
        var hidden = TestData.LinuxServer() with { Id = Guid.NewGuid(), IsHidden = true };
        servers.Servers.AddRange([visible, hidden]);
        var engine = new RecordingMonitoringEngine();
        using var coordinator = Create(servers, engine);

        var result = await coordinator.RefreshAllAsync();

        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(new[] { visible.Id, hidden.Id }.Order(), engine.Requests.Order());
    }

    [Fact]
    public async Task ConcurrentRefreshAllRequests_CoalesceIntoOneBatch()
    {
        var servers = new FakeServerService();
        var server = TestData.LinuxServer();
        servers.Servers.Add(server);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new RecordingMonitoringEngine { BeforeResult = _ => release.Task };
        using var coordinator = Create(servers, engine);

        var first = coordinator.RefreshAllAsync();
        var second = coordinator.RefreshAllAsync();
        await engine.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();

        Assert.Equal(await first, await second);
        Assert.Single(engine.Requests);
    }

    [Fact]
    public async Task OneServerFailure_DoesNotCancelRemainingServers()
    {
        var servers = new FakeServerService();
        var failing = TestData.LinuxServer();
        var successful = TestData.LinuxServer() with { Id = Guid.NewGuid() };
        servers.Servers.AddRange([failing, successful]);
        var engine = new RecordingMonitoringEngine
        {
            Result = id => id == failing.Id
                ? ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected)
                : TestData.Success(TestData.Snapshot(id))
        };
        using var coordinator = Create(servers, engine);

        var result = await coordinator.RefreshAllAsync();

        Assert.Equal(2, result.Requested);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, engine.Requests.Count);
    }

    [Fact]
    public async Task StopDuringBatch_CancelsAndDrainsIt()
    {
        var servers = new FakeServerService();
        servers.Servers.Add(TestData.LinuxServer());
        var engine = new RecordingMonitoringEngine
        {
            BeforeResult = token => Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        using var coordinator = Create(servers, engine);

        var refresh = coordinator.RefreshAllAsync();
        await engine.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RefreshAllAsync());
    }

    [Fact]
    public async Task ExternalStopTimeout_DoesNotClaimNonCooperativeBatchWasDrainedOrDisposeItsToken()
    {
        var servers = new FakeServerService();
        servers.Servers.Add(TestData.LinuxServer());
        var enumerationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnumeration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        servers.GetAllOverride = async _ =>
        {
            enumerationEntered.TrySetResult();
            await releaseEnumeration.Task;
            return servers.Servers.ToList();
        };
        var engine = new RecordingMonitoringEngine();
        var coordinator = Create(servers, engine);
        var refresh = coordinator.RefreshAllAsync();
        await enumerationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.StopAsync(timeout.Token));
        coordinator.Dispose();
        releaseEnumeration.TrySetResult();

        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public async Task ManyServers_UsesRealMonitoringEngineGlobalConcurrencyLimit()
    {
        const int concurrencyLimit = 2;
        var servers = new FakeServerService();
        var template = TestData.LinuxServer();
        servers.Servers.AddRange(Enumerable.Range(0, 8).Select(_ => template with { Id = Guid.NewGuid() }));
        var metrics = new ConcurrencyTrackingMetricsStore(concurrencyLimit);
        var states = new ServerMonitoringStateStore();
        var engine = new MonitoringEngine(
            servers,
            metrics,
            states,
            NullLogger<MonitoringEngine>.Instance,
            new FakeTimeProvider(),
            new MonitoringOptions
            {
                MaxConcurrentCollections = concurrencyLimit,
                InitialDelay = TimeSpan.FromHours(1),
                StartupStagger = TimeSpan.Zero,
                AttentionInterval = TimeSpan.FromHours(1),
                RetryDelays = []
            });
        await using var engineLifetime = engine;
        await engine.StartMonitoringAsync();
        using var coordinator = Create(servers, engine);

        var refresh = coordinator.RefreshAllAsync();
        await metrics.LimitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(concurrencyLimit, metrics.MaxConcurrent);
        metrics.Release.TrySetResult();

        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, result.Succeeded);
        Assert.Equal(concurrencyLimit, metrics.MaxConcurrent);
    }

    private static RefreshAllCoordinator Create(
        FakeServerService servers,
        IMonitoringEngine engine) => new(
            servers,
            engine,
            NullLogger<RefreshAllCoordinator>.Instance);

    private sealed class RecordingMonitoringEngine : IMonitoringEngine
    {
        private readonly object _sync = new();

        public List<Guid> Requests { get; } = [];

        public TaskCompletionSource RequestObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<CancellationToken, Task>? BeforeResult { get; init; }

        public Func<Guid, ServerMetricsCollectionResult>? Result { get; init; }

        public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<ServerMetricsCollectionResult> RefreshNowAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Requests.Add(serverId);
            }

            RequestObserved.TrySetResult();
            if (BeforeResult is not null)
            {
                await BeforeResult(cancellationToken);
            }

            return Result?.Invoke(serverId) ?? TestData.Success(TestData.Snapshot(serverId));
        }
    }

    private sealed class ConcurrencyTrackingMetricsStore(int expectedConcurrent) : IServerMetricsStore
    {
        private int _active;
        private int _maxConcurrent;

        public TaskCompletionSource LimitReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => null;

        public async Task<ServerMetricsCollectionResult> RefreshAsync(
            Server server,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            if (active == expectedConcurrent)
            {
                LimitReached.TrySetResult();
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return TestData.Success(TestData.Snapshot(server.Id));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public void Remove(Guid serverId) { }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maxConcurrent);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _maxConcurrent, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
