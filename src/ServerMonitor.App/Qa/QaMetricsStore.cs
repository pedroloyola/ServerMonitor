using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY <see cref="IServerMetricsStore"/>. Serves the catalog's retained snapshot per server;
/// never performs a real collection. A missing metric is served as <c>null</c>, so the card's
/// unknown ≠ zero handling is exercised for real.
/// </summary>
internal sealed class QaMetricsStore : IServerMetricsStore
{
    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => QaHealthCatalog.SnapshotFor(serverId);

    // No SSH in QA mode; the QA engine owns "refresh", so this is never the collection path.
    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

    public void Remove(Guid serverId)
    {
        // No-op: QA snapshots are immutable and in-memory.
    }
}
