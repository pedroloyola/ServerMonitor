namespace ServerMonitor.WidgetContract;

/// <summary>
/// One server's sanitized, minimized state for a Windows widget surface. It carries ONLY: an opaque
/// internal id, a sanitized friendly display name, normalized health, three normalized percentages
/// (0–100, or <c>null</c> = unknown — never 0-for-unknown, §19), and a freshness timestamp.
/// <para>
/// It deliberately carries NONE of: host/IP, port, SSH username, OS name/version, hostname, credential
/// reference, private-key path, host-key material, raw SSH output, service/container/process names,
/// commands, or logs (§9). The type has no field for any of them, so nothing sensitive can leak through
/// this boundary by construction.
/// </para>
/// </summary>
public sealed record WidgetServerState
{
    /// <summary>
    /// Opaque internal server identifier. Not PII and not a network identifier — it is the same GUID the
    /// app uses internally, meaningful only to the app and its own widget provider (§8).
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Sanitized friendly name configured by the user (§10): control/format characters stripped, length
    /// capped, never falling back to an IP or technical hostname.
    /// </summary>
    public required string DisplayName { get; init; }

    public required WidgetHealth Health { get; init; }

    /// <summary>CPU utilization 0–100, or <c>null</c> when unknown/unavailable (§19).</summary>
    public double? CpuUsagePercent { get; init; }

    /// <summary>Memory utilization 0–100, or <c>null</c> when unknown/unavailable (§19).</summary>
    public double? MemoryUsagePercent { get; init; }

    /// <summary>Disk utilization 0–100, or <c>null</c> when unknown/unavailable (§19).</summary>
    public double? DiskUsagePercent { get; init; }

    // Absolute memory/disk sizes in GiB (used + total), or null when unknown. These are low-sensitivity
    // RESOURCE metrics — like the percentages above — shown only on the Large widget for richer detail
    // ("3.1 / 8.0 GB"). They carry NO host/network/credential/OS information (§9 still holds).
    public double? MemoryUsedGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? DiskUsedGb { get; init; }
    public double? DiskTotalGb { get; init; }

    /// <summary>Server uptime in seconds, or null when unknown — a benign resource metric shown under CPU
    /// on the Large widget ("43d 18h"). No host/network/credential/OS information (§9 still holds).</summary>
    public long? UptimeSeconds { get; init; }

    /// <summary>
    /// When this server's metrics were last successfully read (UTC), or <c>null</c> if never. The reader
    /// derives per-server freshness (fresh/stale) from this against its own clock (§22).
    /// </summary>
    public DateTimeOffset? LastUpdatedUtc { get; init; }
}
