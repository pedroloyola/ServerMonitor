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
internal sealed class FakeServerService : IServerService, IServerLoadStatusSource
{
    public ServerLoadStatus LoadStatus { get; set; } = ServerLoadStatus.Loaded;
    public Func<Task<ServerLoadStatus>>? LoadStatusOverride { get; set; }
    public Task<ServerLoadStatus> GetLoadStatusAsync(CancellationToken cancellationToken = default) => LoadStatusOverride?.Invoke() ?? Task.FromResult(LoadStatus);

    public event EventHandler? ServersChanged;

    public List<Server> Servers { get; } = [];

    public Func<CancellationToken, Task<IReadOnlyList<Server>>>? GetAllOverride { get; set; }

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetAllOverride?.Invoke(cancellationToken)
            ?? Task.FromResult<IReadOnlyList<Server>>(Servers.ToList());

    public void RaiseChanged() => ServersChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>UI.9: how many handlers are subscribed, so a test can prove an unsubscribe.</summary>
    public int ServersChangedHandlerCount => ServersChanged?.GetInvocationList().Length ?? 0;

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>UI.5: opt-in Hide behaviour; without it Hide throws (the UI.4 error-path tests rely on that).</summary>
    public Func<Guid, Task<bool>>? HideOverride { get; set; }

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) =>
        HideOverride?.Invoke(id) ?? throw new NotSupportedException();

    /// <summary>UI.5: opt-in Restore behaviour; without it Restore throws.</summary>
    public Func<Guid, Task<bool>>? RestoreOverride { get; set; }

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
        RestoreOverride?.Invoke(id) ?? throw new NotSupportedException();
}
