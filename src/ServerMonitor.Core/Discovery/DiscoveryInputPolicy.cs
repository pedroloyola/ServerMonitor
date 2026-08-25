using System.Net;

namespace ServerMonitor.Core.Discovery;

/// <summary>
/// Pure, deterministic guardrails for turning untrusted mDNS wire data into a
/// <see cref="DiscoveryObservation"/>. Everything an announcement carries — instance name,
/// hostname, port, addresses — arrives from anyone on the local link and must be treated as
/// hostile: control characters, empty or absurdly long strings, invalid ports and unbounded
/// address lists are all rejected or clamped here so the rest of the pipeline can trust its
/// inputs. This type has no I/O and no time dependency, so it is trivially unit-testable.
/// </summary>
public static class DiscoveryInputPolicy
{
    /// <summary>Maximum distinct addresses retained per service, across all interfaces.</summary>
    public const int MaxAddressesPerService = 16;

    /// <summary>Maximum services surfaced to the UI at once (flood ceiling).</summary>
    public const int MaxVisibleServices = 256;

    /// <summary>Maximum persisted "ignored" identities.</summary>
    public const int MaxIgnoredIdentities = 2048;

    /// <summary>Maximum size of the ignored-devices JSON file that will be read.</summary>
    public const int MaxIgnoreFileBytes = 256 * 1024;

    /// <summary>Maximum accepted length of a service instance name.</summary>
    public const int MaxInstanceNameLength = 128;

    /// <summary>Maximum accepted length of a hostname (composed, fully-qualified).</summary>
    public const int MaxHostNameLength = 255;

    /// <summary>Maximum ASCII characters in one DNS hostname label.</summary>
    public const int MaxDnsLabelLength = 63;

    /// <summary>Length in characters of a lower-case hex SHA-256 identity hash.</summary>
    public const int IdentityHashLength = 64;

    /// <summary>The only DNS-SD service type discovery accepts.</summary>
    public const string SshServiceType = "_ssh._tcp";

    /// <summary>
    /// Attempts to build a validated observation from raw announcement fields. Returns
    /// <c>null</c> when the instance name, service type/domain, host label or port are missing,
    /// malformed, contain control characters, or are implausibly long. The host label and domain
    /// are combined into a canonical fully-qualified hostname (Tmds.MDns exposes only the first
    /// label in <c>Hostname</c> and the domain separately). Addresses are deduped
    /// (order-preserving) and clamped to <see cref="MaxAddressesPerService"/>; IPv4 and IPv6
    /// (including link-local scope ids) are preserved as-is.
    /// </summary>
    public static DiscoveryObservation? TryCreateObservation(
        string? instanceName,
        string? serviceType,
        string? domain,
        string? hostLabel,
        int port,
        IEnumerable<IPAddress>? addresses,
        string? interfaceId,
        DateTimeOffset observedAt)
    {
        if (!IsAcceptableText(instanceName, MaxInstanceNameLength))
        {
            return null;
        }

        if (!IsAcceptableText(hostLabel, MaxHostNameLength) || ContainsWhitespace(hostLabel!))
        {
            return null;
        }

        if (port is < 1 or > 65535)
        {
            return null;
        }

        var identity = ServiceInstanceIdentity.TryCreate(instanceName, serviceType, domain);
        if (identity is null)
        {
            return null;
        }

        // Discovery is SSH-only: accept only the normalized _ssh._tcp service type.
        if (!string.Equals(identity.ServiceType, SshServiceType, StringComparison.Ordinal))
        {
            return null;
        }

        var hostName = ComposeHostName(hostLabel, domain);
        if (hostName is null || hostName.Length > MaxHostNameLength || !IsValidDnsHostName(hostName))
        {
            return null;
        }

        var normalizedInterface = string.IsNullOrWhiteSpace(interfaceId)
            ? "default"
            : interfaceId.Trim();

        var dedupedAddresses = DedupeAddresses(addresses);

        return new DiscoveryObservation
        {
            Identity = identity,
            HostName = hostName,
            Port = port,
            Addresses = dedupedAddresses,
            InterfaceId = normalizedInterface,
            ObservedAt = observedAt
        };
    }

