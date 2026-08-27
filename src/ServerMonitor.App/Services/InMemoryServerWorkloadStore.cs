using System.Collections.Concurrent;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Services;

/// <summary>
/// Transient, in-memory <see cref="IServerWorkloadStore"/> (§40): the workload collector service writes
/// it; the UI observes <see cref="WorkloadChanged"/>. Never persisted, holds no secrets. Mirrors
/// <c>ServerMonitoringStateStore</c>.
/// </summary>
public sealed class InMemoryServerWorkloadStore : IServerWorkloadStore
{
    private readonly ConcurrentDictionary<Guid, ServerWorkloadSnapshot> _snapshots = new();

    public event EventHandler<Guid>? WorkloadChanged;

    public ServerWorkloadSnapshot? Get(Guid serverId) =>
        _snapshots.TryGetValue(serverId, out var snapshot) ? snapshot : null;

    public IReadOnlyCollection<ServerWorkloadSnapshot> GetAll() => _snapshots.Values.ToArray();

    public void Set(ServerWorkloadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[snapshot.ServerId] = snapshot;
        WorkloadChanged?.Invoke(this, snapshot.ServerId);
    }

    public void Remove(Guid serverId)
    {
        if (_snapshots.TryRemove(serverId, out _))
        {
            WorkloadChanged?.Invoke(this, serverId);
        }
    }
}
