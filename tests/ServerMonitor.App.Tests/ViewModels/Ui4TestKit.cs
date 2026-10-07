using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 test kit: a REAL <see cref="DashboardViewModel"/> (its constructor tolerates the missing WinUI dispatcher) over
/// in-memory servers, snapshots and engine states. Synthetic data only: .local hosts, no path, credential or network.
/// </summary>
internal static class Ui4TestKit
{
    public static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public sealed record Entry(Server Server, ServerMetricsSnapshot? Snapshot, ServerMonitoringState State);

    public sealed class Fleet
    {
        public List<Entry> Entries { get; } = [];

        public Fleet Add(
            string name,
            ServerHealth health,
            double? cpu = null,
            double? mem = null,
            double? disk = null,
            bool snapshot = true,
            bool hidden = false,
            string? host = null,
            int port = 22,
            DateTimeOffset? lastSuccess = null)
        {
            var id = Guid.NewGuid();
            var server = new Server
            {
                Id = id,
                Name = name,
                Host = host ?? $"{name}.local",
                Port = port,
                Username = "qa",
                OperatingSystem = ServerOperatingSystem.Linux,
                CreatedAt = Now.AddMinutes(Entries.Count),
                IsHidden = hidden
            };
            var success = lastSuccess ?? (snapshot ? Now.AddSeconds(-8) : null);
            Entries.Add(new Entry(
                server,
                snapshot
                    ? new ServerMetricsSnapshot
                    {
                        ServerId = id,
                        CollectedAt = success ?? Now,
                        CpuUsagePercent = cpu,
                        MemoryUsagePercent = mem,
                        DiskUsagePercent = disk
                    }
                    : null,
                new ServerMonitoringState { ServerId = id, Health = health, LastSuccessAt = success, LastAttemptAt = success }));
            return this;
        }

        public Guid IdOf(string name) => Entries.Single(entry => entry.Server.Name == name).Server.Id;
    }

    public sealed class Harness
    {
        public required DashboardViewModel Dashboard { get; init; }

        public required FakeNavigationService Navigation { get; init; }

        public required FakeServerService Servers { get; init; }

        public required DictionaryMetricsStore Metrics { get; init; }

        public required ServerMonitoringStateStore States { get; init; }

        public required ILocalizationService Localization { get; init; }

        public required FakeMonitoringEngine Engine { get; init; }

        public required FakeConnectionStateStore Connections { get; init; }
    }

    public static Harness Create(
        Fleet fleet,
        ILocalizationService? localization = null,
        MonitoringOptions? options = null,
        INavigationService? navigationOverride = null,
        IServerDialogService? dialogs = null,
        IServerProfileService? profiles = null,
        IServerEditorSession? editorSession = null)
    {
        var servers = new FakeServerService();
        servers.Servers.AddRange(fleet.Entries.Select(entry => entry.Server));
        var metrics = new DictionaryMetricsStore();
        var states = new ServerMonitoringStateStore();
        foreach (var entry in fleet.Entries)
        {
            if (entry.Snapshot is not null)
            {
                metrics.Snapshots[entry.Server.Id] = entry.Snapshot;
            }

            states.Set(entry.State);
        }

        var navigation = new FakeNavigationService();
        var localizer = localization ?? new FakeLocalizationService();
        var engine = new FakeMonitoringEngine();
        var connections = new FakeConnectionStateStore();
        var dashboard = new DashboardViewModel(
            servers,
            profiles ?? new InertProfileService(),
            dialogs ?? new InertDialogService(),
            editorSession ?? (dialogs is IEditorScript script ? new EditorScriptSession(script) : new InertEditorSession()),
            connections,
            metrics,
            states,
            engine,
            new InertDiscoveryService(),
            navigationOverride ?? navigation,
            localizer,
            NullLogger<DashboardViewModel>.Instance,
            refreshAllCoordinator: null,
            monitoringOptions: options,
            clock: new PresentationClock(new FakeTimeProvider(Now)));
        return new Harness
        {
            Dashboard = dashboard,
            Navigation = navigation,
            Servers = servers,
            Metrics = metrics,
            States = states,
            Localization = localizer,
            Engine = engine,
            Connections = connections
        };
    }

    internal sealed class DictionaryMetricsStore : IServerMetricsStore
    {
        public Dictionary<Guid, ServerMetricsSnapshot> Snapshots { get; } = [];

        public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => Snapshots.GetValueOrDefault(serverId);

        public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

        public void Remove(Guid serverId) => Snapshots.Remove(serverId);
    }

    private sealed class InertProfileService : IServerProfileService
    {
        public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerOperationResult> UpdateAsync(Server current, ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class InertDialogService : IEditorScript
    {
        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() => Task.FromResult<ServerEditorResult?>(null);

        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(false);
    }

    private sealed class InertDiscoveryService : IServerDiscoveryService
    {
        public event EventHandler? DiscoveredChanged { add { } remove { } }

        public IReadOnlyList<DiscoveredService> GetDiscovered() => [];

        public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
