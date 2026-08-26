using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M9 compact-widget harness. Launch with <c>--qa-compact</c> for the default
/// eight servers, or <c>--qa-compact:N</c> (0–40) to inspect empty / single / many-server layouts and
/// internal scrolling. It reuses the inert QA data plane and additionally forces the window to open in
/// Compact mode via an in-memory placement store, so no SSH, scheduling, persistence or real
/// window-placement file is touched. Excluded from Release.
/// </summary>
internal static class QaCompactComposition
{
    public const string LaunchFlag = "--qa-compact";
    private const int DefaultCount = 8;

    public static bool IsRequested() => TryGetCount(out _);

    public static void Apply(IServiceCollection services)
    {
        _ = TryGetCount(out var count);
        var catalog = QaCompactCatalog.Build(count);

        services.AddSingleton<IServerService>(new QaCompactServerService(catalog));
        services.AddSingleton<IServerMetricsStore>(new QaCompactMetricsStore(catalog));
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));

        var stateStore = new ServerMonitoringStateStore();
        foreach (var scenario in catalog.Scenarios)
        {
            stateStore.Set(scenario.State);
        }

        services.AddSingleton<IServerMonitoringStateStore>(stateStore);

        // Force the window to open in Compact mode without reading/writing the real placement file.
        services.AddSingleton<IWindowPlacementStore>(new QaCompactPlacementStore());
    }

    private static bool TryGetCount(out int count)
    {
        count = DefaultCount;
        foreach (var argument in Environment.GetCommandLineArgs())
        {
            if (string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (argument.StartsWith(LaunchFlag + ":", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = argument[(LaunchFlag.Length + 1)..];
                if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    count = parsed;
                }

                return true;
            }
        }

        return false;
    }

    private sealed class QaCompactServerService(QaCompactCatalog catalog) : IServerService
    {
        public event EventHandler? ServersChanged { add { } remove { } }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(catalog.Servers);

        public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA compact harness is read-only.");

        public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA compact harness is read-only.");

        public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA compact harness is read-only.");

        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class QaCompactMetricsStore(QaCompactCatalog catalog) : IServerMetricsStore
    {
        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => catalog.SnapshotFor(serverId);

        public Task<ServerMetricsCollectionResult> RefreshAsync(
            Server server,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerMetricsCollectionResult.Failure(
                ServerMonitor.Core.Enums.MetricsCollectionErrorCode.Unexpected));

        public void Remove(Guid serverId)
        {
        }
    }

    private sealed class QaCompactPlacementStore : IWindowPlacementStore
    {
        public WindowPlacementSettings Load() => new() { Mode = WindowMode.Compact };

        public void Save(WindowPlacementSettings settings)
        {
            // No-op: the harness never persists placement.
        }
    }
}
