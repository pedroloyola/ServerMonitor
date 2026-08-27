namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Read-only view of a server's services at a point in time. <see cref="Manager"/> is resolved from the
/// OS (§69); it is <see cref="ServiceManager.Unsupported"/> when no supported manager applies. Carries
/// its own <see cref="Availability"/> so a services failure is isolated from Docker (§38). The list is
/// bounded; <see cref="Truncated"/> signals a safety cap, not an error-partial result.
/// </summary>
public sealed record ServiceSnapshot
{
    /// <summary>An empty, not-yet-probed snapshot with no resolved manager.</summary>
    public static readonly ServiceSnapshot Unknown = new()
    {
        Manager = ServiceManager.Unsupported,
        Availability = WorkloadServiceAvailability.Unknown
    };

    public required ServiceManager Manager { get; init; }

    public required WorkloadServiceAvailability Availability { get; init; }

    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];

    public bool Truncated { get; init; }
}
