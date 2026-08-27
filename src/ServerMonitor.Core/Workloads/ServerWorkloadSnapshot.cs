namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Read-only workload observability for one server (M11): Docker containers and managed services. A
/// second data source entirely separate from <c>ServerMetricsSnapshot</c> (that host snapshot is never
/// inflated with this). In-memory and transient (§40): never persisted, holds no secrets.
/// <para>
/// Freshness is honest (§39): <see cref="CapturedAtUtc"/> is the time the data was last collected
/// <b>fresh</b> and never moves backwards on a failed attempt; <see cref="IsStale"/> marks that the last
/// attempt failed and the Docker/Services views are carried over from before. A failed attempt keeps the
/// previous lists visible rather than zeroing them, mirroring the host stale policy (ADR-011).
/// </para>
/// </summary>
public sealed record ServerWorkloadSnapshot
{
    public required Guid ServerId { get; init; }

    /// <summary>When this workload data was last collected fresh (from the collector's TimeProvider).</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>The most recent collection attempt time (successful or not); may be after <see cref="CapturedAtUtc"/>.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    /// <summary>True when the last attempt failed and Docker/Services are carried-over (stale), not fresh.</summary>
    public bool IsStale { get; init; }

    /// <summary>Docker view; never <c>null</c> — carries its own availability (§38).</summary>
    public required DockerSnapshot Docker { get; init; }

    /// <summary>Services view; never <c>null</c> — carries its own manager and availability (§38).</summary>
    public required ServiceSnapshot Services { get; init; }

    /// <summary>An initial, not-yet-collected snapshot for a server.</summary>
    public static ServerWorkloadSnapshot Initial(Guid serverId, DateTimeOffset nowUtc) => new()
    {
        ServerId = serverId,
        CapturedAtUtc = nowUtc,
        LastAttemptAtUtc = null,
        IsStale = false,
        Docker = DockerSnapshot.Unknown,
        Services = ServiceSnapshot.Unknown
    };
}
