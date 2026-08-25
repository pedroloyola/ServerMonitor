using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// Behavioural tests for the monitoring engine. They are deterministic: the scheduling
/// intervals are pushed far into the (fake) future so the automatic timer never fires, and
/// every collection is driven through <see cref="MonitoringEngine.RefreshNowAsync"/>, which
/// wakes the per-server loop via its wait signal rather than the clock. This exercises the
/// real loop, retry policy and state application without depending on wall-clock timing.
/// </summary>
public sealed class MonitoringEngineTests
{
    // A generous real-time safety net so a logic bug fails the test instead of hanging the run.
    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static MonitoringOptions ManualDrive(IReadOnlyList<TimeSpan>? retryDelays = null) => new()
    {
        InitialDelay = TimeSpan.FromHours(1),
        StartupStagger = TimeSpan.Zero,
        AttentionInterval = TimeSpan.FromHours(1),
        RetryDelays = retryDelays ?? [],
    };

    [Fact]
    public async Task RefreshNowAsync_MonitoredServerHealthy_AppliesHealthyState()
    {
        var server = TestData.LinuxServer();
        await using var h = await StartAsync(ManualDrive(), server);

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.True(result.IsSuccess);
        var state = h.State.Get(server.Id);
        Assert.Equal(ServerHealth.Healthy, state.Health);
        Assert.False(state.IsRefreshing);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.NotNull(state.LastSuccessAt);
        Assert.False(state.IsStale);
        Assert.Equal(1, h.Store.CallCount);
    }

    [Fact]
    public async Task RefreshNowAsync_MetricOverCriticalThreshold_AppliesCriticalState()
    {
        var server = TestData.LinuxServer();
        await using var h = await StartAsync(ManualDrive(), server);
        h.Store.ResultFactory = (s, _) => TestData.Success(TestData.Snapshot(s.Id, cpu: 99));

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.True(result.IsSuccess);
        Assert.Equal(ServerHealth.Critical, h.State.Get(server.Id).Health);
    }

