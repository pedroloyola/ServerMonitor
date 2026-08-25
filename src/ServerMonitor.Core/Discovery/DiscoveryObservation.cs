using System.Net;

namespace ServerMonitor.Core.Discovery;

/// <summary>
/// A single validated observation of an mDNS service instance on one network interface, as
/// emitted by the browser seam (<c>IMdnsServiceBrowser</c>). It is the merge unit: the runtime
/// store keys observations by <see cref="Identity"/> and folds together the per-interface
/// observations of the same instance seen across NICs.
/// </summary>
/// <remarks>
/// Deliberately excludes TXT records (not retained), operating-system guesses, and anything
/// touching SSH, credentials, trust or metrics. Construct only through
/// <see cref="DiscoveryInputPolicy.TryCreateObservation"/> so every field is already sanitized.
/// </remarks>
public sealed record DiscoveryObservation
{
    public required ServiceInstanceIdentity Identity { get; init; }

    /// <summary>Resolved hostname, trimmed of a trailing dot (e.g. "mac-studio.local").</summary>
    public required string HostName { get; init; }

    /// <summary>Advertised TCP port (1–65535).</summary>
    public required int Port { get; init; }

    /// <summary>Deduped addresses for this interface; IPv4 and IPv6 (with scope) preserved.</summary>
    public required IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>Opaque identifier of the network interface this observation arrived on.</summary>
    public required string InterfaceId { get; init; }

    /// <summary>When this observation was made, per the caller's <see cref="TimeProvider"/>.</summary>
    public required DateTimeOffset ObservedAt { get; init; }
}
