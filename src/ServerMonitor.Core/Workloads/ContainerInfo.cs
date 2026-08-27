namespace ServerMonitor.Core.Workloads;

/// <summary>
/// A single Docker container, read-only (M11). Strings are sanitized at the parser boundary
/// (control characters stripped, length-clamped). The optional per-container resource fields are
/// nullable and default to <c>null</c> ("not measured"); whether they are ever populated is a
/// platform-infra decision (ADR-016) driven by the cost of <c>docker stats</c>. unknown ≠ zero:
/// an unmeasured metric is <c>null</c>, never 0.
/// </summary>
public sealed record ContainerInfo
{
    /// <summary>Short container id (sanitized). Never a secret.</summary>
    public required string ContainerId { get; init; }

    /// <summary>Container name (sanitized).</summary>
    public required string Name { get; init; }

    /// <summary>Image reference, may include tag (sanitized).</summary>
    public required string Image { get; init; }

    public required ContainerState State { get; init; }

    /// <summary>Human-readable status line, e.g. "Up 3 hours", "Exited (0) 2 minutes ago" (sanitized).</summary>
    public required string StatusText { get; init; }

    public required ContainerHealth Health { get; init; }

    /// <summary>Creation time when the engine reports it reliably; otherwise <c>null</c>.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    // --- Optional resource metrics (default OFF; ADR-016 decides population). null = not measured. ---

    public double? CpuPercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryLimitBytes { get; init; }

    public double? MemoryPercent { get; init; }
}
