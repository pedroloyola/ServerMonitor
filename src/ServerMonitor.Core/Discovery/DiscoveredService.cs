using System.Net;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Discovery;

/// <summary>
/// Immutable, UI-facing snapshot of one discovered SSH-advertising service, merged across
/// every network interface it was seen on. Purely in-memory and transient: never persisted,
/// never carries TXT data, credentials, trust, SSH transport details, metrics or an
/// operating-system guess. Discovery is only a suggestion — a discovered service is never
/// trusted or connected automatically; the user still adds it through the normal SSH trust flow.
/// </summary>
public sealed record DiscoveredService
{
    /// <summary>
    /// Stable per-session handle for this discovery, distinct from the identity hash. Lets the UI
    /// key a suggestion for its lifetime without reasoning about the underlying mDNS identity.
    /// </summary>
    public required Guid DiscoveryId { get; init; }

    /// <summary>How this service was discovered. Always <see cref="DiscoverySource.Mdns"/> for now.</summary>
    public required DiscoverySource Source { get; init; }

    public required ServiceInstanceIdentity Identity { get; init; }

    /// <summary>Human-facing instance name to show as the suggestion title.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Canonical, fully-qualified resolved hostname (e.g. "mac-studio.local").</summary>
    public required string HostName { get; init; }

    /// <summary>Advertised SSH port.</summary>
    public required int Port { get; init; }

    /// <summary>Merged, deduped candidate addresses (IPv4 + IPv6 with scope), newest merge order.</summary>
    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>When this instance was first seen in the current session.</summary>
    public required DateTimeOffset FirstSeenAt { get; init; }

    /// <summary>When this instance was most recently seen on any interface.</summary>
    public required DateTimeOffset LastSeenAt { get; init; }
}
