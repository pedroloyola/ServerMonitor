using System.Security.Cryptography;
using System.Text;

namespace ServerMonitor.Core.Discovery;

/// <summary>
/// Stable dedup identity for a discovered DNS-SD service instance, formed from the
/// normalized service instance name plus its service type and domain — never the IP
/// address, which can move between interfaces and reboots. Two announcements of the same
/// instance seen on different network interfaces share this identity and are merged;
/// two distinct instance names remain distinct services.
/// </summary>
/// <remarks>
/// Equality and hashing are case-insensitive and trailing-dot-insensitive, so casing or
/// trailing-dot variation between announcements collapses onto a single identity — while the
/// first-seen display casing is preserved in <see cref="InstanceName"/>. This type carries no
/// secrets and no metric/SSH/trust data. <see cref="StableHash"/> is a deterministic,
/// non-sensitive fingerprint of the canonical (lower-cased) identity, suitable for persisting
/// the user's "ignored" decisions separately from <c>servers.json</c>.
/// </remarks>
public sealed record ServiceInstanceIdentity
{
    // Unit-separator U+001F: an unambiguous delimiter between identity components for
    // hashing; it cannot legitimately appear in a validated instance name, type or domain.
    private const char CanonicalSeparator = (char)0x1F;

    private ServiceInstanceIdentity(string instanceName, string serviceType, string domain)
    {
        InstanceName = instanceName;
        ServiceType = serviceType;
        Domain = domain;
    }

    /// <summary>The first-seen service instance name (e.g. "Mac Studio"), display casing preserved.</summary>
    public string InstanceName { get; }

    /// <summary>The service type, lower-cased (e.g. "_ssh._tcp").</summary>
    public string ServiceType { get; }

    /// <summary>The domain, lower-cased without a trailing dot (e.g. "local").</summary>
    public string Domain { get; }

    /// <summary>
    /// Builds a normalized identity, or <c>null</c> when the inputs are empty or otherwise
    /// unusable. The instance name keeps its display casing (trimmed of surrounding whitespace
    /// and a trailing dot); type and domain are trimmed of dots and lower-cased so "_SSH._TCP"
    /// and "local." canonicalize. Equality is case-insensitive, so distinct-casing sightings of
    /// the same name deduplicate rather than duplicating.
    /// </summary>
    public static ServiceInstanceIdentity? TryCreate(string? instanceName, string? serviceType, string? domain)
    {
        var instance = instanceName?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(instance))
        {
            return null;
        }

        var type = NormalizeLabel(serviceType);
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        var normalizedDomain = NormalizeLabel(domain);
        if (string.IsNullOrEmpty(normalizedDomain))
        {
            normalizedDomain = "local";
        }

        return new ServiceInstanceIdentity(instance, type, normalizedDomain);
    }

    /// <summary>
    /// Deterministic, non-sensitive SHA-256 fingerprint of the canonical (lower-cased) identity,
    /// as lower-case hex. Casing variants of the same instance produce the same hash. Used as the
    /// persisted key for ignored devices.
    /// </summary>
    public string StableHash
    {
        get
        {
            var canonical = string.Join(
                CanonicalSeparator,
                InstanceName.ToLowerInvariant(),
                ServiceType,
                Domain);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexStringLower(bytes);
        }
    }

    public bool Equals(ServiceInstanceIdentity? other) =>
        other is not null
        && string.Equals(InstanceName, other.InstanceName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ServiceType, other.ServiceType, StringComparison.Ordinal)
        && string.Equals(Domain, other.Domain, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(
            InstanceName.ToLowerInvariant(),
            ServiceType,
            Domain);

    private static string NormalizeLabel(string? value) =>
        value?.Trim().Trim('.').ToLowerInvariant() ?? string.Empty;
}
