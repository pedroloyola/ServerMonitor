using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface IServerProfileService
{
    Task<ServerOperationResult> AddAsync(
        ServerProfileInput input,
        CancellationToken cancellationToken = default);

    Task<ServerOperationResult> UpdateAsync(
        Server existingServer,
        ServerProfileInput input,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default);
}
