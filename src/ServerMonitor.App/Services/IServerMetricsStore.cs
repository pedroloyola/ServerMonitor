using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

/// <summary>
/// Transient, in-memory metrics cache. Nothing here is persisted across app
/// restarts; it exists only to remember the last snapshot per server and to
/// de-duplicate concurrent manual refreshes for the same server.
/// </summary>
public interface IServerMetricsStore
{
    ServerMetricsSnapshot? GetLastSnapshot(Guid serverId);

    Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default);

    void Remove(Guid serverId);
}
