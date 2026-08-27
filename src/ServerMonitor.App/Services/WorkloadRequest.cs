namespace ServerMonitor.App.Services;

/// <summary>Why a workload collection was requested. Manual bypasses the cadence throttle (§36).</summary>
public enum WorkloadRefreshReason
{
    Scheduled,
    Manual
}

/// <summary>
/// A scheduled unit of work handed from the cadence observer to the single
/// <see cref="WorkloadCollectorService"/> via the bounded queue. Manual refreshes do not travel this
/// queue — they enlist directly and synchronously into the per-server single-flight slot (§37) so a
/// user-initiated refresh deterministically joins any in-flight collection.
/// </summary>
public sealed record WorkloadRequest
{
    public required Guid ServerId { get; init; }

    public required WorkloadRefreshReason Reason { get; init; }
}
