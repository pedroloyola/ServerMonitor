using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY read-only <see cref="IServerService"/> that reports no configured servers. Used by the
/// --qa-discovery harness so the dashboard shows its empty state with the "Encontrados na rede"
/// section beneath it — the layout where discovery must render without any added servers. Never
/// persists; mutations are inert.
/// </summary>
internal sealed class QaNoServersService : IServerService
{
    public event EventHandler? ServersChanged { add { } remove { } }

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Server>>([]);

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA discovery harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA discovery harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA discovery harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
