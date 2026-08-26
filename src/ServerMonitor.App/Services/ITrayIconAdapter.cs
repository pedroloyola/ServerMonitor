namespace ServerMonitor.App.Services;

/// <summary>
/// Platform boundary for the single Windows notification-area icon. Implementations own
/// their native resources and must tolerate repeated start/stop requests.
/// </summary>
public interface ITrayIconAdapter
{
    event EventHandler? OpenRequested;

    event EventHandler? RefreshAllRequested;

    event EventHandler? ToggleCompactRequested;

    event EventHandler? SettingsRequested;

    event EventHandler? ExitRequested;

    void Start();

    /// <summary>
    /// Removes the icon immediately. The main window calls this on the UI thread before
    /// the synchronous host shutdown boundary starts.
    /// </summary>
    void StopSynchronously();

    /// <summary>UI-dispatched fallback used when host shutdown was not window initiated.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
