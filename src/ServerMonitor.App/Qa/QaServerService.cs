using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY read-only <see cref="IServerService"/>. Serves the deterministic catalog servers and
/// never persists anything — <c>servers.json</c> is never touched. Mutations are unsupported;
/// the dashboard swallows the resulting exception, so the harness stays a pure viewer.
/// </summary>
internal sealed class QaServerService : IServerService
{
    // Never raised in QA mode; empty accessors keep the contract without a backing field.
    public event EventHandler? ServersChanged { add { } remove { } }

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Server>>(QaHealthCatalog.Servers);

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA health harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA health harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA health harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
