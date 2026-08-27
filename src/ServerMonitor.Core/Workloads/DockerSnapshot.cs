namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Read-only view of Docker on one server at a point in time. Carries its own
/// <see cref="Availability"/> so a Docker failure is isolated from the services view (§38). The
/// container list is bounded by the parser; <see cref="Truncated"/> signals a list capped for safety
/// rather than a silently partial result.
/// </summary>
public sealed record DockerSnapshot
{
    /// <summary>An empty, not-yet-probed snapshot.</summary>
    public static readonly DockerSnapshot Unknown = new() { Availability = DockerAvailability.Unknown };

    public required DockerAvailability Availability { get; init; }

    public IReadOnlyList<ContainerInfo> Containers { get; init; } = [];

    /// <summary>True when more containers existed than the parser's bound; the list is capped, not partial-by-error.</summary>
    public bool Truncated { get; init; }
}
