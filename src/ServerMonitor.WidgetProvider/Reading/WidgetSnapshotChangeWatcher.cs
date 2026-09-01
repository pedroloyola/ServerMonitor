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
/// </summary>
public sealed class WidgetSnapshotChangeWatcher : IWidgetRefreshPump
{
    /// <summary>Window that collapses one commit's burst of filesystem events into a single repaint.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Safety re-read cadence for lost events. Well below the 90 s stale threshold.</summary>
    public static readonly TimeSpan DefaultBackstopInterval = TimeSpan.FromSeconds(60);

    private readonly Action _refresh;
    private readonly ISnapshotChangeSource _source;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _backstopInterval;
    private readonly IWidgetProviderLog _log;

    private readonly object _gate = new();
    private readonly ITimer _debounceTimer;
    private readonly ITimer _backstopTimer;

    private bool _armed;
    private bool _debouncePending;
    private bool _running;
    private bool _dirty;
    private bool _disposed;

    public WidgetSnapshotChangeWatcher(
        Action refresh,
        ISnapshotChangeSource? source = null,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        TimeSpan? backstopInterval = null,
        IWidgetProviderLog? log = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _log = log ?? NullWidgetProviderLog.Instance;
        _source = source ?? new FileSystemSnapshotChangeSource(log: _log);
        _debounce = debounce ?? DefaultDebounce;
        _backstopInterval = backstopInterval ?? DefaultBackstopInterval;

        var time = timeProvider ?? TimeProvider.System;

        // Both timers are created once and rescheduled, never recreated per event.
        _debounceTimer = time.CreateTimer(
            _ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _backstopTimer = time.CreateTimer(
            _ => OnBackstopElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _source.Changed += OnSourceChanged;
    }

    /// <summary>True while the pump is watching. Test/diagnostic surface.</summary>
    public bool IsArmed
    {
        get { lock (_gate) { return _armed; } }
    }

    public void Arm()
    {
        lock (_gate)
        {
            if (_disposed || _armed)
            {
                return;
            }

            _armed = true;
            _backstopTimer.Change(_backstopInterval, _backstopInterval);
        }

        // Outside the gate: Start() touches the OS and raises nothing synchronously, so there is no reason
        // to order an OS call inside our own critical section.
        _source.Start();
    }

    public void Disarm()
    {
        lock (_gate)
        {
            if (_disposed || !_armed)
            {
                return;
            }

            _armed = false;
            _debouncePending = false;
            // A refresh already in flight finishes its current pass; the loop re-checks _armed and stops.
            _dirty = false;
            _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _backstopTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _source.Stop();
    }

    /// <summary>
    /// Deterministic teardown: disarm, unhook the source, and dispose both timers, so no callback can run
    /// after the provider decides to exit (ADR-018 §30). Idempotent; never throws.
    /// </summary>
    public void Dispose()
    {
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
            SafeRun(() => _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
            SafeRun(() => _backstopTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        }

        _source.Changed -= OnSourceChanged;
        SafeRun(() => _source.Dispose());
        SafeRun(() => _debounceTimer.Dispose());
        SafeRun(() => _backstopTimer.Dispose());
    }

    /// <summary>
    /// Test seam: drives the same entry point both timers use, so the overlap guard can be proved without
    /// racing two clock advances against each other.
    /// </summary>
    internal void TriggerForTesting() => RunRefresh();

    /// <summary>A filesystem signal: schedule one refresh, absorbing everything inside the window.</summary>
    private void OnSourceChanged()
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

    private void OnDebounceElapsed()
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

    private void OnBackstopElapsed()
    {
        lock (_gate)
        {
            if (_disposed || !_armed)
            {
                return;
            }
        }

        // Re-establish a watch that never started (the directory did not exist yet) or that faulted on an
        // overflow. Idempotent when already healthy.
        if (!_source.IsWatching)
        {
            _source.Start();
        }

        RunRefresh();
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
