using ServerMonitor.Core.Discovery;

namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// High-level, UI-facing view of passive local network discovery. The UI observes
/// <see cref="DiscoveredChanged"/> (raised only on material changes, never on every mDNS
/// packet) and pulls the current suggestions with <see cref="GetDiscovered"/>. Ignoring hides
/// the current suggestion; resetting re-reveals still-present devices. This contract exposes no
/// SSH, credential, trust or metric surface — discovery is a suggestion source only.
/// </summary>
public interface IServerDiscoveryService
{
    /// <summary>Raised on the background discovery thread when the visible suggestion set materially changes.</summary>
    event EventHandler DiscoveredChanged;

    /// <summary>Current visible suggestions (excludes ignored identities), capped and ordered deterministically.</summary>
    IReadOnlyList<DiscoveredService> GetDiscovered();

    /// <summary>Ignores an identity so it stops being suggested, and persists the decision.</summary>
    Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Clears all ignored decisions; devices still present become visible suggestions again.</summary>
    Task ResetIgnoredAsync(CancellationToken cancellationToken = default);
}
