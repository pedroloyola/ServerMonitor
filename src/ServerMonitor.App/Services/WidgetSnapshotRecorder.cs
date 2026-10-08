using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Services;

/// <summary>
/// Keeps <c>widget-state.json</c> fresh by riding the existing monitoring-cycle seam (ADR-011/§14),
/// with NO new timer, polling loop, or independent background worker (§15) — the only "wake" is the
/// next monitoring-cycle completion, which the engine already produces.
/// <para>
/// <b>Cadence (leading-edge throttle).</b> Completions arrive once <i>per server per cycle</i>, so a
/// cycle produces a burst. A naive write-per-completion would amplify to many full-fleet writes per
/// second under staggered completions. Instead a write is started only when at least
/// <see cref="_minWriteInterval"/> has elapsed since the previous write began (measured on the injected
/// <see cref="TimeProvider"/>). A burst therefore collapses to one write, and the next cycle's first
/// completion — always &gt; one interval later in practice — flushes the now-complete fleet. This bounds
/// the write rate to at most one snapshot per interval while still writing every cycle. No clock is
/// polled: a completion within the throttle shadow just marks the snapshot dirty and the <i>next</i>
/// completion past the interval flushes it.
/// </para>
/// <para>
/// <b>Concurrency (P-007/L-010).</b> <c>_dirty</c>, <c>_writing</c>, and <c>_lastWriteStartedUtc</c> are
/// mutated only under <c>_gate</c>. A single-writer drain owns all writes; a completion arriving during
/// a write sets <c>_dirty</c> and the drain re-evaluates it, so no completion is lost and two writes
/// never overlap. Because the drain re-reads the live stores at write time (the same sources the
/// dashboard uses, §20), coalesced/dropped triggers cost no freshness beyond the throttle interval.
/// </para>
/// <para>
/// <b>Fleet-change and startup writes (UI.9 D-UI9-6).</b> Cycles alone cannot reflect a fleet that stops
/// cycling: deleting the last server ends all completions, so the deleted server (and its name) would stay
/// in the snapshot forever. <see cref="Start"/> therefore subscribes to
/// <see cref="IServerService.ServersChanged"/> and requests one write at startup. Those two triggers set
/// <c>_forcePending</c> (under <c>_gate</c>) and BYPASS the throttle — they are rare and user-initiated —
/// but go through the very same single-writer drain, so they coalesce (at most one write in flight plus
/// one pending) and never overlap a cycle write. A forced write does not move the cycle throttle anchor,
/// so cycle writes keep their exact cadence and the first cycle after startup is never held back.
/// </para>
/// <para>
/// <b>What a forced write may publish.</b> The STARTUP request only writes when the fleet is empty (Cortex
/// L-1): with servers present the first cycle completion writes within seconds, and an earlier write would
/// only replace the last session's real readings with "no data". An EMPTY fleet is written only when it is
/// true (V-RC-1): the configuration loaded (or does not exist yet), OR the user changed the fleet in this
/// session (Vigil V-B1) — after a <see cref="IServerService.ServersChanged"/> the in-memory list was just
/// persisted and is authoritative, exactly as the dashboard treats it (<c>_configurationChanged</c>). A
/// corrupt/locked configuration with no change in this session reads as an empty list that is NOT the
/// truth, so nothing is written and the last-known-good snapshot ages into Stale instead.
/// </para>
/// Every failure is isolated and swallowed (§16): building or writing the snapshot can never throw into
/// the cycle. Shutdown is bounded (§30): the drain is cancelled and awaited with a timeout so closing
/// the app never hangs on a stuck write.
/// </summary>
public sealed class WidgetSnapshotRecorder : IMonitoringCycleObserver, IAsyncDisposable
{
    /// <summary>Default minimum spacing between writes — half the default 30s cycle, so a normal cycle writes once.</summary>
    public static readonly TimeSpan DefaultMinWriteInterval = TimeSpan.FromSeconds(15);

    /// <summary>Default upper bound on how long <see cref="DisposeAsync"/> waits for an in-flight write.</summary>
    public static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IServerService _servers;
    private readonly IServerLoadStatusSource _loadStatus;
    private readonly IServerMonitoringStateStore _stateStore;
    private readonly IServerMetricsStore _metricsStore;
    private readonly MonitoringThresholds _thresholds;
    private readonly IWidgetStateWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minWriteInterval;
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly ILogger<WidgetSnapshotRecorder> _logger;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private bool _dirty;
    // Set only by ServersChanged; consumed by the drain as a throttle bypass.
    private bool _forcePending;
    // Set only by Start(); a throttle bypass that writes ONLY an empty fleet (Cortex L-1).
    private bool _startupPending;
    // Latched by the first ServersChanged after Start(): the in-memory list is authoritative (Vigil V-B1).
    private bool _fleetChangedSinceStart;
    private bool _writing;
    private bool _disposed;
    private bool _started;
    // Cadence is measured on the MONOTONIC timestamp, never wall-clock: a backward NTP/manual clock step
    // must not strand a dirty snapshot, and a forward step must not permit a write sooner than the
    // interval. Wall-clock time is used only for the snapshot's GeneratedAtUtc.
    private long? _lastWriteTimestamp;
    private Task _drain = Task.CompletedTask;
    private long _failureCount;

