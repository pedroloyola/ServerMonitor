namespace ServerMonitor.App.Services;

/// <summary>
/// Inert default <see cref="IWorkloadRefreshCoordinator"/>: a manual refresh is a no-op. Registered as
/// the common-area default so the Workloads UI resolves in every composition; the real
/// <see cref="WorkloadCollectorService"/> (non-QA) and the QA harness override it.
/// </summary>
public sealed class NullWorkloadRefreshCoordinator : IWorkloadRefreshCoordinator
{
    public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
