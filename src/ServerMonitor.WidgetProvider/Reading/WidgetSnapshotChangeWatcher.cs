using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>
/// The repaint pump (M13 QA-9): turns "the snapshot file changed" into exactly one repaint, for as long
/// as at least one widget is on screen.
/// <para>
/// <b>Coalescing.</b> One atomic commit legitimately produces several filesystem events (temp created,
/// temp renamed onto the destination, destination renamed to the backup, backup deleted). The first
/// signal in a quiet period schedules a refresh one debounce window later, and every further signal
/// inside that window is absorbed. The window is deliberately NOT restarted by later signals, so a
/// continuous stream can never starve the refresh: one commit yields one logical repaint, and a burst
/// yields one repaint plus at most one more for whatever arrived after the window closed.
/// </para>
/// <para>
/// <b>Backstop.</b> Filesystem events can be lost outright — an internal-buffer overflow, or a watch that
/// never established because the directory did not exist yet. A low-frequency re-read runs while armed so
/// the widget converges even with a dead watcher, and each tick also retries establishing the watch. The
/// timer is pushed out after every refresh, so it fires only when nothing else has happened for a whole
/// interval. This is a file re-read, not a monitoring cycle: it opens no SSH and asks the app for nothing
/// (ADR-018 §14/§15).
/// </para>
/// <para>
/// <b>Serialization.</b> Refreshes never overlap and a trigger is never lost. A filesystem signal that
/// arrives mid-repaint opens the next debounce window as usual, so the newer bytes reach the board one
/// window later; and if the two triggers themselves collide — a window closing while a backstop re-read
/// is running, or the reverse — the later one marks the pump dirty and the running pass loops once more
/// rather than painting in parallel or being dropped. All timing is driven by an injected
/// <see cref="TimeProvider"/>, and every callback is failure-isolated: the pump can never fault the COM
/// server (§16).
/// </para>
/// <para>
/// <b>The watcher never outlives the armed state.</b> Arming, disarming and the backstop's re-arm all
/// touch the OS outside the state lock (a <c>Start</c> that blocks must not block widget callbacks), so
/// they are ordered against each other by a second lock plus a monotonic generation stamped on every
/// arm/disarm/dispose decision. A decision only reaches the <see cref="ISnapshotChangeSource"/> while its
/// generation is still the current one, and a <c>Start</c> whose decision goes stale WHILE it runs is
/// undone immediately. Without that, a backstop could read <c>IsWatching == false</c>, be overtaken by a
/// complete <see cref="Disarm"/>, and only then call <c>Start</c> — leaving a live FileSystemWatcher
/// behind a disarmed pump.
/// </para>
/// <para>
/// <b>Disposal drains.</b> <see cref="Dispose"/> does not return while a timer, source or refresh callback
/// is still running, so "nothing repaints after Shutdown" is a guarantee rather than a hope. The wait is
/// event-driven and bounded by <see cref="DefaultDrainTimeout"/> on the injected clock — the same
/// bounded-shutdown shape the coordinator uses (§30) — and is skipped when Dispose is itself called from
/// inside a pump callback, which would otherwise wait on the calling thread.
/// </para>
/// </summary>
public sealed class WidgetSnapshotChangeWatcher : IWidgetRefreshPump
{
    /// <summary>Window that collapses one commit's burst of filesystem events into a single repaint.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Safety re-read cadence for lost events. Well below the 90 s stale threshold.</summary>
    public static readonly TimeSpan DefaultBackstopInterval = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound on the disposal drain, matching the coordinator's own shutdown budget.</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True while THIS thread is inside a pump callback. Dispose consults it so a callback that disposes
    /// its own pump (a repaint that decides to shut the provider down) never waits for itself.
    /// </summary>
    [ThreadStatic]
    private static bool s_inCallback;

    private readonly Action _refresh;
    private readonly ISnapshotChangeSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _backstopInterval;
    private readonly TimeSpan _drainTimeout;
    private readonly IWidgetProviderLog _log;

    /// <summary>Guards all pump state. Never held across a call into the source or the refresh delegate.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// Serializes the OS-facing lifecycle calls (<c>Start</c>/<c>Stop</c>/<c>Dispose</c> on the source) and
    /// is held ACROSS them, so two decisions can never be applied out of order. Lock order is always
    /// <c>_sourceGate</c> then <see cref="_gate"/>; nothing takes them the other way round.
    /// </summary>
    private readonly object _sourceGate = new();

    private readonly ITimer _debounceTimer;
    private readonly ITimer _backstopTimer;

    /// <summary>Set while no callback is in flight, so disposal can wait on it instead of polling.</summary>
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    private bool _armed;
    private bool _debouncePending;
    private bool _running;
    private bool _dirty;
    private bool _disposed;
    private int _activeCallbacks;

