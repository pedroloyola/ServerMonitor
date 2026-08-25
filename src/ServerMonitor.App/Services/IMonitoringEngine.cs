using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// Owns automatic monitoring: it schedules per-server refreshes, applies the retry and
/// health policy, and publishes <see cref="Core.Monitoring.ServerMonitoringState"/>. The UI
/// never manages timers; it observes state and, for a manual refresh, calls
/// <see cref="RefreshNowAsync"/>, which shares the same per-server single-flight as the
/// scheduler and restarts that server's interval.
/// </summary>
public interface IMonitoringEngine
{
    Task StartMonitoringAsync(CancellationToken cancellationToken = default);

    Task StopMonitoringAsync(CancellationToken cancellationToken = default);

    Task<ServerMetricsCollectionResult> RefreshNowAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);
}
