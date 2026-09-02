namespace ServerMonitor.App.Services;

/// <summary>
/// Says whether this session lost its exit affordance, so the UI can explain itself (M13 S2 §13).
/// <para>
/// The degradation surfaces a window the user never asked for. Doing that silently is the thing Prism
/// rejected: the window has to arrive with the reason — the notification-area icon is unavailable, so in
/// this session closing the window quits, and the saved background preference was NOT changed.
/// </para>
/// </summary>
public interface IBackgroundDegradationNotice
{
    event EventHandler? Changed;

    /// <summary>True once this session has no usable notification-area icon.</summary>
    bool IsDegraded { get; }

    /// <summary>Records the degradation. Idempotent; raises <see cref="Changed"/> only on the transition.</summary>
    void Raise();
}

/// <summary>
/// Session-scoped, in memory, and deliberately NOT persisted: it describes what happened to THIS process,
/// not a user preference. The stored `BackgroundMonitoringEnabled` is never rewritten by a degradation.
/// </summary>
public sealed class BackgroundDegradationNotice : IBackgroundDegradationNotice
{
    private readonly object _sync = new();
    private bool _isDegraded;

    public event EventHandler? Changed;

    public bool IsDegraded
    {
        get { lock (_sync) { return _isDegraded; } }
    }

    public void Raise()
    {
        lock (_sync)
        {
            if (_isDegraded)
            {
                return;
            }

            _isDegraded = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
