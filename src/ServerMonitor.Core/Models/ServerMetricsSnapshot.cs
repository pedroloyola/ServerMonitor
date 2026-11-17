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

    /// <summary>The 1 minute load average; null when unknown, never 0 for a missing reading.</summary>
    public double? LoadAverage1 { get; init; }

    public double? LoadAverage5 { get; init; }

    public double? LoadAverage15 { get; init; }

    /// <summary>The CPUs the load is measured against (online CPUs on Linux, hw.logicalcpu on macOS).</summary>
    public int? LogicalCpuCount { get; init; }

    public bool HasAnyData =>
        CpuUsagePercent is not null ||
        LoadAverage1 is not null ||
        MemoryTotalBytes is not null ||
        DiskTotalBytes is not null ||
        Uptime is not null ||
        !string.IsNullOrWhiteSpace(Hostname) ||
        !string.IsNullOrWhiteSpace(OperatingSystemName) ||
        !string.IsNullOrWhiteSpace(OperatingSystemVersion);
}
