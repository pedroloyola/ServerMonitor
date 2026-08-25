using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Controllable <see cref="IServerService"/> for monitoring-engine tests. Only the members
/// the engine touches — <see cref="GetAllAsync"/> and <see cref="ServersChanged"/> — are
/// implemented; the mutation methods are irrelevant here and throw. Tests mutate
/// <see cref="Servers"/> then call <see cref="RaiseChanged"/> to drive a reconcile.
/// </summary>
internal sealed class FakeServerService : IServerService
{
    public event EventHandler? ServersChanged;

    public List<Server> Servers { get; } = [];

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Server>>(Servers.ToList());

    public void RaiseChanged() => ServersChanged?.Invoke(this, EventArgs.Empty);

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
