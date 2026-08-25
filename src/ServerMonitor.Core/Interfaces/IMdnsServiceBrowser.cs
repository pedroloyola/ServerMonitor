using ServerMonitor.Core.Discovery;

namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// Deterministic, fakeable seam over the underlying mDNS browser. The production adapter wraps
/// the third-party library (Tmds.MDns) and maps its raw announcements into validated
/// <see cref="DiscoveryObservation"/> values; a test fake can raise the same three events on
/// demand to drive the runtime store without any network. The seam intentionally exposes only
/// Found / Updated / Removed plus start/stop — no library types leak across it.
/// </summary>
public interface IMdnsServiceBrowser
{
    /// <summary>Raised when an instance is seen for the first time on an interface.</summary>
    event EventHandler<DiscoveryObservation> Found;

    /// <summary>Raised when an already-seen instance re-announces or its data changes.</summary>
    event EventHandler<DiscoveryObservation> Updated;

    /// <summary>Raised when an instance sends a goodbye / disappears on an interface.</summary>
    event EventHandler<DiscoveryObservation> Removed;

    /// <summary>Begins passive browsing for the configured service type. Idempotent.</summary>
    void Start();

    /// <summary>Stops browsing and releases the underlying browser. Idempotent.</summary>
    void Stop();
}
