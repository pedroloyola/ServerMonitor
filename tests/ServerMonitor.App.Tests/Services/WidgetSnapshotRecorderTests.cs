using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
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
                Servers,
                States,
                Metrics,
                MonitoringThresholds.Default,
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

    // ---- UI.9 D-UI9-6: fleet-change and startup writes (SPEC test 11, V-RC-1/2/7, V-B1, L-1) -----------

    /// <summary>Signals every entry into GetAllAsync, so a test knows a drain decision has been reached.</summary>
    private static SemaphoreSlim SignalGetAll(Harness h)
    {
        var entered = new SemaphoreSlim(0);
        h.Servers.GetAllOverride = _ =>
        {
            entered.Release();
            return Task.FromResult<IReadOnlyList<Server>>(h.Servers.Servers.ToList());
        };
        return entered;
    }

    [Fact]
    public async Task Start_with_servers_writes_nothing_and_the_first_cycle_writes()
    {
        // Cortex L-1: with servers present, a startup write would only replace the last session's real
        // readings with "no data"; the first cycle completion writes instead.
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);
        var entered = SignalGetAll(h);

        h.Recorder.Start();
        Assert.True(await entered.WaitAsync(SafetyNet)); // the startup request was evaluated...

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // ...and the first cycle writes
        Assert.True(await h.Writer.WaitCompletedAsync());
        await h.Recorder.DisposeAsync();

        Assert.Equal(1, h.Writer.StartedCount);
        Assert.Equal("Home", Assert.Single(Assert.Single(h.Writer.Snapshots).Servers).DisplayName);
    }

    [Fact]
    public async Task Start_with_servers_and_no_cycle_writes_nothing()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);
        var entered = SignalGetAll(h);

        h.Recorder.Start();
        Assert.True(await entered.WaitAsync(SafetyNet));
        await h.Recorder.DisposeAsync(); // quiesce: the startup decision has been taken

        Assert.Equal(0, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Start_on_a_loaded_empty_fleet_writes_an_empty_snapshot()
    {
        // A fresh fleet with no servers must reach the widget as Empty, not stay Missing/Unavailable.
        await using var h = new Harness();

        h.Recorder.Start();

        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Empty(Assert.Single(h.Writer.Snapshots).Servers);
    }

    [Fact]
    public async Task Start_on_a_missing_configuration_writes_an_empty_snapshot()
    {
        // DV-1: NotFound (no servers file yet: a fresh install) is an honest empty fleet.
        await using var h = new Harness();
        h.Servers.LoadStatus = ServerLoadStatus.NotFound;

        h.Recorder.Start();

        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Empty(Assert.Single(h.Writer.Snapshots).Servers);
    }

    [Fact]
    public async Task Unavailable_configuration_with_an_empty_list_and_no_fleet_change_never_writes_an_empty_snapshot()
    {
        // V-RC-1 (UI9-COR-1): a corrupt/locked servers file reads as an empty list. Writing it would claim
        // "no servers" and destroy the last-known-good snapshot. The gate must be CONSULTED (the signal),
        // and nothing may be written — at startup, nor on a later cycle.
        await using var h = new Harness();
        var consulted = new SemaphoreSlim(0);
        h.Servers.LoadStatusOverride = () =>
        {
            consulted.Release();
            return Task.FromResult(ServerLoadStatus.Unavailable);
        };

        h.Recorder.Start();
        Assert.True(await consulted.WaitAsync(SafetyNet));
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await consulted.WaitAsync(SafetyNet));

        await h.Recorder.DisposeAsync(); // awaits the drain to quiescence: every decision has been taken

        Assert.Equal(0, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Quarantined_session_add_then_delete_writes_the_empty_fleet_and_drops_the_name()
    {
        // Vigil V-B1 (UI9-SEC-1): the load status is cached Unavailable for the whole process (a quarantined
        // entry). After a fleet change the in-memory list was just persisted and is authoritative, so the
        // empty fleet must be written — otherwise the deleted server's name stays in the snapshot forever.
        await using var h = new Harness();
        var consulted = new SemaphoreSlim(0);
        h.Servers.LoadStatusOverride = () =>
        {
            consulted.Release();
            return Task.FromResult(ServerLoadStatus.Unavailable);
        };
        h.Recorder.Start();
        Assert.True(await consulted.WaitAsync(SafetyNet)); // startup: empty + Unavailable -> nothing written

        var added = h.AddServer("Added Then Deleted", ServerHealth.Healthy);
        h.Servers.RaiseChanged();
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Single(h.Writer.Snapshots[^1].Servers);

        h.Servers.Servers.Remove(added);
        h.Servers.RaiseChanged(); // no clock advance; status is still Unavailable
        Assert.True(await h.Writer.WaitCompletedAsync());
        await h.Recorder.DisposeAsync();

        var last = h.Writer.Snapshots[^1];
        Assert.Empty(last.Servers);
        Assert.DoesNotContain("Added Then Deleted", WidgetStateSerializer.Serialize(last));
    }

    [Fact]
    public async Task Unavailable_status_does_not_block_a_non_empty_fleet()
    {
        // The gate is about the EMPTY claim only; a non-empty list is real data whatever the cached status.
        await using var h = new Harness();
        h.Servers.LoadStatus = ServerLoadStatus.Unavailable;
        h.AddServer("Home", ServerHealth.Healthy);

        h.Recorder.Start();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.Single(Assert.Single(h.Writer.Snapshots).Servers);
    }

    [Fact]
    public async Task Deleting_the_last_server_during_a_blocked_cycle_write_writes_the_empty_fleet_without_any_clock_advance()
    {
        // V-RC-2 (a) / UI9-SEC-1: the cycle write is in flight; the user deletes the last server. With no
        // further cycle and the clock frozen inside the throttle window, the drain must still write the
        // empty fleet — otherwise the deleted server's name persists in the snapshot indefinitely.
        await using var h = new Harness();
        var server = h.AddServer("Deleted Box", ServerHealth.Healthy);
        h.Recorder.Start();

        var gate = h.Writer.InstallGate();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // cycle write, held open
        Assert.True(await h.Writer.WaitStartedAsync());

        h.Servers.Servers.Remove(server);
        h.Servers.RaiseChanged();
        gate.SetResult(); // NO h.Clock.Advance: we are still inside the throttle window

        Assert.True(await h.Writer.WaitCompletedAsync()); // the cycle write
        Assert.True(await h.Writer.WaitCompletedAsync()); // the forced fleet-change write
        await h.Recorder.DisposeAsync();

        Assert.Equal(2, h.Writer.StartedCount);
        Assert.Equal(1, h.Writer.MaxConcurrent);
        var last = h.Writer.Snapshots[^1];
        Assert.Empty(last.Servers);
        Assert.DoesNotContain("Deleted Box", WidgetStateSerializer.Serialize(last));
    }

    [Fact]
    public async Task A_burst_of_fifty_fleet_changes_coalesces_to_at_most_two_writes_that_never_overlap()
    {
        // V-RC-2 (b): ≤1 in flight + ≤1 pending, whatever the burst size.
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        var gate = h.Writer.InstallGate();
        h.Recorder.Start();
        h.Servers.RaiseChanged(); // write #1, held open
        Assert.True(await h.Writer.WaitStartedAsync());

        for (var i = 0; i < 50; i++)
        {
            h.Servers.RaiseChanged();
        }

        gate.SetResult();
        Assert.True(await h.Writer.WaitCompletedAsync());
        Assert.True(await h.Writer.WaitCompletedAsync());
        await h.Recorder.DisposeAsync(); // quiesce: no third write can still be pending

        Assert.Equal(2, h.Writer.StartedCount);
        Assert.Equal(1, h.Writer.MaxConcurrent);
    }

    [Fact]
    public async Task Hiding_a_server_removes_its_name_through_the_same_path()
    {
        // V-RC-2 (c): hide is a fleet change too; the hidden server's name must leave the snapshot.
        await using var h = new Harness();
        var visible = h.AddServer("Visible", ServerHealth.Healthy);
        var secret = h.AddServer("Hidden Later", ServerHealth.Healthy);
        h.Recorder.Start();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));
        Assert.True(await h.Writer.WaitCompletedAsync());

        h.Servers.Servers[h.Servers.Servers.IndexOf(secret)] = secret with { IsHidden = true };
        h.Servers.RaiseChanged(); // no clock advance

        Assert.True(await h.Writer.WaitCompletedAsync());
        var last = h.Writer.Snapshots[^1];
        Assert.Equal(visible.Id, Assert.Single(last.Servers).Id);
        Assert.DoesNotContain("Hidden Later", WidgetStateSerializer.Serialize(last));
    }

    [Fact]
    public async Task Forced_writes_do_not_hold_back_or_speed_up_the_cycle_cadence()
    {
        // The throttle bypass is scoped to fleet changes: a forced write does not anchor the throttle (so
        // the next cycle still writes at once), and cycles stay throttled among themselves.
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);
        h.Recorder.Start();
        h.Servers.RaiseChanged();
        Assert.True(await h.Writer.WaitCompletedAsync()); // forced, T0

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // cycle, T0: not held back
        Assert.True(await h.Writer.WaitCompletedAsync());

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // cycle, T0: throttled
        await h.Recorder.DisposeAsync();

        Assert.Equal(2, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Cycle_triggers_stay_throttled_even_after_a_fleet_change()
    {
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);
        h.Recorder.Start();

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // cycle, anchors at T0
        Assert.True(await h.Writer.WaitCompletedAsync());
        h.Servers.RaiseChanged();                                           // forced, bypasses the window
        Assert.True(await h.Writer.WaitCompletedAsync());

        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success)); // cycle inside window: no write
        await h.Recorder.DisposeAsync();

        Assert.Equal(2, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Dispose_unsubscribes_from_fleet_changes_and_later_changes_write_nothing()
    {
        // SPEC test 6 / V-RC-7.
        var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);

        Assert.Equal(0, h.Servers.ServersChangedHandlerCount);
        h.Recorder.Start();
        h.Recorder.Start(); // idempotent: still exactly one subscription
        Assert.Equal(1, h.Servers.ServersChangedHandlerCount);
        h.Servers.RaiseChanged();
        Assert.True(await h.Writer.WaitCompletedAsync());

        await h.DisposeAsync();

        Assert.Equal(0, h.Servers.ServersChangedHandlerCount);
        var before = h.Writer.StartedCount;
        h.Servers.RaiseChanged();
        h.Recorder.Start(); // no resurrection after dispose
        Assert.Equal(0, h.Servers.ServersChangedHandlerCount);
        Assert.Equal(before, h.Writer.StartedCount);
    }

    [Fact]
    public async Task Fleet_change_handler_never_does_io_on_the_raising_thread()
    {
        // V-RC-7: ServerService raises ServersChanged inside the user's save path. Even with the store read
        // blocked, raising must return at once; the write happens on the drain.
        await using var h = new Harness();
        h.AddServer("Home", ServerHealth.Healthy);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new SemaphoreSlim(0);
        h.Servers.GetAllOverride = async _ =>
        {
            entered.Release();
            await release.Task;
            return h.Servers.Servers.ToList();
        };
        h.Recorder.Start();
        Assert.True(await entered.WaitAsync(SafetyNet)); // startup drain is blocked inside GetAllAsync

        h.Servers.RaiseChanged(); // returns synchronously; would deadlock the test if it awaited the store

        release.SetResult();
        Assert.True(await h.Writer.WaitCompletedAsync()); // the fleet-change write (startup alone writes nothing)
        await h.Recorder.DisposeAsync();
        Assert.Equal(1, h.Writer.StartedCount);
        Assert.Equal(1, h.Writer.MaxConcurrent);
    }

    [Fact]
    public async Task Mapped_snapshot_carries_the_engine_thresholds_reason_and_stale_policy()
    {
        // The recorder hands its injected thresholds to the mapper (D-UI9-2) and the per-server interval
        // reaches staleAfterSeconds (D-UI9-3).
        await using var h = new Harness();
        var server = new Server { Id = Guid.NewGuid(), Name = "Busy", RefreshIntervalSeconds = 60 };
        h.Servers.Servers.Add(server);
        h.SetHealth(server.Id, ServerHealth.Warning);
        h.Metrics.Set(server.Id, new ServerMetricsSnapshot
        {
            ServerId = server.Id, CollectedAt = Start, CpuUsagePercent = 10, MemoryUsagePercent = 20, DiskUsagePercent = 85
        });

        h.Recorder.Start();
        h.Recorder.OnCycleCompleted(Completion(MonitoringOutcome.Success));

        Assert.True(await h.Writer.WaitCompletedAsync());
        var mapped = Assert.Single(Assert.Single(h.Writer.Snapshots).Servers);
        Assert.Equal(WidgetAttentionMetrics.Disk, mapped.AttentionMetric);
        Assert.Equal(120, mapped.StaleAfterSeconds);
    }

    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

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
