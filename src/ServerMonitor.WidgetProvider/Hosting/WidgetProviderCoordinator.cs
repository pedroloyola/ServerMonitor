using ServerMonitor.WidgetContract;
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

    /// <summary>
    /// Widgets the host has shown interest in receiving updates for — the ones worth repainting on our
    /// own initiative. Deliberately separate from <see cref="_widgets"/>: a widget stays REGISTERED across
    /// a Deactivate (it still exists, it is just not being viewed) but stops driving the repaint pump, so
    /// the provider goes idle once the board is closed.
    /// </summary>
    private readonly HashSet<string> _onScreen = new(StringComparer.Ordinal);

    private readonly IWidgetRefreshPump? _pump;

    /// <summary>
    /// Serializes pump transitions, held ACROSS the decision so two concurrent callbacks can never settle
    /// on a stale answer (one reads 0 and is about to disarm, another reads 1 and arms, then the first
    /// disarms a pump that must be running). Lock order is always <c>_pumpGate</c> then <see cref="_gate"/>;
    /// nothing takes them the other way round.
    /// </summary>
    private readonly object _pumpGate = new();

    private bool _rehydrated;
    private volatile bool _shuttingDown;
    private int _inFlightUpdates;
    private readonly ManualResetEventSlim _drained = new(initialState: true);

    /// <summary>Test seam: invoked just before <see cref="Shutdown"/> blocks on the drain, so a test can
    /// prove the wait was entered without a wall-clock assertion.</summary>
    internal Action? DrainWaitEnteredForTesting { get; set; }

    /// <param name="pumpFactory">
    /// Builds the repaint pump from the refresh callback it must drive. Optional so the coordinator can be
    /// constructed without one for tests of pure callback handling; production always supplies it — see
    /// <see cref="CreateWithFileSystemPump"/>.
    /// </param>
    public WidgetProviderCoordinator(
        IWidgetHost host,
        WidgetSnapshotReader? reader = null,
        TimeProvider? timeProvider = null,
        TimeSpan? staleThreshold = null,
        IWidgetProviderLog? log = null,
        Func<Action, IWidgetRefreshPump>? pumpFactory = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _reader = reader ?? new WidgetSnapshotReader();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _staleThreshold = staleThreshold ?? WidgetFreshness.DefaultStaleThreshold;
        _log = log ?? NullWidgetProviderLog.Instance;

        // Only a delegate is handed over here; the pump calls nothing back until it is armed.
        _pump = pumpFactory?.Invoke(RefreshAll);
    }

    /// <summary>
    /// THE production composition (M13 QA-9): a coordinator wired to a repaint pump that watches the same
    /// snapshot file the reader reads. Both are built from one path so the watched directory and the read
    /// file can never drift apart.
    /// <para>
    /// This is the runtime caller of <see cref="RefreshAll"/> that the provider was missing: the Widgets
    /// host is not an update pump, so without it a widget on an open board never repaints, however fresh
    /// <c>widget-state.json</c> becomes. The pump reads that one file — it opens no SSH, does not talk to
    /// the app, and asks the monitoring engine for nothing (ADR-018 §6/§14).
    /// </para>
    /// </summary>
    public static WidgetProviderCoordinator CreateWithFileSystemPump(
        IWidgetHost host,
        string? snapshotPath = null,
        TimeProvider? timeProvider = null,
        IWidgetProviderLog? log = null,
        TimeSpan? debounce = null,
        TimeSpan? backstopInterval = null)
    {
        var path = snapshotPath ?? WidgetStateLocation.ForCurrentUser();
        var reader = new WidgetSnapshotReader(path, timeProvider: timeProvider, log: log);

        return new WidgetProviderCoordinator(
            host,
            reader,
            timeProvider,
            staleThreshold: null,
            log: log,
            pumpFactory: refresh => new WidgetSnapshotChangeWatcher(
                refresh,
                new FileSystemSnapshotChangeSource(path, log),
                timeProvider,
                debounce,
                backstopInterval,
                log));
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

        // Deterministic teardown BEFORE the drain: stop the pump so neither a timer nor a filesystem
        // callback can start a new repaint while we unwind (§30). Disposal is idempotent and contained.
        if (_pump is not null)
        {
            lock (_pumpGate)
            {
                try
                {
                    _pump.Disarm();
                    _pump.Dispose();
                }
                catch (Exception exception)
                {
                    _log.Warn($"Widget repaint pump teardown failed. Error: {exception.GetType().Name}.");
                }
            }
        }

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
    /// <para>
    /// <b>POLICY: recovered widgets count as on screen, so rehydration arms the repaint pump.</b> The
    /// Windows App SDK exposes NO activation state on <c>WidgetInfo</c>/<c>WidgetContext</c>, and nothing
    /// in the widget-provider contract promises an <c>Activate</c> after a provider is relaunched with the
    /// board already open — the documentation only says <c>Activate</c>/<c>Deactivate</c> mark transitions
    /// of host interest, and that a widget is already active after <c>CreateWidget</c>. So the provider
    /// cannot ask, and cannot assume. The two ways to be wrong are not symmetric: assuming NOT active
    /// silently reproduces the QA-9 defect (a visible widget that never repaints, with no signal that
    /// anything is wrong), while assuming active costs at most one file re-read per snapshot commit for as
    /// long as the host keeps this process alive — and the host releases the provider, which then exits on
    /// its idle grace, once it stops caring. This class therefore takes the fail-safe direction and lets
    /// the normal state machine correct it: the host's first <c>Deactivate</c> (or the widget's deletion)
    /// disarms the pump as usual. Flipping the policy is one line — do not add the id to
    /// <see cref="_onScreen"/> here — and both behaviors are covered by tests.
    /// </para>
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

                // POLICY (see remarks on RehydrateFromHost): a recovered widget counts as on screen.
                _onScreen.Add(widget.WidgetId);
                toPaint.Add(widget);
            }

            _rehydrated = true;
            _tombstones.Clear();
        }

        SyncPump();

        foreach (var widget in toPaint)
        {
            PushUpdate(widget);
        }
    }

    /// <summary>
    /// A widget was created/activated/context-changed: register it, mark it on screen, repaint it.
    /// <para>
    /// All three callbacks mean the host wants content for this widget. <c>CreateWidget</c> is included
    /// deliberately: per the Windows App SDK documentation, "when a widget is first created, as indicated
    /// by a call to CreateWidget, it is in the active state" — the host does NOT follow it with an
    /// <c>Activate</c>, so treating create as an activation is required, not merely convenient.
    /// </para>
    /// </summary>
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
            _onScreen.Add(widget.WidgetId);
        }

        SyncPump(); // the first on-screen widget starts the pump; already armed is a no-op
        PushUpdate(widget);
    }

    /// <summary>The widget's context changed (e.g. resized): update the stored size and repaint.</summary>
    public void OnWidgetContextChanged(WidgetActivation widget) => OnWidgetActivated(widget);

    /// <summary>
    /// The host is no longer requesting content for this widget (the board closed, or it scrolled out of
    /// view). The widget still EXISTS, so the registry is intentionally left alone — but it stops counting
    /// towards the repaint pump, and when the last one goes the pump disarms and the provider does no
    /// periodic work at all.
    /// </summary>
    public void OnWidgetDeactivated(string? widgetId)
    {
        if (string.IsNullOrEmpty(widgetId))
        {
            return;
        }

        lock (_gate)
        {
            _onScreen.Remove(widgetId);
        }

        SyncPump();
    }

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
            _onScreen.Remove(widgetId);

            // Until the one-shot rehydration has run, remember deletions so a stale GetWidgetInfos
            // snapshot cannot re-add this id (H-2). Bounded to the startup window.
            if (!_rehydrated)
            {
                _tombstones.Add(widgetId);
            }
        }

        SyncPump();
    }

    /// <summary>Number of widgets currently on screen (the pump runs exactly while this is above zero).</summary>
    public int OnScreenWidgetCount
    {
        get { lock (_gate) { return _onScreen.Count; } }
    }

    /// <summary>
    /// Arms the pump exactly while at least one widget is on screen. Called after every membership change;
    /// Arm and Disarm are both idempotent, so it is the transition that matters, not the count. The whole
    /// decision is taken under <see cref="_pumpGate"/> so concurrent callbacks cannot settle on a stale
    /// answer, and the pump is never touched while <see cref="_gate"/> is held.
    /// </summary>
    private void SyncPump()
    {
        if (_pump is null)
        {
            return;
        }

        lock (_pumpGate)
        {
            bool shouldRun;
            lock (_gate)
            {
                shouldRun = !_shuttingDown && _onScreen.Count > 0;
            }

            try
            {
                if (shouldRun)
                {
                    _pump.Arm();
                }
                else
                {
                    _pump.Disarm();
                }
            }
            catch (Exception exception)
            {
                // The pump must never be able to break widget handling or reach the COM boundary (§16).
                _log.Warn($"Widget repaint pump state change failed. Error: {exception.GetType().Name}.");
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
            var now = _timeProvider.GetUtcNow();
            var strings = WidgetStrings.Current();
            var viewModel = WidgetViewModelBuilder.Build(read, widget.Size, now, strings, _staleThreshold);
            var card = WidgetCardRenderer.Render(viewModel);
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