    public WidgetSnapshotRecorder(
        IServerService servers,
        IServerLoadStatusSource loadStatus,
        IServerMonitoringStateStore stateStore,
        IServerMetricsStore metricsStore,
        MonitoringThresholds thresholds,
        IWidgetStateWriter writer,
        ILogger<WidgetSnapshotRecorder> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? minWriteInterval = null,
        TimeSpan? shutdownDrainTimeout = null)
    {
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _loadStatus = loadStatus ?? throw new ArgumentNullException(nameof(loadStatus));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
        _thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minWriteInterval = minWriteInterval ?? DefaultMinWriteInterval;
        _shutdownDrainTimeout = shutdownDrainTimeout ?? DefaultShutdownDrainTimeout;
    }

    public void OnCycleCompleted(MonitoringCycleCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // A cancelled cycle carried no measurement and changed no state — nothing to reflect.
        if (completion.Outcome == MonitoringOutcome.Cancelled)
        {
            return;
        }

        TriggerWrite();
    }

    /// <summary>
    /// D-UI9-6: subscribes to fleet changes and requests the startup write. Called once by the app after the
    /// host starts; idempotent, and a no-op after <see cref="DisposeAsync"/>. Never throws into startup.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started)
            {
                return;
            }

            // Under _gate so it cannot interleave with DisposeAsync's unsubscribe. A field-like event's
            // add/remove is lock-free, so no lock-order hazard is introduced.
            _started = true;
            _servers.ServersChanged += OnServersChanged;
        }

        Trigger(WriteTrigger.Startup);
    }

    private enum WriteTrigger
    {
        Cycle,
        Startup,
        FleetChange
    }

    // V-RC-7: synchronous, exception-proof, no I/O on the invoker's thread (ServerService raises this inside
    // the user's save path, before the engine's own reconcile handler). TriggerWrite only flips flags under
    // _gate and, at most, queues the drain on the thread pool.
    private void OnServersChanged(object? sender, EventArgs e)
    {
        try
        {
            Trigger(WriteTrigger.FleetChange);
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    /// <summary>
    /// Marks the snapshot dirty and, if the throttle allows and no drain is running, starts the single
    /// writer. <paramref name="force"/> (fleet change / startup only) bypasses the throttle. Internal so
    /// tests can drive it directly; production calls it from <see cref="OnCycleCompleted"/>,
    /// <see cref="Start"/>, and the <see cref="IServerService.ServersChanged"/> handler.
    /// </summary>
    internal void TriggerWrite(bool force = false) => Trigger(force ? WriteTrigger.FleetChange : WriteTrigger.Cycle);

    private void Trigger(WriteTrigger trigger)
    {
        var force = trigger != WriteTrigger.Cycle;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (trigger)
            {
                case WriteTrigger.Startup:
                    _startupPending = true;
                    break;
                case WriteTrigger.FleetChange:
                    _forcePending = true;
                    _fleetChangedSinceStart = true;
                    break;
                default:
                    _dirty = true;
                    break;
            }

            // A running drain will observe _dirty/_forcePending; establishing this under the same lock the
            // drain uses to stop makes start-vs-stop linearizable (L-010). Throttle (cycles only): if the
            // interval has not elapsed, leave the snapshot dirty — the next completion past the interval
            // flushes it, so no timer is needed.
            if (_writing || (!force && !MayWriteNowLocked()))
            {
                return;
            }

            // Do NOT stamp _lastWriteTimestamp here: the drain stamps it when it actually commits to a
            // write. Stamping it now would make the drain's own throttle check see 0 elapsed and skip the
            // very write we just started.
            _writing = true;
            _drain = Task.Run(() => DrainAsync(_shutdown.Token));
        }
    }

    private bool MayWriteNowLocked() =>
        _lastWriteTimestamp is not { } last ||
        _timeProvider.GetElapsedTime(last) >= _minWriteInterval;

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                bool startupOnly;
                bool fleetChanged;
                lock (_gate)
                {
                    // Stop when asked to, or when nothing may be written now: no forced write pending AND
                    // (nothing dirty OR the cycle throttle window has not yet elapsed). In the last case
                    // _dirty stays set and a later completion re-arms us.
                    var forced = _forcePending || _startupPending;
                    if (cancellationToken.IsCancellationRequested || (!forced && (!_dirty || !MayWriteNowLocked())))
                    {
                        _writing = false;
                        return;
                    }

                    // A startup request on its own may only publish an empty fleet (L-1); anything else
                    // pending alongside it (a fleet change, a dirty cycle) makes this an ordinary write.
                    startupOnly = _startupPending && !_forcePending && !_dirty;
                    fleetChanged = _fleetChangedSinceStart;

                    // One write reflects the live stores, so it satisfies every kind of pending request.
                    _forcePending = false;
                    _startupPending = false;
                    _dirty = false;
                    if (!forced)
                    {
                        // Only cycle writes anchor the cycle throttle (see class remarks).
                        _lastWriteTimestamp = _timeProvider.GetTimestamp();
                    }
                }

                try
                {
                    await WriteOnceAsync(startupOnly, fleetChanged, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _writing = false;
                    }

                    return;
                }
                catch (Exception exception)
                {
                    // Any failure — including a spurious OCE not tied to shutdown (L-1) — is recoverable:
                    // log and keep the writer alive; the loop re-checks _dirty and either writes again
                    // (next cycle, past the throttle) or quiesces cleanly.
                    LogFailure(exception);
                }
            }
        }
        catch (Exception exception)
        {
            // Absolute backstop: never let the drain fault silently and strand _writing == true.
            lock (_gate)
            {
                _writing = false;
            }

            LogFailure(exception);
        }
    }

    private async Task WriteOnceAsync(bool startupOnly, bool fleetChanged, CancellationToken cancellationToken)
    {
        var servers = await _servers.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (startupOnly && servers.Count > 0)
        {
            // Cortex L-1: the first cycle completion writes real readings within seconds; writing now would
            // only replace the last session's readings with "no data".
            _logger.LogDebug("Widget startup write skipped: the first cycle completion will write.");
            return;
        }

        if (servers.Count == 0 && !fleetChanged)
        {
            // V-RC-1 (UI9-COR-1): an empty list is only "no servers" when the fleet really loaded. Missing
            // configuration (fresh install) is honestly empty too; Unavailable (corrupt/locked/quarantined)
            // is not, so nothing is written and the last-known-good snapshot ages into Stale instead. A fleet
            // change in this session makes the persisted in-memory list authoritative (Vigil V-B1).
            var status = await _loadStatus.GetLoadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is not (ServerLoadStatus.Loaded or ServerLoadStatus.NotFound))
            {
                _logger.LogDebug("Widget snapshot not written: server configuration is {Status}.", status);
                return;
            }
        }

        var snapshot = WidgetSnapshotMapper.Map(
            servers,
            id => _stateStore.Get(id),
            id => _metricsStore.GetLastSnapshot(id),
            _thresholds,
            _timeProvider.GetUtcNow());

        await _writer.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Widget state snapshot updated ({Count} server(s)).", snapshot.Servers.Count);
    }

    private void LogFailure(Exception exception)
    {
        var total = Interlocked.Increment(ref _failureCount);

        // Coarse logging so a persistently failing disk cannot spam the log; never the payload (§31).
        if (total == 1 || total % 50 == 0)
        {
            _logger.LogWarning(
                "Widget snapshot write failed ({Total} so far). Monitoring is unaffected. Error: {Type}.",
                total,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Unsubscribes from fleet changes, then cancels the drain and waits — with a hard timeout (§30) — for
    /// any in-flight write to unwind, so closing the app never blocks on a stuck write. The normal cycle is
    /// the source of truth, so there is no final forced write.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return; // idempotent: safe if both the container and a caller dispose us
            }

            _disposed = true;

            // V-RC-7: unsubscribe BEFORE cancelling, so no fleet change can queue work against a dying drain.
            if (_started)
            {
                _servers.ServersChanged -= OnServersChanged;
            }
        }

        _shutdown.Cancel();

        Task drain;
        lock (_gate)
        {
            drain = _drain;
        }

        var drainCompleted = false;
        try
        {
            await drain.WaitAsync(_shutdownDrainTimeout, _timeProvider).ConfigureAwait(false);
            drainCompleted = true;
        }
        catch
        {
            // Timeout: abandon the in-flight write (the process is exiting). The atomic writer guarantees
            // the on-disk file is either the old or a complete new snapshot. The drain is self-isolating.
        }

        // Only dispose the token source once no drain can still touch it. On timeout the drain may still
        // be running and reads _shutdown.Token, so we leave the (callback/timer-free) CTS to the GC.
        if (drainCompleted)
        {
            _shutdown.Dispose();
        }
    }
}
