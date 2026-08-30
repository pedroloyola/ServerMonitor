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
    private readonly IServerMonitoringStateStore _stateStore;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IWidgetStateWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minWriteInterval;
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly ILogger<WidgetSnapshotRecorder> _logger;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private bool _dirty;
    private bool _writing;
    private bool _disposed;
    // Cadence is measured on the MONOTONIC timestamp, never wall-clock: a backward NTP/manual clock step
    // must not strand a dirty snapshot, and a forward step must not permit a write sooner than the
    // interval. Wall-clock time is used only for the snapshot's GeneratedAtUtc.
    private long? _lastWriteTimestamp;
    private Task _drain = Task.CompletedTask;
    private long _failureCount;

    public WidgetSnapshotRecorder(
        IServerService servers,
        IServerMonitoringStateStore stateStore,
        IServerMetricsStore metricsStore,
        IWidgetStateWriter writer,
        ILogger<WidgetSnapshotRecorder> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? minWriteInterval = null,
        TimeSpan? shutdownDrainTimeout = null)
    {
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
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
    /// Marks the snapshot dirty and, if the throttle allows and no drain is running, starts the single
    /// writer. Internal so tests can drive it directly; production only calls it from
    /// <see cref="OnCycleCompleted"/>.
    /// </summary>
    internal void TriggerWrite()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _dirty = true;

            // A running drain will observe _dirty; establishing this under the same lock the drain uses
            // to stop makes start-vs-stop linearizable (L-010). Throttle: if the interval has not
            // elapsed, leave the snapshot dirty — the next completion past the interval flushes it, so
            // no timer is needed.
            if (_writing || !MayWriteNowLocked())
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
                lock (_gate)
                {
                    // Stop when asked to, when nothing is pending, or when the throttle window has not
                    // yet elapsed. In the last case _dirty stays set and a later completion re-arms us.
                    if (cancellationToken.IsCancellationRequested || !_dirty || !MayWriteNowLocked())
                    {
                        _writing = false;
                        return;
                    }

                    _dirty = false;
                    _lastWriteTimestamp = _timeProvider.GetTimestamp();
                }

                try
                {
                    await WriteOnceAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task WriteOnceAsync(CancellationToken cancellationToken)
    {
        var servers = await _servers.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = WidgetSnapshotMapper.Map(
            servers,
            id => _stateStore.Get(id),
            id => _metricsStore.GetLastSnapshot(id),
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
    /// Cancels the drain and waits — with a hard timeout (§30) — for any in-flight write to unwind, so
    /// closing the app never blocks on a stuck write. The normal cycle is the source of truth, so there
    /// is no final forced write.
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
