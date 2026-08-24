using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface IServerService
{
    event EventHandler? ServersChanged;

    Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ServerOperationResult> AddAsync(
        ServerInput input,
        CancellationToken cancellationToken = default);

    Task<ServerOperationResult> UpdateAsync(
        Guid id,
        ServerInput input,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default);
}
