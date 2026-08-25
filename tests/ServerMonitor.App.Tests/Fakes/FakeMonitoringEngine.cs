using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Controllable <see cref="IMonitoringEngine"/> for ViewModel tests. A manual refresh is
/// answered by <see cref="OnRefresh"/> (which a test can use to also mutate the state/metrics
/// stores, mimicking a real cycle) and counted so the card's delegation can be asserted.
/// </summary>
internal sealed class FakeMonitoringEngine : IMonitoringEngine
{
    public int RefreshNowCount { get; private set; }

    public Guid LastRefreshedServerId { get; private set; }

    public Func<Guid, ServerMetricsCollectionResult>? OnRefresh { get; set; }

    public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ServerMetricsCollectionResult> RefreshNowAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        RefreshNowCount++;
        LastRefreshedServerId = serverId;
        var result = OnRefresh?.Invoke(serverId)
            ?? ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected);
        return Task.FromResult(result);
    }
}