    /// <summary>Bumped by every arm/disarm/dispose decision; identifies which decision may touch the OS.</summary>
    private long _generation;

    public WidgetSnapshotChangeWatcher(
        Action refresh,
        ISnapshotChangeSource? source = null,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        TimeSpan? backstopInterval = null,
        IWidgetProviderLog? log = null,
        TimeSpan? drainTimeout = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _log = log ?? NullWidgetProviderLog.Instance;
        _source = source ?? new FileSystemSnapshotChangeSource(log: _log);
        _debounce = debounce ?? DefaultDebounce;
        _backstopInterval = backstopInterval ?? DefaultBackstopInterval;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Both timers are created once and rescheduled, never recreated per event.
        _debounceTimer = _timeProvider.CreateTimer(
            _ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _backstopTimer = _timeProvider.CreateTimer(
            _ => OnBackstopElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _source.Changed += OnSourceChanged;
    }

    /// <summary>True while the pump is watching. Test/diagnostic surface.</summary>
    public bool IsArmed
    {
        get { lock (_gate) { return _armed; } }
    }

    /// <summary>
    /// Test seam: invoked just before <see cref="Dispose"/> blocks on the callback drain, so a test can
    /// prove the wait was entered — and drive what happens during it — without a wall-clock assertion.
    /// </summary>
    internal Action? DrainWaitEnteredForTesting { get; set; }

    public void Arm()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || _armed)
            {
                return;
            }

            _armed = true;
            generation = ++_generation;
            _backstopTimer.Change(_backstopInterval, _backstopInterval);
        }

