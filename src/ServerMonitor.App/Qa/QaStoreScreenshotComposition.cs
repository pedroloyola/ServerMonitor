using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

// QA-ONLY. This whole folder is excluded from Release builds (see ServerMonitor.App.csproj) and is
// only wired into DI when the app is launched with --qa-store-screenshot. It exists so a clean,
// product-looking Dashboard can be captured for the Microsoft Store listing without touching real
// servers, SSH, persistence or credentials. All data is SYNTHETIC and contains no PII. Never shipped.
internal static class QaStoreScreenshotComposition
{
    public const string LaunchFlag = "--qa-store-screenshot";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static void Apply(IServiceCollection services)
    {
        var entries = BuildEntries();

        services.AddSingleton<IServerService>(
            new ScreenshotServerService(entries.Select(entry => entry.Server).ToList()));
        services.AddSingleton<IServerMetricsStore>(
            new ScreenshotMetricsStore(entries.ToDictionary(entry => entry.Server.Id, entry => entry.Snapshot)));

        // Inert facade + empty discovery, so nothing real runs and no suggestions appear.
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));

        var stateStore = new ServerMonitoringStateStore();
        foreach (var entry in entries)
        {
            stateStore.Set(entry.State);
        }

        services.AddSingleton<IServerMonitoringStateStore>(stateStore);
    }

    private static IReadOnlyList<Entry> BuildEntries()
    {
        var order = 0;
        return new[]
        {
            // Curated, plausible, all-healthy set for a hero shot. Hosts are generic private-LAN
            // placeholders (no real hostnames, IPs, usernames or PII).
            Make("Home Server", ServerOperatingSystem.Linux, cpu: 18, mem: 46, disk: 63, ref order),
            Make("Media Server", ServerOperatingSystem.Linux, cpu: 34, mem: 58, disk: 71, ref order),
            Make("Mac Mini", ServerOperatingSystem.MacOS, cpu: 12, mem: 42, disk: 37, ref order),
        };
    }

    private static Entry Make(
        string name,
        ServerOperatingSystem os,
        double cpu,
        double mem,
        double disk,
        ref int order)
    {
        var id = Guid.NewGuid();
        var server = new Server
        {
            Id = id,
            Name = name,
            Host = $"10.0.0.{20 + order}",
            Port = 22,
            Username = "admin",
            OperatingSystem = os,
            RefreshIntervalSeconds = 30,
            CreatedAt = Now.AddSeconds(order++),
        };

        var snapshot = new ServerMetricsSnapshot
        {
            ServerId = id,
            CollectedAt = Now,
            CpuUsagePercent = cpu,
            MemoryUsagePercent = mem,
            DiskUsagePercent = disk,
        };

        var state = new ServerMonitoringState
        {
            ServerId = id,
            Health = ServerHealth.Healthy,
            LastSuccessAt = Now,
            LastAttemptAt = Now,
        };

        return new Entry(server, snapshot, state);
    }

    private sealed record Entry(Server Server, ServerMetricsSnapshot Snapshot, ServerMonitoringState State);

    // Read-only server list; no persistence, no mutation (matches the QA health harness contract).
    private sealed class ScreenshotServerService : IServerService
    {
        private readonly IReadOnlyList<Server> _servers;

        public ScreenshotServerService(IReadOnlyList<Server> servers) => _servers = servers;

        public event EventHandler? ServersChanged { add { } remove { } }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_servers);

        public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA store-screenshot harness is read-only.");

        public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA store-screenshot harness is read-only.");

        public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA store-screenshot harness is read-only.");

        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    // Serves the curated snapshot per server; never performs a real collection.
    private sealed class ScreenshotMetricsStore : IServerMetricsStore
    {
        private readonly IReadOnlyDictionary<Guid, ServerMetricsSnapshot> _snapshots;

        public ScreenshotMetricsStore(IReadOnlyDictionary<Guid, ServerMetricsSnapshot> snapshots) =>
            _snapshots = snapshots;

        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) =>
            _snapshots.TryGetValue(serverId, out var snapshot) ? snapshot : null;

        public Task<ServerMetricsCollectionResult> RefreshAsync(
            Server server,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

        public void Remove(Guid serverId)
        {
            // No-op: synthetic snapshots are immutable and in-memory.
        }
    }
}
