namespace ServerMonitor.App.Services;

/// <summary>Process-only activation fact, safe before the window graph is constructed.</summary>
public sealed class ActivationLatch
{
    private int _recorded;
    public bool IsRecorded => Volatile.Read(ref _recorded) != 0;
    public void Record() => Interlocked.Exchange(ref _recorded, 1);
}
