using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY inert <see cref="IMonitoringEngine"/>. It never schedules, connects or collects, so the
/// harness stays static and offline. A manual refresh is a no-op: the card re-reads the same
/// seeded scenario state, so clicking refresh cannot mutate or break a scenario.
/// </summary>
internal sealed class QaMonitoringEngine : IMonitoringEngine
{
    public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ServerMetricsCollectionResult> RefreshNowAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));
}
