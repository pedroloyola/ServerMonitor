using System.Collections.Concurrent;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public sealed class ServerMetricsStore(IServerMetricsCollector collector) : IServerMetricsStore
{
    private readonly ConcurrentDictionary<Guid, ServerMetricsSnapshot> _lastSnapshots = new();
    private readonly ConcurrentDictionary<Guid, Task<ServerMetricsCollectionResult>> _inFlight = new();

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => _lastSnapshots.GetValueOrDefault(serverId);

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return _inFlight.GetOrAdd(server.Id, _ => CollectAsync(server, cancellationToken));
    }

    public void Remove(Guid serverId)
    {
        _lastSnapshots.TryRemove(serverId, out _);
    }

    private async Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        // Force asynchrony before touching _inFlight. GetOrAdd stores the task
        // this method returns; without the yield, a synchronously-completing
        // collector (e.g. a cancelled token or a non-Linux fast fail) would run
        // the finally below—removing the in-flight entry—before GetOrAdd had
        // even inserted it, leaving a completed task cached forever and every
        // later refresh returning that first stale result.
        await Task.Yield();
        try
        {
            var result = await collector.CollectAsync(server, cancellationToken).ConfigureAwait(false);
            if (result.Snapshot is not null)
            {
                _lastSnapshots[server.Id] = result.Snapshot;
            }

            return result;
        }
        finally
        {
            _inFlight.TryRemove(server.Id, out _);
        }
    }
}
