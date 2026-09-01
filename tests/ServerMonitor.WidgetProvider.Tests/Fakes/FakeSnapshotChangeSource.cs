using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests.Fakes;

/// <summary>
/// Controllable <see cref="ISnapshotChangeSource"/>: the test decides exactly when a "the snapshot may
/// have changed" signal arrives, so the pump's debounce/coalescing/backstop can be proved without any
/// dependence on real filesystem timing. The real source is covered separately by tests that perform an
/// actual atomic replace on disk.
/// </summary>
internal sealed class FakeSnapshotChangeSource : ISnapshotChangeSource
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }

    /// <summary>Simulates a watch that could not be established (missing directory) or that faulted.</summary>
    public bool WatchEstablishes { get; set; } = true;

    public bool IsWatching { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        StartCount++;
        IsWatching = WatchEstablishes;
    }

    public void Stop()
    {
        StopCount++;
        IsWatching = false;
    }

    public void Dispose()
    {
        DisposeCount++;
        IsWatching = false;
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

    /// <summary>True while a handler is still subscribed (proves the pump unhooked on dispose).</summary>
    public bool HasSubscribers => Changed is not null;
}
