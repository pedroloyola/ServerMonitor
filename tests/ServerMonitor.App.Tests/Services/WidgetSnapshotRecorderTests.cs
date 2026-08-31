using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// Deterministic concurrency/lifecycle tests for the widget recorder. Time is driven by
/// <see cref="FakeTimeProvider"/> (throttle AND the bounded-shutdown timeout), and progress by
/// observable gate/semaphore barriers released by real writer progress — never by <c>Task.Delay</c>/
/// <c>Task.Yield</c> quiescence guesses (L-010/QUALITY_BAR §5/§6). The semaphore waits carry a large
/// safety-net timeout that is NOT the pass/fail boundary: correct code releases them in microseconds; the
/// net only prevents a broken test from hanging the suite.
/// </summary>
public sealed class WidgetSnapshotRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private static MonitoringCycleCompletion Completion(MonitoringOutcome outcome) => new()
    {
        ServerId = Guid.NewGuid(),
        CapturedAtUtc = Start,
        Outcome = outcome,
        Health = ServerHealth.Healthy,
        Snapshot = null
    };

    private sealed class Harness : IAsyncDisposable
    {
        public FakeTimeProvider Clock { get; } = new(Start);
        public FakeServerService Servers { get; } = new();
        public ServerMonitoringStateStore States { get; } = new();
        public DictionaryMetricsStore Metrics { get; } = new();
        public RecordingWidgetStateWriter Writer { get; } = new();
        public WidgetSnapshotRecorder Recorder { get; }

        public Harness()
        {
            Recorder = new WidgetSnapshotRecorder(
                Servers,
                States,
                Metrics,
                Writer,
                NullLogger<WidgetSnapshotRecorder>.Instance,
                Clock,
                minWriteInterval: Interval,
                shutdownDrainTimeout: ShutdownTimeout);
        }

        public Server AddServer(string name, ServerHealth health)
        {
            var server = new Server { Id = Guid.NewGuid(), Name = name };
            Servers.Servers.Add(server);
            SetHealth(server.Id, health);
            return server;
        }

        public void SetHealth(Guid id, ServerHealth health) =>
            States.Set(new ServerMonitoringState { ServerId = id, Health = health, LastSuccessAt = Start });

        public ValueTask DisposeAsync() => Recorder.DisposeAsync();
    }

    [Fact]
    public async Task First_cycle_completion_writes_a_snapshot()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Warning);

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        Assert.True(await h.Writer.WaitCompletedAsync());
        var snapshot = Assert.Single(h.Writer.Snapshots);
        var server = Assert.Single(snapshot.Servers);
        Assert.Equal("Home", server.DisplayName);
        Assert.Equal(WidgetHealth.Warning, server.Health);
        Assert.Equal(Start, snapshot.GeneratedAtUtc);
    }

    [Fact]
    public async Task Cancelled_cycle_does_not_write()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Cancelled));
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Equal(1, h.Writer.StartedCount); // the cancelled cycle contributed nothing
    }

    [Fact]
    public async Task Burst_coalesces_next_cycle_writes_latest_and_never_overlaps()
    {
        await using var h = new Harness();
        var server = h.AddServer("Home", ServerHealth.Healthy);

        // Hold the leading write open so the rest of the burst lands while a write is in flight.
        var gate = h.Writer.InstallGate();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // leading write starts (Healthy)
        Assert.True(await h.Writer.WaitStartedAsync());

        // Fleet worsens; more completions arrive within the throttle window → dirty only, no new write.
        h.SetHealth(server.Id, ServerHealth.Critical);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        // A cycle later the throttle window has elapsed, so the trailing write is allowed.
        h.Clock.Advance(Interval);
        gate.SetResult();

        Assert.True(await h.Writer.WaitCompletedAsync()); // leading (Healthy)
        Assert.True(await h.Writer.WaitCompletedAsync()); // trailing (Critical)

        Assert.Equal(2, h.Writer.StartedCount);          // coalesced: leading + one trailing, not four
        Assert.Equal(1, h.Writer.MaxConcurrent);         // single-writer: the two writes never overlapped
        Assert.Equal(WidgetHealth.Healthy, Assert.Single(h.Writer.Snapshots[0].Servers).Health);
        Assert.Equal(WidgetHealth.Critical, Assert.Single(h.Writer.Snapshots[1].Servers).Health);
        Assert.Equal(WidgetHealth.Critical, h.Writer.Snapshots[1].OverallHealth);
    }

    [Fact]
    public async Task Second_completion_within_interval_does_not_write_until_interval_elapses()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // write #1 at T0
        Assert.True(await h.Writer.WaitCompletedAsync());

        // Still inside the throttle window: this trigger must NOT produce a write.
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        // Past the window: the next completion flushes. Three starts would mean the throttled trigger wrote.
        h.Clock.Advance(Interval);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // write #2
        Assert.True(await h.Writer.WaitCompletedAsync());

        Assert.Equal(2, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Throttle_allows_but_in_flight_write_still_prevents_overlap()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        var gate = h.Writer.InstallGate();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // write #1 in flight
        Assert.True(await h.Writer.WaitStartedAsync());

        // Advance so the throttle WOULD permit another write, then trigger: _writing is still true, so no
        // second drain/write may start — proving _writing (not just the throttle) guards single-writer.
        h.Clock.Advance(Interval);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        gate.SetResult();
        Assert.True(await h.Writer.WaitCompletedAsync()); // #1
        Assert.True(await h.Writer.WaitCompletedAsync()); // the coalesced trailing
        Assert.Equal(1, h.Writer.MaxConcurrent);
    }

    [Fact]
    public async Task Writer_failure_is_isolated_and_recovers_next_cycle()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        h.Writer.FailWith = new IOException("disk full");
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // fails, must not throw
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Empty(h.Writer.Snapshots);

        h.Writer.FailWith = null;
        h.Clock.Advance(Interval);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Single(h.Writer.Snapshots);
    }

    [Fact]
    public async Task Spurious_cancellation_not_from_shutdown_is_recoverable()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        // An OCE NOT tied to the recorder's shutdown token must be treated as a recoverable failure, not
        // as a shutdown (L-1): the drain logs it and keeps going rather than exiting for good.
        h.Writer.FailWith = new OperationCanceledException();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Empty(h.Writer.Snapshots);

        h.Writer.FailWith = null;
        h.Clock.Advance(Interval);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Single(h.Writer.Snapshots); // recorder was not wedged by the spurious cancellation
    }

    [Fact]
    public async Task GetAll_failure_is_isolated_and_does_not_wedge_the_recorder()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        h.Servers.GetAllOverride = _ => throw new InvalidOperationException("repository down");
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // must not throw, no write

        h.Servers.GetAllOverride = null;
        h.Clock.Advance(Interval);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Single(h.Writer.Snapshots);
    }

    [Fact]
    public async Task Rapid_triggers_across_the_interval_boundary_never_overlap()
    {
        // Exercises the trigger-vs-drain-exit boundary repeatedly: many completions interleaved with
        // clock advances. The single _gate must keep writes non-overlapping under any interleaving, and
        // the recorder must make forward progress (writes happen) without wedging.
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        const int cycles = 20;
        for (var i = 0; i < cycles; i++)
        {
            // Each cycle: cross the interval so one write is allowed, plus two throttled completions that
            // must coalesce into nothing. Awaiting the completion forces the drain to actually run,
            // exercising the trigger-vs-drain-exit boundary at the start of the next cycle.
            h.Clock.Advance(Interval);
            h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
            h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
            h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
            Assert.True(await h.Writer.WaitCompletedAsync());
        }

        await h.DisposeAsync(); // quiesce deterministically (cancels + awaits the drain)

        Assert.Equal(1, h.Writer.MaxConcurrent);       // never two overlapping writes, any interleaving
        Assert.Equal(cycles, h.Writer.StartedCount);   // exactly one write per interval; extras coalesced
    }

    [Fact]
    public async Task Dispose_stops_further_writes()
    {
        var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitCompletedAsync());

        await h.DisposeAsync(); // cancels + awaits the drain to quiescence

        var startedBefore = h.Writer.StartedCount;
        h.Clock.Advance(Interval);
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // no-op after shutdown

        // TriggerWrite returns synchronously without starting a Task, so the count is stable now.
        Assert.Equal(startedBefore, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Dispose_is_bounded_when_a_write_ignores_cancellation()
    {
        // Fully deterministic (§30): a write ignores the cancellation token and stays in flight; the
        // FakeTimeProvider fires the bounded-shutdown timeout so DisposeAsync returns rather than hanging.
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        var gate = h.Writer.InstallGate();
        h.Writer.IgnoreCancellation = true;
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitStartedAsync()); // write is in flight, will not observe cancel

        // Calling DisposeAsync runs synchronously up to (and registers) the WaitAsync timeout timer.
        var dispose = h.Recorder.DisposeAsync().AsTask();
        h.Clock.Advance(ShutdownTimeout); // fires the bounded-shutdown timeout deterministically
        await dispose;                    // completes despite the stuck write

        gate.SetResult(); // release the abandoned write so its task can finish cleanly
    }

    // ---- test doubles -------------------------------------------------------

    private sealed class DictionaryMetricsStore : IServerMetricsStore
    {
        private readonly Dictionary<Guid, ServerMetricsSnapshot> _snapshots = new();

        public void Set(Guid id, ServerMetricsSnapshot snapshot) => _snapshots[id] = snapshot;

        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) =>
            _snapshots.GetValueOrDefault(serverId);

        public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Remove(Guid serverId) => _snapshots.Remove(serverId);
    }

    private sealed class RecordingWidgetStateWriter : IWidgetStateWriter
    {
        // Large safety net only — correct code releases the barriers immediately; this just stops a
        // broken test from hanging the suite. It is never the pass/fail boundary of a passing test.
        private const int SafetyNetMs = 30_000;

        private readonly SemaphoreSlim _started = new(0);
        private readonly SemaphoreSlim _completed = new(0);
        private readonly object _sync = new();
        private readonly List<WidgetStateSnapshot> _snapshots = new();
        private volatile TaskCompletionSource? _gate;

        private int _startedCount;
        private int _concurrent;
        private int _maxConcurrent;

        public int StartedCount => Volatile.Read(ref _startedCount);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public Exception? FailWith { get; set; }
        public bool IgnoreCancellation { get; set; }

        public IReadOnlyList<WidgetStateSnapshot> Snapshots
        {
            get { lock (_sync) { return _snapshots.ToArray(); } }
        }

        public TaskCompletionSource InstallGate()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _gate = tcs;
            return tcs;
        }

        public async Task WriteAsync(WidgetStateSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startedCount);
            var now = Interlocked.Increment(ref _concurrent);
            UpdateMax(now);
            _started.Release();

            try
            {
                var gate = _gate;
                if (gate is not null)
                {
                    if (IgnoreCancellation)
                    {
                        await gate.Task.ConfigureAwait(false);
                    }
                    else
                    {
                        await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                if (FailWith is not null)
                {
                    throw FailWith;
                }

                lock (_sync)
                {
                    _snapshots.Add(snapshot);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
                _completed.Release();
            }
        }

        private void UpdateMax(int observed)
        {
            int current;
            while (observed > (current = Volatile.Read(ref _maxConcurrent)))
            {
                Interlocked.CompareExchange(ref _maxConcurrent, observed, current);
            }
        }

        public Task<bool> WaitStartedAsync() => _started.WaitAsync(SafetyNetMs);

        public Task<bool> WaitCompletedAsync() => _completed.WaitAsync(SafetyNetMs);
    }
}
