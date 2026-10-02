using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY read-only <see cref="IServerService"/> for --qa-overview. Serves the scenario's servers (hidden ones included,
/// so the Servidores note is real) and never persists anything. The <c>loading</c> scenario's load never completes, so
/// the loading state can be inspected for as long as needed. Mutations are inert or unsupported, like the other harnesses.
/// </summary>
internal sealed class QaOverviewServerService(QaOverviewScenario scenario) : IServerService
{
    private readonly TaskCompletionSource<IReadOnlyList<Server>> _neverCompletes = new();

    public event EventHandler? ServersChanged { add { } remove { } }

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
        scenario.NeverLoads
            ? _neverCompletes.Task
            : Task.FromResult<IReadOnlyList<Server>>(scenario.Servers.Select(entry => entry.Server).ToList());

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA overview harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA overview harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA overview harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>QA-ONLY <see cref="IServerMetricsStore"/>: the scenario's retained snapshot per server (null = no data).</summary>
internal sealed class QaOverviewMetricsStore(QaOverviewScenario scenario) : IServerMetricsStore
{
    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) =>
        scenario.Servers.FirstOrDefault(entry => entry.Server.Id == serverId)?.Snapshot;

    public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

    public void Remove(Guid serverId)
    {
        // No-op: QA snapshots are immutable and in-memory.
    }
}
