using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M11 read-only workloads harness. Activated only with <see cref="LaunchFlag"/>.
/// It replaces the data plane with deterministic in-memory doubles and pre-populates the workload store
/// with one server per shape from <see cref="QaWorkloadsCatalog"/>, so the real Docker/services UI can be
/// inspected across availability failures, empty/healthy/unhealthy/stopped, 50/500 containers, 100/2000
/// services, truncation above the cap, systemd/launchd/unsupported managers, stale carry-over and hostile
/// (sanitized) names — with no SSH, Docker host or service manager. The real monitoring engine, workload
/// collector service and cadence observer are not registered in QA mode (the caller guards them).
/// Excluded from Release (Qa\**\*.cs is Compile-Removed for non-Debug builds).
/// </summary>
internal static class QaWorkloadsComposition
{
    public const string LaunchFlag = "--qa-workloads";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    public static void Apply(IServiceCollection services)
    {
        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton<IServerService, QaWorkloadsServerService>();
        services.AddSingleton<IServerMetricsStore, QaWorkloadsMetricsStore>();
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));

        var stateStore = new ServerMonitoringStateStore();
        foreach (var scenario in QaWorkloadsCatalog.Scenarios)
        {
            stateStore.Set(scenario.State);
        }

        services.AddSingleton<IServerMonitoringStateStore>(stateStore);

        // Pre-populate the real in-memory workload store so the UI shows every shape immediately.
        var workloadStore = new InMemoryServerWorkloadStore();
        foreach (var scenario in QaWorkloadsCatalog.Scenarios)
        {
            workloadStore.Set(scenario.Workload);
        }

        services.AddSingleton<IServerWorkloadStore>(workloadStore);

        // The deterministic fake collector (per requirement) and a refresh coordinator that re-serves the
        // catalog shape into the store, so a manual refresh in the UI works without the real pipeline.
        services.AddSingleton<IWorkloadCollector, QaWorkloadCollector>();
        services.AddSingleton<IWorkloadRefreshCoordinator>(new QaWorkloadRefreshCoordinator(workloadStore));
    }

    private sealed class QaWorkloadsServerService : IServerService
    {
        public event EventHandler? ServersChanged { add { } remove { } }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(QaWorkloadsCatalog.Servers);

        public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA workloads harness is read-only.");

        public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA workloads harness is read-only.");

        public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA workloads harness is read-only.");

        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class QaWorkloadsMetricsStore : IServerMetricsStore
    {
        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => QaWorkloadsCatalog.SnapshotFor(serverId);

        public Task<ServerMetricsCollectionResult> RefreshAsync(
            Server server,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

        public void Remove(Guid serverId)
        {
        }
    }

    /// <summary>Re-serves the catalog snapshot into the store on manual refresh — deterministic, no SSH.</summary>
    private sealed class QaWorkloadRefreshCoordinator(IServerWorkloadStore store) : IWorkloadRefreshCoordinator
    {
        public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default)
        {
            var workload = QaWorkloadsCatalog.WorkloadFor(serverId);
            if (workload is not null)
            {
                store.Set(workload);
            }

            return Task.CompletedTask;
        }
    }
}
