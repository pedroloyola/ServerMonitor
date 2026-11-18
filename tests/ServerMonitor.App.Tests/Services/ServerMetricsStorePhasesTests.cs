using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M16.1: the store completes a refresh from the collector's early-result callback, while the session's second phase
/// still runs; the result returned afterwards only closes that phase and is never published again. The reservation is
/// always completed, also when the refresh joins one already under way.
/// </summary>
public sealed class ServerMetricsStorePhasesTests
{
    [Fact]
    public async Task TheCallback_CompletesTheRefresh_BeforeThePhaseTwoEnds()
    {
        var server = TestData.LinuxServer();
        var early = TestData.Snapshot(server.Id, cpu: 10);
        var collector = new PhasedCollector { BaseResult = TestData.Success(early) };
        var store = new ServerMetricsStore(collector);

        var refresh = store.RefreshAsync(server, probes: null);
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Same(early, store.GetLastSnapshot(server.Id));
        Assert.False(collector.PhaseTwo.Task.IsCompleted);

        collector.PhaseTwo.SetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 99)));
        await collector.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The returned result closes the second phase only: the first one stays published.
        Assert.Same(early, store.GetLastSnapshot(server.Id));
        Assert.Same(result, await refresh);
    }

    [Fact]
    public async Task AFailure_WithoutCallback_GoesThroughTheExistingPath()
    {
        var server = TestData.LinuxServer();
        var collector = new PhasedCollector { BaseResult = null };
        var store = new ServerMetricsStore(collector);

        var refresh = store.RefreshAsync(server, probes: null);
        Assert.False(refresh.IsCompleted);
        collector.PhaseTwo.SetResult(TestData.Failure(MetricsCollectionErrorCode.ConnectionFailed));
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, result.ErrorCode);
        Assert.Null(store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task APhaseTwoThatThrows_AfterTheCallback_DoesNotChangeThePublishedResult()
    {
        var server = TestData.LinuxServer();
        var early = TestData.Snapshot(server.Id, cpu: 10);
        var collector = new PhasedCollector { BaseResult = TestData.Success(early) };
        var store = new ServerMetricsStore(collector);

        var result = await store.RefreshAsync(server, probes: null).WaitAsync(TimeSpan.FromSeconds(10));
        collector.PhaseTwo.SetException(new InvalidOperationException());
        await collector.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Same(early, store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task TheReservation_IsHandedToTheCollector_AndCompletedWhenThePhaseTwoEnds()
    {
        var server = TestData.LinuxServer();
        var scheduler = Scheduler();
        var reservation = scheduler.TryReserve(server.Id, CancellationToken.None)!;
        var collector = new PhasedCollector { BaseResult = TestData.Success(TestData.Snapshot(server.Id, cpu: 1)) };
        var store = new ServerMetricsStore(collector);

        await store.RefreshAsync(server, reservation).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(reservation, collector.LastProbes);
        Assert.True(scheduler.IsInFlight(server.Id));

        collector.PhaseTwo.SetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 1)));
        await collector.Returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !scheduler.IsInFlight(server.Id));
    }

    [Fact]
    public async Task JoiningARefreshUnderWay_CompletesTheUnusedReservation()
    {
        var server = TestData.LinuxServer();
        var scheduler = Scheduler();
        var collector = new FakeMetricsCollector();
        var gate = collector.Gate();
        var store = new ServerMetricsStore(collector);

        var first = store.RefreshAsync(server);
        var reservation = scheduler.TryReserve(server.Id, CancellationToken.None)!;
        var second = store.RefreshAsync(server, reservation);

        Assert.Same(first, second);
        Assert.False(scheduler.IsInFlight(server.Id));

        gate.SetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 5)));
        await first;
    }

    [Fact]
    public void ANullServer_StillCompletesTheReservation()
    {
        var scheduler = Scheduler();
        var serverId = TestData.LinuxServer().Id;
        var reservation = scheduler.TryReserve(serverId, CancellationToken.None)!;
        var store = new ServerMetricsStore(new FakeMetricsCollector());

        // RefreshAsync is not async: the null check throws synchronously, before any Task exists.
        Assert.Throws<ArgumentNullException>(() => { _ = store.RefreshAsync(null!, reservation); });
        Assert.False(scheduler.IsInFlight(serverId));
    }

    [Fact]
    public async Task AOnePhaseCollector_CompletesTheReservation()
    {
        // The interface default runs the one-phase collection and calls back once with its result.
        var server = TestData.LinuxServer();
        var scheduler = Scheduler();
        var reservation = scheduler.TryReserve(server.Id, CancellationToken.None)!;
        var collector = new FakeMetricsCollector { Result = TestData.Success(TestData.Snapshot(server.Id, cpu: 5)) };
        var store = new ServerMetricsStore(collector);

        var result = await store.RefreshAsync(server, reservation);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, collector.CallCount);
        await WaitUntilAsync(() => !scheduler.IsInFlight(server.Id));
    }

    [Fact]
    public async Task ANewRefresh_CanStartWhileThePreviousPhaseTwoStillRuns()
    {
        var server = TestData.LinuxServer();
        var first = new PhasedCollector { BaseResult = TestData.Success(TestData.Snapshot(server.Id, cpu: 1)) };
        var store = new ServerMetricsStore(first);

        await store.RefreshAsync(server, probes: null).WaitAsync(TimeSpan.FromSeconds(10));
        first.BaseResult = TestData.Success(TestData.Snapshot(server.Id, cpu: 2));
        var second = await store.RefreshAsync(server, probes: null).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2d, second.Snapshot!.CpuUsagePercent);
        Assert.Equal(2, first.CallCount);
        first.PhaseTwo.TrySetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 3)));
    }

    private static RemoteProbeScheduler Scheduler() => new([new OneProbe()], new HistoryGeneration());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Calls back with <see cref="BaseResult"/> (when set) and then holds the "second phase" open until the test
    /// completes <see cref="PhaseTwo"/>.
    /// </summary>
    private sealed class PhasedCollector : IServerMetricsCollector
    {
        private int _callCount;
        private ServerMetricsCollectionResult? _baseResult;

        public ServerMetricsCollectionResult? BaseResult
        {
            get => Volatile.Read(ref _baseResult);
            set => Volatile.Write(ref _baseResult, value);
        }

        public TaskCompletionSource<ServerMetricsCollectionResult> PhaseTwo { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RemoteProbeReservation? LastProbes { get; private set; }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ServerMetricsCollectionResult> CollectAsync(Server server, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The phased path must be used.");

        public async Task<ServerMetricsCollectionResult> CollectAsync(
            Server server,
            MetricsCollectionPhases phases,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastProbes = phases.Probes;
            await Task.Yield();
            if (BaseResult is { } baseResult)
            {
                phases.OnBaseResult(baseResult);
            }

            try
            {
                return await PhaseTwo.Task.ConfigureAwait(false);
            }
            finally
            {
                Returned.TrySetResult();
            }
        }
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
