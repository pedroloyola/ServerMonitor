namespace ServerMonitor.WidgetContract;

/// <summary>
/// The whole-fleet snapshot the widget provider reads. Versioned (§11), generated off the monitoring
/// cycle (§14), written atomically (§12), and treated as untrusted on read (§17). An empty fleet is a
/// valid snapshot: <see cref="Servers"/> is empty and <see cref="OverallHealth"/> is
/// <see cref="WidgetHealth.Unknown"/> (§33).
/// </summary>
public sealed record WidgetStateSnapshot
{
    /// <summary>Schema version; a reader accepts only <see cref="WidgetSchema.CurrentVersion"/> (§11).</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// When the app produced this snapshot (UTC). This is the whole-snapshot freshness anchor the
    /// provider uses to decide fresh/stale/unavailable (§22).
    /// </summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>
    /// Deterministic worst-of-fleet health (§21), precomputed by the writer so the provider need not
    /// re-derive it. See <see cref="WidgetHealthPrecedence"/>.
    /// </summary>
    public required WidgetHealth OverallHealth { get; init; }

    /// <summary>The visible/active fleet, at most <see cref="WidgetSchema.MaxServers"/> entries (§18).</summary>
    public required IReadOnlyList<WidgetServerState> Servers { get; init; }
}
