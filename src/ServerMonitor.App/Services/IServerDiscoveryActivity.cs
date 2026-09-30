namespace ServerMonitor.App.Services;

/// <summary>
/// M14.5 — whether local network discovery is ACTUALLY running, for the dashboard's empty state
/// ("Looking for SSH servers on your local network…"). Implemented only by the real discovery service:
/// the inert default and the QA stand-ins do not implement it, so a composition without live discovery
/// never shows a search indicator.
/// </summary>
public interface IServerDiscoveryActivity
{
    /// <summary>True from a successful start of the mDNS browser until it is stopped.</summary>
    bool IsSearching { get; }

    /// <summary>Raised on the thread that started or stopped discovery, after <see cref="IsSearching"/> changed.</summary>
    event EventHandler? IsSearchingChanged;
}
