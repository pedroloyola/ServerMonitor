using ServerMonitor.Core.Discovery;

namespace ServerMonitor.App.Services;

/// <summary>
/// M14.5 — whether local network discovery is ACTUALLY running, for the dashboard's empty state
/// ("Looking for SSH servers on your local network…"). Implemented only by the real discovery service and the
/// on-demand <c>--qa-discovery</c> stand-in: the inert default and the other QA stand-ins do not implement it, so
/// a composition without live discovery never shows a search indicator or a search action.
/// <para>
/// REL.0B (UI9-FW-PROMPT): the host never starts discovery. Starting the mDNS browser binds UDP listeners on
/// 0.0.0.0:5353 and [::]:5353, which is what makes Windows Firewall ask for network permission, so the ONLY way
/// in is <see cref="StartSearchAsync"/>, from an explicit user action. <see cref="StopSearchAsync"/> runs when the
/// window goes to the background; the host stops it on exit.
/// </para>
/// </summary>
public interface IServerDiscoveryActivity
{
    /// <summary>True from a successful start of the mDNS browser until it is stopped.</summary>
    bool IsSearching { get; }

    /// <summary>
    /// Raised on the thread that started or stopped discovery, after <see cref="IsSearching"/> or
    /// <see cref="LastSearchEnd"/> changed. On a stop it is raised only after the suggestions are cleared.
    /// </summary>
    event EventHandler? IsSearchingChanged;

    /// <summary>How the last search ended; <see cref="DiscoverySearchEnd.None"/> while one runs or before the first.</summary>
    DiscoverySearchEnd LastSearchEnd { get; }

    /// <summary>
    /// Starts searching the local network. Explicit user action ONLY. Idempotent while searching; a failed start
    /// leaves <see cref="IsSearching"/> false and does not throw (cancellation excepted).
    /// </summary>
    Task StartSearchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// REL.0D O-7: as <see cref="StartSearchAsync(CancellationToken)"/>, but only a suggestion
    /// <paramref name="isUsable"/> accepts (one the user could still add, e.g. not a server already configured) keeps the
    /// search past <see cref="DiscoveryOptions.NothingFoundTimeout"/>. Called under the service's store lock on a
    /// background thread: it must be cheap, lock-free and thread-safe. A stand-in without the bound ignores it.
    /// </summary>
    Task StartSearchAsync(Func<DiscoveredService, bool> isUsable, CancellationToken cancellationToken = default) =>
        StartSearchAsync(cancellationToken);

    /// <summary>Stops searching. Idempotent; a later explicit action may start it again.</summary>
    Task StopSearchAsync(CancellationToken cancellationToken = default);
}
