namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Pure freshness/carry-over logic for workload snapshots (§39), applied by the App collector service
/// after each fresh attempt. Mirrors the host stale policy (ADR-011): a failed attempt never fabricates
/// freshness and never zeroes prior data.
/// <para>
/// A part (Docker or Services) that returns a <b>definitive</b> availability — a real, current answer,
/// including "not installed" / "unavailable" / "permission denied" — is fresh and replaces the previous
/// part. A part that returns <see cref="DockerAvailability.Unknown"/>/<see cref="DockerAvailability.Error"/>
/// (we failed to determine) is carried over from the previous snapshot when the previous part was
/// definitive, and the merged snapshot is marked stale. <see cref="ServerWorkloadSnapshot.CapturedAtUtc"/>
/// advances only when at least one part is freshly definitive; otherwise it stays put (never moves
/// backwards).
/// </para>
/// </summary>
public static class WorkloadFreshnessMerger
{
    /// <summary>A definitive Docker answer — one we can trust as current, even if negative.</summary>
    public static bool IsDefinitive(DockerAvailability availability) =>
        availability is not (DockerAvailability.Unknown or DockerAvailability.Error);

    /// <summary>A definitive services answer — one we can trust as current, even if negative.</summary>
    public static bool IsDefinitive(WorkloadServiceAvailability availability) =>
        availability is not (WorkloadServiceAvailability.Unknown or WorkloadServiceAvailability.Error);

    /// <summary>
    /// Merges a fresh <paramref name="attempt"/> against the <paramref name="previous"/> stored snapshot
    /// (or <c>null</c> if none), stamping the result at <paramref name="nowUtc"/>. The attempt's own
    /// timestamps/<c>IsStale</c> are ignored — this method owns freshness.
    /// </summary>
    public static ServerWorkloadSnapshot Merge(
        ServerWorkloadSnapshot? previous,
        ServerWorkloadSnapshot attempt,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var dockerFresh = IsDefinitive(attempt.Docker.Availability);
        var docker = attempt.Docker;
        var dockerCarried = false;
        if (!dockerFresh && previous is not null && IsDefinitive(previous.Docker.Availability))
        {
            docker = previous.Docker;
            dockerCarried = true;
        }

        var servicesFresh = IsDefinitive(attempt.Services.Availability);
        var services = attempt.Services;
        var servicesCarried = false;
        if (!servicesFresh && previous is not null && IsDefinitive(previous.Services.Availability))
        {
            services = previous.Services;
            servicesCarried = true;
        }

        var anyFresh = dockerFresh || servicesFresh;
        var anyCarried = dockerCarried || servicesCarried;

        // CapturedAtUtc advances only when something fresh was shown; otherwise keep the prior capture
        // time (never move it backwards). With no prior snapshot, there is nothing older, so use now.
        var capturedAt = anyFresh ? nowUtc : previous?.CapturedAtUtc ?? nowUtc;

        return new ServerWorkloadSnapshot
        {
            ServerId = attempt.ServerId,
            CapturedAtUtc = capturedAt,
            LastAttemptAtUtc = nowUtc,
            IsStale = anyCarried,
            Docker = docker,
            Services = services
        };
    }
}
