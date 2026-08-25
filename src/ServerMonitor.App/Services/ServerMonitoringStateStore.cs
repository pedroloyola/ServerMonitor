using System.Collections.Concurrent;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

public sealed class ServerMonitoringStateStore : IServerMonitoringStateStore
{
    private readonly ConcurrentDictionary<Guid, ServerMonitoringState> _states = new();

    public event EventHandler<Guid>? StateChanged;

    public ServerMonitoringState Get(Guid serverId) =>
        _states.TryGetValue(serverId, out var state) ? state : ServerMonitoringState.Initial(serverId);

    public bool TryGet(Guid serverId, out ServerMonitoringState state) =>
        _states.TryGetValue(serverId, out state!);

    public IReadOnlyCollection<ServerMonitoringState> GetAll() => _states.Values.ToArray();

    public void Set(ServerMonitoringState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _states[state.ServerId] = state;
        StateChanged?.Invoke(this, state.ServerId);
    }

    public void Remove(Guid serverId)
    {
        if (_states.TryRemove(serverId, out _))
        {
            StateChanged?.Invoke(this, serverId);
        }
    }
}
