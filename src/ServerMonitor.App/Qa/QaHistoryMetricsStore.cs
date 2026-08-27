using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>QA-ONLY <see cref="IServerMetricsStore"/> serving the history catalog's current snapshot
/// per server, so each chart's live "current value" is exercised (including null → "—").</summary>
internal sealed class QaHistoryMetricsStore : IServerMetricsStore
{
    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => QaHistoryCatalog.SnapshotFor(serverId);

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

    public void Remove(Guid serverId)
    {
        // No-op: QA snapshots are immutable and in-memory.
    }
}
