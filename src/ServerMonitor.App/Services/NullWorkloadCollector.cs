using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Services;

/// <summary>
/// Inert default <see cref="IWorkloadCollector"/>: returns an all-<c>Unknown</c> fresh attempt without
/// touching SSH. Used until platform-infra's real collector (Infrastructure/Collectors, ADR-016) is
/// wired in, and in any composition where workloads should stay dormant, so the app builds and runs and
/// the workload UI degrades to "unknown" gracefully.
/// </summary>
public sealed class NullWorkloadCollector : IWorkloadCollector
{
    private readonly TimeProvider _timeProvider;

    public NullWorkloadCollector(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ServerWorkloadSnapshot> CollectAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Task.FromResult(ServerWorkloadSnapshot.Initial(server.Id, _timeProvider.GetUtcNow()));
    }
}
