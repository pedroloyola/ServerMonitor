namespace ServerMonitor.Core.Enums;

/// <summary>
/// How a service suggestion was discovered. Kept explicit on the snapshot so the UI and future
/// discovery layers (subnet scan, manual) are distinguishable without inferring from other
/// fields. Carries no operating-system guess and no secrets.
/// </summary>
public enum DiscoverySource
{
    /// <summary>Discovered passively via mDNS / DNS-SD (_ssh._tcp on the local link).</summary>
    Mdns = 0
}
