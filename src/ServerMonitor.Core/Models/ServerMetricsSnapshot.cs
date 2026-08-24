namespace ServerMonitor.Core.Models;

public sealed record ServerMetricsSnapshot
{
    public required Guid ServerId { get; init; }

    public required DateTimeOffset CollectedAt { get; init; }

    public double? CpuUsagePercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public double? MemoryUsagePercent { get; init; }

    public long? DiskUsedBytes { get; init; }

    public long? DiskTotalBytes { get; init; }

    public double? DiskUsagePercent { get; init; }

    public TimeSpan? Uptime { get; init; }

    public string? Hostname { get; init; }

    public string? OperatingSystemName { get; init; }

    public string? OperatingSystemVersion { get; init; }

    public bool HasAnyData =>
        CpuUsagePercent is not null ||
        MemoryTotalBytes is not null ||
        DiskTotalBytes is not null ||
        Uptime is not null ||
        !string.IsNullOrWhiteSpace(Hostname) ||
        !string.IsNullOrWhiteSpace(OperatingSystemName) ||
        !string.IsNullOrWhiteSpace(OperatingSystemVersion);
}
