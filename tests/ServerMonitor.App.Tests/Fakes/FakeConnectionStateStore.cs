using ServerMonitor.App.Services;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

internal sealed class FakeConnectionStateStore : IServerConnectionStateStore
{
    private readonly Dictionary<Guid, SshConnectionResult> _states = new();

    public event EventHandler<Guid>? StateChanged;

    public int SetCount { get; private set; }

    public SshConnectionResult? LastSet { get; private set; }

    public SshConnectionResult? Get(Guid serverId) =>
        _states.TryGetValue(serverId, out var result) ? result : null;

    public void Set(Guid serverId, SshConnectionResult result)
    {
        SetCount++;
        LastSet = result;
        _states[serverId] = result;
        StateChanged?.Invoke(this, serverId);
    }

    public void Remove(Guid serverId) => _states.Remove(serverId);
}
