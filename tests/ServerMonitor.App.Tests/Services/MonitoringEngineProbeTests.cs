using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M16.1: the engine reserves each poll's second phase with the server's monitoring token, counts the poll complete
/// without waiting for that phase, runs the built-in commands only while it is still in flight, and cancels it when
/// the server is removed.
/// </summary>
public sealed class MonitoringEngineProbeTests
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);

    private static CancellationToken TestTimeout => new CancellationTokenSource(DeadlockGuard).Token;

    private static MonitoringOptions ManualDrive() => new()
    {
        InitialDelay = TimeSpan.FromHours(1),
        StartupStagger = TimeSpan.Zero,
        AttentionInterval = TimeSpan.FromHours(1),
        RetryDelays = [],
    };

    [Fact]
    public async Task ThePoll_CompletesWhileThePhaseTwoRuns_AndTheNextPollRunsNoProbes()
    {
        var server = TestData.LinuxServer();
        var scheduler = new RemoteProbeScheduler([new OneProbe()], new HistoryGeneration());
        var store = new ReservationKeepingStore();
        await using var engine = await StartAsync(store, scheduler, server);

        var first = await engine.RefreshNowAsync(server.Id, TestTimeout);

        // The poll is over, yet the reservation is still open: the engine never waited for the second phase.
        Assert.True(first.IsSuccess);
        var reservation = Assert.Single(store.Reservations);
        Assert.NotNull(reservation);
        Assert.Equal(server.Id, reservation.ServerId);
        Assert.True(scheduler.IsInFlight(server.Id));

        await engine.RefreshNowAsync(server.Id, TestTimeout);
        Assert.Null(store.Reservations[1]);

        reservation.Complete();
        await engine.RefreshNowAsync(server.Id, TestTimeout);
        Assert.NotNull(store.Reservations[2]);
        store.Reservations[2]!.Complete();
    }

    [Fact]
    public async Task ManyPollsWhileThePhaseTwoRuns_NeverOpenASecondPhaseTwo()
    {
        // The per-server cap (ADR-027): one second phase per server, so at most two sessions to it at once (that one
        // and the next poll's built-in session), however many polls come while it runs.
        var server = TestData.LinuxServer();
        var scheduler = new RemoteProbeScheduler([new OneProbe()], new HistoryGeneration());
        var store = new ReservationKeepingStore();
        await using var engine = await StartAsync(store, scheduler, server);

        for (var poll = 0; poll < 5; poll++)
        {
            await engine.RefreshNowAsync(server.Id, TestTimeout);
        }

        Assert.Equal(5, store.Reservations.Count);
        Assert.NotNull(store.Reservations[0]);
        Assert.All(store.Reservations.Skip(1), Assert.Null);
        Assert.True(scheduler.IsInFlight(server.Id));
        store.Reservations[0]!.Complete();
    }

    [Fact]
    public async Task RemovingTheServer_CancelsItsPhaseTwo()
    {
        var server = TestData.LinuxServer();
        var scheduler = new RemoteProbeScheduler([new OneProbe()], new HistoryGeneration());
        var store = new ReservationKeepingStore();
        var service = new FakeServerService();
        service.Servers.Add(server);
        var states = new ServerMonitoringStateStore();
        await using var engine = new MonitoringEngine(
            service, store, states, NullLogger<MonitoringEngine>.Instance, new TimerRecordingTimeProvider(), ManualDrive(),
            probeScheduler: scheduler);
        await engine.StartMonitoringAsync();

        await engine.RefreshNowAsync(server.Id, TestTimeout);
        var reservation = Assert.Single(store.Reservations)!;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = reservation.CancellationToken.Register(() => cancelled.TrySetResult());

        service.Servers.Clear();
        service.RaiseChanged();

        await cancelled.Task.WaitAsync(DeadlockGuard);
        reservation.Complete();
        Assert.False(scheduler.IsInFlight(server.Id));
    }

    [Fact]
    public async Task WithoutAScheduler_NoReservationIsPassed()
    {
        var server = TestData.LinuxServer();
        var store = new ReservationKeepingStore();
        await using var engine = await StartAsync(store, scheduler: null, server);

        await engine.RefreshNowAsync(server.Id, TestTimeout);

        Assert.Null(Assert.Single(store.Reservations));
    }

    private static async Task<MonitoringEngine> StartAsync(
        ReservationKeepingStore store,
        RemoteProbeScheduler? scheduler,
        Server server)
    {
        var service = new FakeServerService();
        service.Servers.Add(server);
        var engine = new MonitoringEngine(
            service,
            store,
            new ServerMonitoringStateStore(),
            NullLogger<MonitoringEngine>.Instance,
            new TimerRecordingTimeProvider(),
            ManualDrive(),
            probeScheduler: scheduler);
        await engine.StartMonitoringAsync();
        return engine;
    }

    /// <summary>
    /// Publishes the built-in result at once and keeps every reservation open, as a session whose second phase is
    /// still running would. The test completes them.
    /// </summary>
    private sealed class ReservationKeepingStore : IServerMetricsStore
    {
        private readonly List<RemoteProbeReservation?> _reservations = [];

        public IReadOnlyList<RemoteProbeReservation?> Reservations
        {
            get
            {
                lock (_reservations)
                {
                    return [.. _reservations];
                }
            }
        }

        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => null;

        public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
            RefreshAsync(server, null, cancellationToken);

        public Task<ServerMetricsCollectionResult> RefreshAsync(
            Server server,
            RemoteProbeReservation? probes,
            CancellationToken cancellationToken = default)
        {
            lock (_reservations)
            {
                _reservations.Add(probes);
            }

            return Task.FromResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 5)));
        }

        public void Remove(Guid serverId) { }
    }

    private sealed class OneProbe : IRemoteProbe
    {
        public string Id => "test.probe";

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command => "true";

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }
}
