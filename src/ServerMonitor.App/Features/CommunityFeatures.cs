using ServerMonitor.Features;

namespace ServerMonitor.App.Features;

/// <summary>
/// The capabilities this repository composes. Every one of them declares
/// <see cref="FeatureDescriptor.RequiresEntitlement"/> = <c>false</c>: they ship, they are free, and the
/// entitlement provider is never asked about them.
/// <para>
/// Adding an id here does not gate anything. Turning one of these into an entitlement-requiring capability
/// would remove functionality a user already has, which the first non-negotiable principle of M14 forbids.
/// </para>
/// </summary>
public static class CommunityFeatures
{
    /// <summary>Local SQLite history: recorder, bounded channel, single writer, queries, retention (M10).</summary>
    public static readonly FeatureDescriptor History = FeatureDescriptor.Unconditional("history.local");

    /// <summary>Read-only Docker and service inventory over the shared SSH session (M11).</summary>
    public static readonly FeatureDescriptor Workloads = FeatureDescriptor.Unconditional("workloads.readonly");

    /// <summary>Passive local mDNS/DNS-SD suggestions, with ignored-device decisions (M7).</summary>
    public static readonly FeatureDescriptor Discovery = FeatureDescriptor.Unconditional("discovery.mdns");

    /// <summary>The sanitized fleet snapshot the out-of-process Windows widget reads (M13 Slice 1).</summary>
    public static readonly FeatureDescriptor WidgetSnapshot = FeatureDescriptor.Unconditional("widget.snapshot");
}
