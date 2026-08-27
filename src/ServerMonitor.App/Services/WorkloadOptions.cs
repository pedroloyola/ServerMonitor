namespace ServerMonitor.App.Services;

/// <summary>
/// Tuning for the M11 workload collection path. Deliberately modest: workloads are a secondary,
/// read-only side channel that must never contend with the host monitoring engine.
/// </summary>
public sealed record WorkloadOptions
{
    public static readonly WorkloadOptions Default = new();

    /// <summary>Minimum spacing between scheduled workload collections per server (§35).</summary>
    public TimeSpan MinCadence { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Max concurrent workload collections across all servers. Its own limiter (not the M6 host limiter)
    /// so workloads never steal host collection slots (§36).
    /// </summary>
    public int MaxConcurrentCollections { get; init; } = 2;

    /// <summary>Per-collection SSH timeout handed to the remote source.</summary>
    public TimeSpan CollectionTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Bound on the request queue between the observer/refresh coordinator and the collector.</summary>
    public int QueueCapacity { get; init; } = 256;
}
