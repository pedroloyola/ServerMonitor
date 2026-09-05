using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests.Fakes;

/// <summary>
/// Controllable <see cref="ISnapshotChangeSource"/>: the test decides exactly when a "the snapshot may
/// have changed" signal arrives, so the pump's debounce/coalescing/backstop can be proved without any
/// dependence on real filesystem timing. The real source is covered separately by tests that perform an
/// actual atomic replace on disk.
/// <para>
/// It mirrors the real source's own SERIALIZATION — one internal lock around every lifecycle call, so a
/// <see cref="Stop"/> can never overlap a <see cref="Start"/> — and it can PARK on demand, either inside
/// <see cref="Start"/> or on the first read of <see cref="IsWatching"/>. Those two barriers are what let
/// the arm/disarm ordering be proved with a deterministic interleaving instead of a sleep: the confirmed
/// race is a backstop that reads <see cref="IsWatching"/>, is overtaken by a complete disarm, and only
/// then calls <see cref="Start"/>.
/// </para>
/// </summary>
internal sealed class FakeSnapshotChangeSource : ISnapshotChangeSource
{
    /// <summary>Generous: a barrier is always released by the test, never by elapsed time.</summary>
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Mirrors the real source: every lifecycle call is serialized against the others.</summary>
    private readonly object _gate = new();

    private int _startCount;
    private int _stopCount;
    private int _disposeCount;
    private volatile bool _isWatching;
    private ManualResetEventSlim? _isWatchingBarrier;

    public int StartCount => Volatile.Read(ref _startCount);

    public int StopCount => Volatile.Read(ref _stopCount);

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Simulates a watch that could not be established (missing directory) or that faulted.</summary>
    public bool WatchEstablishes { get; set; } = true;

    /// <summary>Released once per <see cref="Start"/> entry, so a test can wait for one instead of sleeping.</summary>
    public SemaphoreSlim StartEntered { get; } = new(0);

    /// <summary>Released when the first <see cref="IsWatching"/> read parks on its barrier.</summary>
    public SemaphoreSlim IsWatchingEntered { get; } = new(0);

    /// <summary>When set, <see cref="Start"/> parks on it (from <see cref="BlockStartFrom"/> onwards).</summary>
    public ManualResetEventSlim? BlockStart { get; set; }

    /// <summary>1-based ordinal of the first <see cref="Start"/> call that parks on <see cref="BlockStart"/>.</summary>
    public int BlockStartFrom { get; set; } = 1;

    /// <summary>
    /// Parks the FIRST read of <see cref="IsWatching"/> on this event and then clears itself, so the test's
    /// own later assertions read the property freely. This is the seam for the confirmed race: it holds a
    /// caller in the gap between "is a watch established?" and "establish one".
    /// </summary>
    public void ParkFirstIsWatchingRead(ManualResetEventSlim barrier) =>
        Interlocked.Exchange(ref _isWatchingBarrier, barrier);

    public bool IsWatching
    {
        get
        {
            var barrier = Interlocked.Exchange(ref _isWatchingBarrier, null);
            if (barrier is not null)
            {
                IsWatchingEntered.Release();
                barrier.Wait(ParkTimeout);
            }

            return _isWatching;
        }
    }

    public event Action? Changed;

    public void Start()
    {
        var ordinal = Interlocked.Increment(ref _startCount);
        StartEntered.Release();

        lock (_gate)
        {
            // Parking INSIDE the lifecycle lock is what the real source does with its OS call: a Stop
            // arriving now queues behind this Start rather than slipping past it.
            if (ordinal >= BlockStartFrom)
            {
                BlockStart?.Wait(ParkTimeout);
            }

            _isWatching = WatchEstablishes;
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _stopCount);
        lock (_gate)
        {
            _isWatching = false;
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        lock (_gate)
        {
            _isWatching = false;
        }
    }

    /// <summary>Raises one change signal, exactly as the real watcher would.</summary>
    public void Raise() => Changed?.Invoke();

    /// <summary>Raises <paramref name="count"/> signals back to back — one atomic commit's burst.</summary>
    public void RaiseBurst(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Raise();
        }
    }

    /// <summary>
    /// The real source's fault path: an internal-buffer overflow leaves the watch not delivering, so
    /// <see cref="IsWatching"/> goes false and one unconditional "re-read" signal is raised.
    /// </summary>
    public void Fault()
    {
        _isWatching = false;
        Raise();
    }

    /// <summary>True while a handler is still subscribed (proves the pump unhooked on dispose).</summary>
    public bool HasSubscribers => Changed is not null;
}