    [Fact]
    public async Task RefreshNowAsync_TransientFailurePersists_GoesOfflineAfterRetries()
    {
        var server = TestData.LinuxServer();
        // Two retry delays => three attempts per cycle. ConnectionFailed with no carried
        // SshConnectionResult classifies as transient (Retryable), so all attempts run.
        await using var h = await StartAsync(ManualDrive([TimeSpan.Zero, TimeSpan.Zero]), server);
        h.Store.ResultFactory = (_, _) => TestData.Failure(MetricsCollectionErrorCode.ConnectionFailed);

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, result.ErrorCode);
        var state = h.State.Get(server.Id);
        Assert.Equal(ServerHealth.Offline, state.Health);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(3, h.Store.CallCount);
    }

    [Fact]
    public async Task RefreshNowAsync_TransientThenSuccess_RecoversWithinCycle()
    {
        var server = TestData.LinuxServer();
        await using var h = await StartAsync(ManualDrive([TimeSpan.Zero]), server);
        h.Store.ResultFactory = (s, index) => index == 0
            ? TestData.Failure(MetricsCollectionErrorCode.TimedOut)
            : TestData.Success(TestData.Snapshot(s.Id, cpu: 5));

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.True(result.IsSuccess);
        Assert.Equal(ServerHealth.Healthy, h.State.Get(server.Id).Health);
        Assert.Equal(2, h.Store.CallCount);
    }

    [Fact]
    public async Task RefreshNowAsync_AuthenticationFailure_AppliesUnknownAndDoesNotRetry()
    {
        var server = TestData.LinuxServer();
        // Retry budget available (one delay), but a non-retryable auth failure must break out
        // after the first attempt: the collector is called exactly once.
        await using var h = await StartAsync(ManualDrive([TimeSpan.Zero]), server);
        h.Store.ResultFactory = (_, _) => TestData.Failure(
            MetricsCollectionErrorCode.ConnectionFailed,
            TestData.Connected() with
            {
                State = ServerConnectionState.AuthenticationFailed,
                ErrorCode = SshConnectionErrorCode.AuthenticationFailed
            });

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.False(result.IsSuccess);
        var state = h.State.Get(server.Id);
        Assert.Equal(ServerHealth.Unknown, state.Health);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(1, h.Store.CallCount);
    }

    [Fact]
    public async Task RefreshNowAsync_UnsupportedOperatingSystem_RunsOneOffCollection()
    {
        // Unknown OS is never scheduled, but a manual refresh still performs a single
        // collection so the button works and state is published.
        var server = TestData.LinuxServer(os: ServerOperatingSystem.Unknown);
        await using var h = await StartAsync(ManualDrive(), server);

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, h.Store.CallCount);
        Assert.Equal(ServerHealth.Healthy, h.State.Get(server.Id).Health);
    }

    [Fact]
    public async Task RefreshNowAsync_UnknownServerId_ReturnsInvalidConfiguration()
    {
        await using var h = await StartAsync(ManualDrive());

        var result = await h.Engine.RefreshNowAsync(Guid.NewGuid(), TestTimeout);

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.Equal(0, h.Store.CallCount);
    }

    [Fact]
    public async Task ServersChanged_ServerAdded_StartsMonitoringIt()
    {
        await using var h = await StartAsync(ManualDrive());
        var server = TestData.LinuxServer();

        h.Service.Servers.Add(server);
        h.Service.RaiseChanged();

        // Reconcile runs on the change event; wait for the new monitor's initial state.
        await WaitUntilAsync(() => h.State.GetAll().Any(s => s.ServerId == server.Id));

        var result = await h.Engine.RefreshNowAsync(server.Id, TestTimeout);
        Assert.True(result.IsSuccess);
        Assert.Equal(ServerHealth.Healthy, h.State.Get(server.Id).Health);
    }

    [Fact]
    public async Task ServersChanged_ServerRemoved_StopsMonitoringAndClearsState()
    {
        var server = TestData.LinuxServer();
        await using var h = await StartAsync(ManualDrive(), server);
        await WaitUntilAsync(() => h.State.GetAll().Any(s => s.ServerId == server.Id));

        h.Service.Servers.Clear();
        h.Service.RaiseChanged();

        await WaitUntilAsync(() => h.State.GetAll().All(s => s.ServerId != server.Id));
    }

    [Fact]
    public async Task ScheduledFailureLongAfterLastSuccess_MarksStale()
    {
        // The first (and only) success stamps LastSuccessAt at the fake start time; every
        // later scheduled cycle fails. Once the clock has advanced past twice the interval,
        // a failing cycle must report the prior reading as stale without moving LastSuccessAt.
        var server = TestData.LinuxServer(refreshIntervalSeconds: 10);
        var options = new MonitoringOptions
        {
            InitialDelay = TimeSpan.Zero, // collect immediately, at the fake start time
            StartupStagger = TimeSpan.Zero,
            AttentionInterval = TimeSpan.FromSeconds(10),
            RetryDelays = [],
        };
        await using var h = await StartAsync(options, server);
        var startedAt = h.Time.GetUtcNow();
        h.Store.ResultFactory = (s, index) => index == 0
            ? TestData.Success(TestData.Snapshot(s.Id, cpu: 5))
            : TestData.Failure(MetricsCollectionErrorCode.TimedOut);

        // Wait for the immediate first cycle to record success (clock is frozen meanwhile).
        await WaitUntilAsync(() => h.State.Get(server.Id).LastSuccessAt is not null);
        Assert.Equal(startedAt, h.State.Get(server.Id).LastSuccessAt);

        // Nudge the clock forward until a scheduled failing cycle runs. Each nudge exceeds
        // the interval, so once the loop has parked on its timer the next advance fires it;
        // repeating absorbs the register-then-advance window deterministically.
        await WaitUntilAsync(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            return h.State.Get(server.Id).Health == ServerHealth.Offline;
        });

        var state = h.State.Get(server.Id);
        Assert.True(state.IsStale);
        Assert.Equal(startedAt, state.LastSuccessAt); // never moved backwards by a failure
    }

    [Fact]
    public async Task ManualRefresh_RestartsTheServerInterval()
    {
        // Interval 10 s, but the automatic timer is parked far in the future so the only cycles
        // are the manual ones. After each manual refresh the loop reparks a fresh interval, so
        // advancing the clock by less than one interval past a manual never produces an auto
        // cycle — even once the total elapsed time exceeds a single interval.
        var server = TestData.LinuxServer(refreshIntervalSeconds: 10);
        await using var h = await StartAsync(ManualDrive(), server);

        await h.Engine.RefreshNowAsync(server.Id, TestTimeout);
        Assert.Equal(1, h.Store.CallCount);

        h.Time.Advance(TimeSpan.FromSeconds(9)); // < interval since the manual refresh
        Assert.Equal(1, h.Store.CallCount);

        await h.Engine.RefreshNowAsync(server.Id, TestTimeout); // manual again resets the countdown
        Assert.Equal(2, h.Store.CallCount);

        h.Time.Advance(TimeSpan.FromSeconds(9)); // total elapsed now 18 s > interval, but < interval since manual
        Assert.Equal(2, h.Store.CallCount); // no stray auto cycle: the manual reset the schedule
    }

    [Fact]
    public async Task ManualRefresh_DuringScheduledCollection_JoinsItWithoutImmediateDuplicate()
    {
        var server = TestData.LinuxServer(refreshIntervalSeconds: 10);
        var service = new FakeServerService();
        service.Servers.Add(server);
        var store = new ScriptedMetricsStore();
        var state = new ServerMonitoringStateStore();
        var time = new SignalingTimeProvider(TimeSpan.FromSeconds(10));
        using var collecting = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        store.ResultFactory = (current, index) =>
        {
            if (index == 0)
            {
                collecting.Release();
                release.Wait(TestTimeout);
            }

            return TestData.Success(TestData.Snapshot(current.Id, cpu: 5));
        };
        var options = new MonitoringOptions
        {
            InitialDelay = TimeSpan.Zero,
            StartupStagger = TimeSpan.Zero,
            AttentionInterval = TimeSpan.FromSeconds(10),
            RetryDelays = []
        };
        await using var engine = new MonitoringEngine(
            service, store, state, NullLogger<MonitoringEngine>.Instance, time, options);
        await engine.StartMonitoringAsync();
        Assert.True(await collecting.WaitAsync(TimeSpan.FromSeconds(10)));

        // The scheduled cycle is already inside the collector. Manual refresh must join this
        // single flight and must not leave its wake signal armed for a second immediate cycle.
        var manual = engine.RefreshNowAsync(server.Id, TestTimeout);
        release.Release();

        Assert.True((await manual).IsSuccess);
        await time.ExpectedDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, store.CallCount);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, store.CallCount);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => store.CallCount == 2);
        Assert.Equal(2, store.CallCount);
    }

    [Fact]
    public async Task LongClockJump_ProducesAtMostOneCatchUpCycle()
    {
        // Simulates system sleep/resume: the scheduler uses a single one-shot delay per cycle
        // (via TimeProvider), so one large clock jump fires that one timer at most once rather
        // than replaying every interval that elapsed while suspended.
        var server = TestData.LinuxServer(refreshIntervalSeconds: 10);
        await using var h = await StartAsync(ManualDrive(), server);

        await h.Engine.RefreshNowAsync(server.Id, TestTimeout);
        await Task.Delay(50); // let the loop repark on its next interval timer
        var before = h.Store.CallCount;

        h.Time.Advance(TimeSpan.FromHours(1)); // a whole hour "missed" while asleep
        await Task.Delay(150); // allow any catch-up cycle to run

        var catchUp = h.Store.CallCount - before;
        Assert.True(catchUp <= 1, $"a single resume must not replay missed ticks; ran {catchUp} cycles");
    }

    [Fact]
    public async Task StopMonitoringAsync_IsIdempotent()
    {
        var server = TestData.LinuxServer();
        var h = await StartAsync(ManualDrive(), server);

        await h.Engine.StopMonitoringAsync();
        await h.Engine.StopMonitoringAsync();

        await h.DisposeAsync();
    }

    [Fact]
    public async Task StopDuringScheduledRefresh_CancelsAndDrainsCollection()
    {
        var server = TestData.LinuxServer();
        var service = new FakeServerService();
        service.Servers.Add(server);
        var store = new CancellationAwareMetricsStore();
        var engine = new MonitoringEngine(
            service,
            store,
            new ServerMonitoringStateStore(),
            NullLogger<MonitoringEngine>.Instance,
            TimeProvider.System,
            new MonitoringOptions
            {
                InitialDelay = TimeSpan.Zero,
                StartupStagger = TimeSpan.Zero,
                AttentionInterval = TimeSpan.FromHours(1),
                RetryDelays = []
            });
        await using var lifetime = engine;
        await engine.StartMonitoringAsync();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await engine.StopMonitoringAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(store.Cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RefreshNow_WhileServerRemovedConcurrently_CompletesAndDoesNotHang()
    {
        // Regression for the manual-refresh orphan race: a manual refresh in flight while its
        // server is removed must still complete. The engine enqueues the request under the same
        // reconcile gate that cancels and removes monitors, so the loop's cancellation drain
        // always completes it — the caller (and the card's refresh spinner) never hangs.
        var server = TestData.LinuxServer();
        await using var h = await StartAsync(ManualDrive(), server);
        await WaitUntilAsync(() => h.State.GetAll().Any(s => s.ServerId == server.Id));

        var collecting = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);
        h.Store.ResultFactory = (s, _) =>
        {
            collecting.Release();                    // a collection is now in flight
            proceed.Wait(TimeSpan.FromSeconds(10));  // hold it open until the test removes the server
            return TestData.Success(TestData.Snapshot(s.Id, cpu: 5));
        };

        var manual = h.Engine.RefreshNowAsync(server.Id, TestTimeout);
        Assert.True(await collecting.WaitAsync(TimeSpan.FromSeconds(10))); // the loop picked up the manual

        // Remove the server mid-collection: reconcile cancels and removes the monitor.
        h.Service.Servers.Clear();
        h.Service.RaiseChanged();
        await WaitUntilAsync(() => h.State.GetAll().All(s => s.ServerId != server.Id));

        proceed.Release(); // let the now-cancelled cycle finish and the loop unwind

        var result = await manual; // must not hang; TestTimeout would have cancelled it otherwise
        Assert.True(result.IsSuccess || result.ErrorCode == MetricsCollectionErrorCode.Cancelled);
    }

    private static async Task<Harness> StartAsync(MonitoringOptions options, params Server[] servers)
    {
        var service = new FakeServerService();
        service.Servers.AddRange(servers);
        var store = new ScriptedMetricsStore();
        var state = new ServerMonitoringStateStore();
        var time = new FakeTimeProvider();
        var engine = new MonitoringEngine(
            service, store, state, NullLogger<MonitoringEngine>.Instance, time, options);
        await engine.StartMonitoringAsync();
        return new Harness(engine, service, store, state, time);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }

    private sealed record Harness(
        MonitoringEngine Engine,
        FakeServerService Service,
        ScriptedMetricsStore Store,
        ServerMonitoringStateStore State,
        FakeTimeProvider Time) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private sealed class SignalingTimeProvider(TimeSpan expectedDelay) : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new();

        public TaskCompletionSource ExpectedDelayScheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime == expectedDelay)
            {
                ExpectedDelayScheduled.TrySetResult();
            }

            return _inner.CreateTimer(callback, state, dueTime, period);
        }

        public void Advance(TimeSpan delta) => _inner.Advance(delta);
    }

    private sealed class CancellationAwareMetricsStore : IServerMetricsStore
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => null;

        public async Task<ServerMetricsCollectionResult> RefreshAsync(
            Server server,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The scheduled collection should be cancelled.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public void Remove(Guid serverId) { }
    }
}
