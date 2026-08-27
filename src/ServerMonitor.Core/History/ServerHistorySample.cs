using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.History;

/// <summary>
/// One persisted point of server history. Metrics-only: never contains secrets, credentials,
/// hostnames or raw SSH errors. Identity is the stable <see cref="ServerId"/> (a server may
/// change name/host/IP and keep its history). Nullable metrics preserve <c>unknown ≠ zero</c>:
/// a failed/offline cycle stores <c>null</c>, never <c>0</c>.
/// </summary>
public sealed record ServerHistorySample
{
    public required Guid ServerId { get; init; }

    /// <summary>When the producing cycle completed, in UTC. Persisted as Unix epoch milliseconds.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required ServerHealth Health { get; init; }

    public double? CpuPercent { get; init; }

    public double? MemoryPercent { get; init; }

    public double? DiskPercent { get; init; }
}
