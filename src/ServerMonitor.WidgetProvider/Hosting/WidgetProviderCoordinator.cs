using ServerMonitor.WidgetProvider.Diagnostics;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Hosting;

/// <summary>
/// Turns host callbacks into widget paints, independent of COM/WinRT so it is fully unit-testable (§33).
/// It owns the registry of active widgets and, for each callback, reads the untrusted snapshot →
/// evaluates freshness → builds the neutral dev card → pushes an update. Nothing here opens SSH, the
/// engine, credentials, or history — the snapshot file is the only input (ADR-018 §6).
/// <para>
/// Concurrency (§13). All registry mutations run under a single <c>_gate</c>, so Create/Delete are
/// serialized and duplicates are safe. This class does NOT decide process lifetime — that is the COM
/// server-process reference count (<see cref="Com.ComServerProcess"/>), the correct barrier. Startup
/// rehydration is protected by tombstones: a Delete seen before <see cref="RehydrateFromHost"/> records
/// the id, and rehydration skips it, so a stale GetWidgetInfos snapshot can never resurrect a just-deleted
/// widget (H-2).
/// </para>
/// Every host/reader call is wrapped so no exception escapes toward the COM boundary (§16); a faulty
/// update to one widget never stops the others.
/// </summary>
public sealed class WidgetProviderCoordinator
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IWidgetHost _host;
    private readonly WidgetSnapshotReader _reader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _staleThreshold;
    private readonly IWidgetProviderLog _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, WidgetActivation> _widgets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tombstones = new(StringComparer.Ordinal);
    private bool _rehydrated;
    private volatile bool _shuttingDown;
    private int _inFlightUpdates;
    private readonly ManualResetEventSlim _drained = new(initialState: true);

    /// <summary>Test seam: invoked just before <see cref="Shutdown"/> blocks on the drain, so a test can
    /// prove the wait was entered without a wall-clock assertion.</summary>
    internal Action? DrainWaitEnteredForTesting { get; set; }

    public WidgetProviderCoordinator(
        IWidgetHost host,
        WidgetSnapshotReader? reader = null,
        TimeProvider? timeProvider = null,
        TimeSpan? staleThreshold = null,
        IWidgetProviderLog? log = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _reader = reader ?? new WidgetSnapshotReader();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _staleThreshold = staleThreshold ?? WidgetFreshness.DefaultStaleThreshold;
        _log = log ?? NullWidgetProviderLog.Instance;
    }

    /// <summary>
    /// Marks the coordinator as shutting down and drains any in-flight update, so a late rehydration
    /// continuation is a no-op and, in the common case, no <c>host.Update</c> races the process revoke
    /// (M-1). Must be called on EVERY exit path (in the host process's finally), so a registration failure
    /// or exception also invalidates late work. Idempotent.
    /// <para>
    /// BOUNDED-SHUTDOWN RESIDUAL (documented, accepted): the drain waits at most <see cref="DrainTimeout"/>.
    /// <c>host.Update</c> is a synchronous WinRT call into <c>WidgetManager</c> that cannot be
    /// cooperatively cancelled, so if one is genuinely stuck in the host past the timeout, this returns
    /// (and the process revokes) while that single call is still outstanding; it completes on its own
    /// afterwards. That is harmless — a call to a revoked provider is isolated by the per-update try/catch —
    /// and matches the bounded-shutdown pattern used elsewhere (ADR-018 §30). The wait is event-driven and
    /// the timeout is <see cref="TimeProvider"/>-driven, so it neither busy-spins nor blocks unbounded.
    /// </para>
    /// </summary>
    public void Shutdown()
    {
        _shuttingDown = true;

        if (Volatile.Read(ref _inFlightUpdates) == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(DrainTimeout, _timeProvider);
        DrainWaitEnteredForTesting?.Invoke();
        try
        {
            _drained.Wait(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Bounded-shutdown residual (see remarks): proceed even if a host call is still outstanding.
        }
    }

    /// <summary>Number of widgets the provider currently believes are active.</summary>
    public int ActiveWidgetCount
    {
        get { lock (_gate) { return _widgets.Count; } }
    }

    /// <summary>
    /// On startup, ask the host which widgets already exist and repaint each (§12). A widget already
    /// tombstoned by a Delete is skipped so a stale snapshot cannot resurrect it (H-2). Rehydration runs
    /// once; afterwards tombstones are dropped. Contains any host exception.
    /// </summary>
    public void RehydrateFromHost()
    {
        IReadOnlyList<WidgetActivation> existing;
        try
        {
            existing = _host.GetActiveWidgets();
        }
        catch (Exception exception)
        {
            _log.Warn($"GetWidgetInfos failed on startup. Error: {exception.GetType().Name}.");
            lock (_gate)
            {
                _rehydrated = true;
                _tombstones.Clear();
            }

            return;
        }

        var toPaint = new List<WidgetActivation>();
        lock (_gate)
        {
            // If shutdown was decided while GetWidgetInfos was still running (e.g. it timed out and the
            // process began exiting), this late continuation must be a no-op — never add or repaint
            // widgets as the process revokes (M-1).
            if (_shuttingDown)
            {
                _rehydrated = true;
                _tombstones.Clear();
                return;
            }

            foreach (var widget in existing)
            {
                if (string.IsNullOrEmpty(widget.WidgetId) || _tombstones.Contains(widget.WidgetId))
                {
                    continue; // never re-add a widget deleted since the snapshot was taken
                }

                _widgets[widget.WidgetId] = widget;
                toPaint.Add(widget);
            }

            _rehydrated = true;
            _tombstones.Clear();
        }

        foreach (var widget in toPaint)
        {
            PushUpdate(widget);
        }
    }

    /// <summary>A widget was created/activated/context-changed: register and repaint it.</summary>
    public void OnWidgetActivated(WidgetActivation widget)
    {
        if (string.IsNullOrEmpty(widget.WidgetId))
        {
            return;
        }

        lock (_gate)
        {
            _tombstones.Remove(widget.WidgetId); // it is alive again
            _widgets[widget.WidgetId] = widget;
        }

        PushUpdate(widget);
    }

    /// <summary>The widget's context changed (e.g. resized): update the stored size and repaint.</summary>
    public void OnWidgetContextChanged(WidgetActivation widget) => OnWidgetActivated(widget);

    /// <summary>A widget was deleted: drop it from the registry (serialized with all other mutations).</summary>
    public void OnWidgetDeleted(string? widgetId)
    {
        if (string.IsNullOrEmpty(widgetId))
        {
            return;
        }

        lock (_gate)
        {
            _widgets.Remove(widgetId);

            // Until the one-shot rehydration has run, remember deletions so a stale GetWidgetInfos
            // snapshot cannot re-add this id (H-2). Bounded to the startup window.
            if (!_rehydrated)
            {
                _tombstones.Add(widgetId);
            }
        }
    }

    /// <summary>Repaint every registered widget. Best-effort per widget.</summary>
    public void RefreshAll()
    {
        WidgetActivation[] snapshot;
        lock (_gate)
        {
            snapshot = _widgets.Values.ToArray();
        }

        foreach (var widget in snapshot)
        {
            PushUpdate(widget);
        }
    }

    private void PushUpdate(WidgetActivation widget)
    {
        if (_shuttingDown)
        {
            return; // do not touch the host while the process is exiting
        }

        // In-flight lease: register before doing any work (resetting the drained event on the 0->1 edge),
        // then RE-CHECK the flag. Shutdown() sets the flag and waits on the drained event, so an update
        // already past the lease is ordered before shutdown returns (barring the bounded timeout) and an
        // update attempted after the flag is skipped.
        if (Interlocked.Increment(ref _inFlightUpdates) == 1)
        {
            _drained.Reset();
        }

        try
        {
            if (_shuttingDown)
            {
                return;
            }

            var read = _reader.Read();
            var freshness = WidgetFreshness.Evaluate(read, _timeProvider.GetUtcNow(), _staleThreshold);
            var card = WidgetTemplateBuilder.Build(read, freshness, widget.Size);
            _host.Update(widget.WidgetId, card.TemplateJson, card.DataJson);
        }
        catch (Exception exception)
        {
            // One widget's update failing must not stop the others or reach the COM boundary (§16).
            _log.Warn($"Widget update failed. Error: {exception.GetType().Name}.");
        }
        finally
        {
            if (Interlocked.Decrement(ref _inFlightUpdates) == 0)
            {
                _drained.Set();
            }
        }
    }
}
