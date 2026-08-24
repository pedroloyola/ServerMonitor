using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public interface IServerConnectionStateStore
{
    event EventHandler<Guid>? StateChanged;

    SshConnectionResult? Get(Guid serverId);

    void Set(Guid serverId, SshConnectionResult result);

    void Remove(Guid serverId);
}
