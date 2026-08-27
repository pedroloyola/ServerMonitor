namespace ServerMonitor.App.Services;

/// <summary>
/// Forces a fresh workload collection for a server, bypassing the cadence throttle and coalescing with
/// any collection already in flight (single-flight, §37). Invoked alongside
/// <c>IMonitoringEngine.RefreshNowAsync</c> by the manual per-server refresh and by Refresh All, so a
/// user-initiated refresh updates workloads too (§36), respecting the global workload concurrency limit.
/// </summary>
public interface IWorkloadRefreshCoordinator
{
    /// <summary>Completes when the triggered (or joined in-flight) collection for the server finishes.</summary>
    Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default);
}
