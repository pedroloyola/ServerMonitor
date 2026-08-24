using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface IServerRepository
{
    Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAllAsync(
        IReadOnlyCollection<Server> servers,
        CancellationToken cancellationToken = default);
}