    /// <summary>
    /// Combines an mDNS host label with its domain into a canonical, lower-cased, fully-qualified
    /// hostname with no trailing dot (e.g. label "Mac-Studio" + domain "local." → "mac-studio.local").
    /// If the label already carries the domain suffix it is not duplicated. Returns <c>null</c>
    /// for an empty label.
    /// </summary>
    public static string? ComposeHostName(string? hostLabel, string? domain)
    {
        var label = hostLabel?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        var lowerLabel = label.ToLowerInvariant();
        var normalizedDomain = domain?.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedDomain))
        {
            return lowerLabel;
        }

        if (lowerLabel.Equals(normalizedDomain, StringComparison.Ordinal)
            || lowerLabel.EndsWith("." + normalizedDomain, StringComparison.Ordinal))
        {
            return lowerLabel;
        }

        return lowerLabel + "." + normalizedDomain;
    }

    /// <summary>
    /// True only for an exact lower-case hex SHA-256 hash (<see cref="IdentityHashLength"/> chars).
    /// Used to reject anything else before it is persisted to, or trusted from, the ignore store.
    /// </summary>
    public static bool IsValidIdentityHash(string? value)
    {
        if (value is null || value.Length != IdentityHashLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLowerHex = character is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!isLowerHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deduplicates addresses preserving first-seen order and both families (with IPv6 scope),
    /// clamped to <see cref="MaxAddressesPerService"/>. Null and any-address entries are dropped.
    /// </summary>
    public static IReadOnlyList<IPAddress> DedupeAddresses(IEnumerable<IPAddress>? addresses)
    {
        if (addresses is null)
        {
            return [];
        }

        var seen = new HashSet<IPAddress>();
        var result = new List<IPAddress>(MaxAddressesPerService);
        foreach (var address in addresses)
        {
            if (address is null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                continue;
            }

            if (seen.Add(address))
            {
                result.Add(address);
                if (result.Count >= MaxAddressesPerService)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static bool IsAcceptableText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character) || IsDisallowedFormatting(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rejects Unicode bidirectional and line/paragraph format controls (e.g. U+202E RIGHT-TO-LEFT
    /// OVERRIDE) that can spoof how untrusted text renders, while leaving ordinary letters, marks,
    /// emoji and zero-width joiners untouched. Values are the codepoints themselves, so this stays
    /// pure ASCII in source (no embedded control bytes).
    /// </summary>
    private static bool IsDisallowedFormatting(char character) =>
        character == (char)0x061C                                   // ARABIC LETTER MARK
        || character == (char)0x200E || character == (char)0x200F   // LRM, RLM
        || character == (char)0x2028 || character == (char)0x2029   // line / paragraph separator
        || (character >= (char)0x202A && character <= (char)0x202E) // LRE, RLE, PDF, LRO, RLO
        || (character >= (char)0x2066 && character <= (char)0x2069);// LRI, RLI, FSI, PDI

    /// <summary>
    /// Validates a composed, lower-cased FQDN: each dot-separated label must be non-empty,
    /// contain only ASCII letters, digits and hyphens, contain at most 63 ASCII characters, and
    /// neither start nor end with a hyphen. Rejects "bad..host", "bad/host", "bad:host",
    /// overlong labels and leading/trailing-hyphen labels.
    /// </summary>
    private static bool IsValidDnsHostName(string hostName)
    {
        if (hostName.Length == 0)
        {
            return false;
        }

        foreach (var label in hostName.Split('.'))
        {
            if (label.Length is 0 or > MaxDnsLabelLength || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var character in label)
            {
                var isLabelChar = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
                if (!isLabelChar)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }
}
