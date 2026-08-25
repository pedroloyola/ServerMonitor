using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

/// <summary>
/// Transient, in-memory store of per-server <see cref="ServerMonitoringState"/>. The
/// monitoring engine writes it; the UI observes <see cref="StateChanged"/> to render
/// health, staleness and the refresh indicator. Never persisted; holds no metric values.
/// </summary>
public interface IServerMonitoringStateStore
{
    event EventHandler<Guid>? StateChanged;

    ServerMonitoringState Get(Guid serverId);

    /// <summary>
    /// Reads only an explicitly stored state. Unlike <see cref="Get"/>, removal events do
    /// not become synthetic Unknown observations for transition consumers.
    /// </summary>
    bool TryGet(Guid serverId, out ServerMonitoringState state);

    IReadOnlyCollection<ServerMonitoringState> GetAll();

    void Set(ServerMonitoringState state);

    void Remove(Guid serverId);
}
