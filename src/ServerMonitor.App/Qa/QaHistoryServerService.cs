using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>QA-ONLY read-only <see cref="IServerService"/> serving the history catalog's servers.</summary>
internal sealed class QaHistoryServerService : IServerService
{
    public event EventHandler? ServersChanged { add { } remove { } }

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Server>>(QaHistoryCatalog.Servers);

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA history harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA history harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA history harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
