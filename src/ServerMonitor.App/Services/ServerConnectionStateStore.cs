using System.Collections.Concurrent;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public sealed class ServerConnectionStateStore : IServerConnectionStateStore
{
    private readonly ConcurrentDictionary<Guid, SshConnectionResult> _states = [];

    public event EventHandler<Guid>? StateChanged;

    public SshConnectionResult? Get(Guid serverId) => _states.GetValueOrDefault(serverId);

    public void Set(Guid serverId, SshConnectionResult result)
    {
        if (serverId == Guid.Empty)
        {
            return;
        }

        _states[serverId] = result;
        StateChanged?.Invoke(this, serverId);
    }

    public void Remove(Guid serverId)
    {
        _states.TryRemove(serverId, out _);
        StateChanged?.Invoke(this, serverId);
    }
}