        // Outside the gate: Start() touches the OS and must not block widget callbacks. Ordering against a
        // concurrent Disarm/Dispose is the generation's job, not the state lock's.
        ApplySourceState(generation, shouldWatch: true);
    }

    public void Disarm()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || !_armed)
            {
                return;
            }

            _armed = false;
            generation = ++_generation;
            _debouncePending = false;
            // A refresh already in flight finishes its current pass; the loop re-checks _armed and stops.
            _dirty = false;
            _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _backstopTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        ApplySourceState(generation, shouldWatch: false);
    }

    /// <summary>
    /// Deterministic teardown: disarm, unhook and dispose the source, DRAIN every callback still running,
    /// and dispose both timers — so no callback can run after the provider decides to exit (ADR-018 §30).
    /// Idempotent; never throws.
    /// <para>
    /// BOUNDED-SHUTDOWN RESIDUAL (documented, accepted): the drain waits at most
    /// <see cref="DefaultDrainTimeout"/>. The refresh delegate ends in a synchronous WinRT call into the
    /// widget host that cannot be cooperatively cancelled, so if one is genuinely stuck past the timeout
    /// this returns while that single pass is still outstanding; it completes on its own afterwards and is
    /// isolated by the coordinator's per-update try/catch. The wait is event-driven and the timeout is
    /// <see cref="TimeProvider"/>-driven, so it neither busy-spins nor blocks unbounded.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        bool drain;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _armed = false;
            _debouncePending = false;
            _dirty = false;
            _generation++;
            SafeRun(() => _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
            SafeRun(() => _backstopTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));

            // No callback can be ENTERED once _disposed is set under this gate, so the count read here is
            // final: from now on it only falls.
            drain = _activeCallbacks > 0;
        }

        _source.Changed -= OnSourceChanged;

        // Ordered against an in-flight Start: whoever holds _sourceGate finishes first, and the generation
        // bumped above makes the loser a no-op.
        lock (_sourceGate)
        {
            SafeRun(() => _source.Dispose());
        }

        if (drain && !s_inCallback)
        {
            WaitForCallbacksToDrain();
        }

        // After the drain nothing is inside a timer callback, so disposing them races with nothing.
        SafeRun(() => _debounceTimer.Dispose());
        SafeRun(() => _backstopTimer.Dispose());
    }

    /// <summary>
    /// Test seam: drives the same entry point both timers use, so the overlap guard can be proved without
    /// racing two clock advances against each other.
    /// </summary>
    internal void TriggerForTesting()
    {
        if (!EnterCallback())
        {
            return;
        }

        try
        {
            RunRefresh();
        }
        finally
        {
            ExitCallback();
        }
    }

    /// <summary>A filesystem signal: schedule one refresh, absorbing everything inside the window.</summary>
    private void OnSourceChanged()
    {
        // Leased like every other callback: the debounce timer must not be disposed underneath this.
        if (!EnterCallback())
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_disposed || !_armed || _debouncePending)
                {
                    return;
                }

                _debouncePending = true;
                _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            ExitCallback();
        }
    }

    private void OnDebounceElapsed()
    {
        if (!EnterCallback())
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                _debouncePending = false;
                if (_disposed || !_armed)
                {
                    return;
                }
            }

            RunRefresh();
        }
        finally
        {
            ExitCallback();
        }
    }

    private void OnBackstopElapsed()
    {
        if (!EnterCallback())
        {
            return;
        }

        try
        {
            long generation;
            lock (_gate)
            {
                if (_disposed || !_armed)
                {
                    return;
                }

                generation = _generation;
            }

            // Re-establish a watch that never started (the directory did not exist yet) or that faulted on
            // an overflow. Idempotent when already healthy, inert once this decision has gone stale.
            EnsureSourceWatching(generation);
            RunRefresh();
        }
        finally
        {
            ExitCallback();
        }
    }

    /// <summary>
    /// Applies one arm/disarm decision to the source. Held across the OS call so decisions are applied in
    /// order, guarded by the generation so a superseded decision never touches the source, and — for
    /// <c>Start</c> — re-validated afterwards, because the decision can go stale WHILE the OS call runs.
    /// </summary>
    private void ApplySourceState(long generation, bool shouldWatch)
    {
        lock (_sourceGate)
        {
            if (IsStale(generation))
            {
                return; // a newer decision exists; it applies itself under this same lock
            }

            if (!shouldWatch)
            {
                _source.Stop();
                return;
            }

            _source.Start();

            if (IsStale(generation))
            {
                // Disarmed (or disposed) while Start ran: undo it here rather than leaving a live watcher
                // behind a disarmed pump.
                SafeRun(() => _source.Stop());
            }
        }
    }

    /// <summary>Backstop re-arm, under the same ordering rules as <see cref="ApplySourceState"/>.</summary>
    private void EnsureSourceWatching(long generation)
    {
        lock (_sourceGate)
        {
            if (IsStale(generation) || _source.IsWatching)
            {
                return;
            }

            _source.Start();

            if (IsStale(generation))
            {
                SafeRun(() => _source.Stop());
            }
        }
    }

    /// <summary>True once a newer decision has superseded <paramref name="generation"/>.</summary>
    private bool IsStale(long generation)
    {
        lock (_gate)
        {
            return _disposed || generation != _generation;
        }
    }

    /// <summary>
    /// Runs the refresh at most once at a time, without losing a trigger that arrives mid-refresh, and
    /// pushes the backstop out so it only fires when nothing else has.
    /// </summary>
    private void RunRefresh()
    {
        lock (_gate)
        {
            if (_disposed || !_armed)
            {
                return;
            }

            _dirty = true;
            if (_running)
            {
                return; // the in-flight refresh will observe _dirty and loop
            }

            _running = true;
        }

        while (true)
        {
            lock (_gate)
            {
                if (!_dirty || _disposed || !_armed)
                {
                    _running = false;
                    if (_armed && !_disposed)
                    {
                        _backstopTimer.Change(_backstopInterval, _backstopInterval);
                    }

                    return;
                }

                _dirty = false;
            }

            try
            {
                _refresh();
            }
            catch (Exception exception)
            {
                // One failed repaint pass must not kill the pump; the next signal or backstop retries.
                _log.Warn($"Widget repaint pump failed. Error: {exception.GetType().Name}.");
            }
        }
    }

    /// <summary>
    /// Takes an in-flight lease for a callback. False once disposal has begun — the callback must then do
    /// nothing at all, which is what makes "no callback runs after Dispose returns" true for late arrivals
    /// as well as for the ones the drain waits on.
    /// </summary>
    private bool EnterCallback()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (++_activeCallbacks == 1)
            {
                _idle.Reset();
            }
        }

        s_inCallback = true;
        return true;
    }

    private void ExitCallback()
    {
        s_inCallback = false;
        lock (_gate)
        {
            if (--_activeCallbacks == 0)
            {
                _idle.Set();
            }
        }
    }

    private void WaitForCallbacksToDrain()
    {
        try
        {
            using var timeout = new CancellationTokenSource(_drainTimeout, _timeProvider);
            DrainWaitEnteredForTesting?.Invoke();
            _idle.Wait(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Bounded-shutdown residual (see remarks on Dispose): proceed with the pass still outstanding.
            _log.Warn("Widget repaint pump drain timed out; proceeding with shutdown.");
        }
        catch (Exception exception)
        {
            _log.Warn($"Widget repaint pump drain failed. Error: {exception.GetType().Name}.");
        }
    }

    private void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _log.Warn($"Widget repaint pump teardown failed. Error: {exception.GetType().Name}.");
        }
    }
}
