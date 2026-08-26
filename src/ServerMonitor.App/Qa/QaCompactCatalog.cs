using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

// QA-ONLY. Excluded from Release (see ServerMonitor.App.csproj); wired only under --qa-compact.
// Lets the real compact widget be inspected at any server count (0 / 1 / 2 / 8 / 20) across every
// health state without touching real servers, SSH, persistence or credentials.

/// <summary>
/// A deterministic, variable-length catalog for the compact-widget harness. Servers cycle through
/// the eight monitoring states so a run of N cards shows Healthy/Warning/Critical/Offline/Stale/
/// Unknown/Refreshing/Partial in rotation. Purely in-memory.
/// </summary>
internal sealed class QaCompactCatalog
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private QaCompactCatalog(IReadOnlyList<QaHealthScenario> scenarios)
    {
        Scenarios = scenarios;
        Servers = scenarios.Select(scenario => scenario.Server).ToList();
    }

    public IReadOnlyList<QaHealthScenario> Scenarios { get; }

    public IReadOnlyList<Server> Servers { get; }

    public ServerMetricsSnapshot? SnapshotFor(Guid serverId) =>
        Scenarios.FirstOrDefault(scenario => scenario.Server.Id == serverId)?.Snapshot;

    public static QaCompactCatalog Build(int count)
    {
        var clamped = Math.Clamp(count, 0, 40);
        var scenarios = new List<QaHealthScenario>(clamped);
        for (var index = 0; index < clamped; index++)
        {
            scenarios.Add(BuildScenario(index));
        }

        return new QaCompactCatalog(scenarios);
    }

    private static QaHealthScenario BuildScenario(int index)
    {
        var os = index % 2 == 0 ? ServerOperatingSystem.Linux : ServerOperatingSystem.MacOS;
        var id = Guid.NewGuid();
        var server = new Server
        {
            Id = id,
            Name = $"QA Server {index + 1:00}",
            Host = $"qa-{index + 1:00}.local",
            Port = 22,
            Username = "qa",
            OperatingSystem = os,
            RefreshIntervalSeconds = 30,
            CreatedAt = Now.AddSeconds(index),
        };

        var (snapshot, state) = ScenarioFor(index % 8, id);
        return new QaHealthScenario
        {
            Label = $"Compact {index + 1}",
            Server = server,
            Snapshot = snapshot,
            State = state,
        };
    }

    private static (ServerMetricsSnapshot? Snapshot, ServerMonitoringState State) ScenarioFor(int bucket, Guid id)
    {
        return bucket switch
        {
            0 => (Snapshot(id, 22, 41, 52), State(id, ServerHealth.Healthy, Now, Now)),
            1 => (Snapshot(id, 84, 41, 52), State(id, ServerHealth.Warning, Now, Now)),
            2 => (Snapshot(id, 20, 52, 93), State(id, ServerHealth.Critical, Now, Now)),
            3 => (Snapshot(id, 30, 40, 50, Now.AddMinutes(-8)),
                    State(id, ServerHealth.Offline, Now.AddMinutes(-8), Now, consecutiveFailures: 4,
                        lastError: MetricsCollectionErrorCode.ConnectionFailed)),
            4 => (Snapshot(id, 28, 44, 55, Now.AddHours(-2)),
                    State(id, ServerHealth.Healthy, Now.AddHours(-2), Now, isStale: true)),
            5 => (null, State(id, ServerHealth.Unknown)),
            6 => (Snapshot(id, 35, 48, 60), State(id, ServerHealth.Healthy, Now, Now, isRefreshing: true)),
            _ => (Snapshot(id, 12, null, 51), State(id, ServerHealth.Healthy, Now, Now)),
        };
    }

    private static ServerMetricsSnapshot Snapshot(
        Guid id, double? cpu, double? mem, double? disk, DateTimeOffset? collectedAt = null) => new()
    {
        ServerId = id,
        CollectedAt = collectedAt ?? Now,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
    };

    private static ServerMonitoringState State(
        Guid id,
        ServerHealth health,
        DateTimeOffset? lastSuccess = null,
        DateTimeOffset? lastAttempt = null,
        bool isRefreshing = false,
        bool isStale = false,
        int consecutiveFailures = 0,
        MetricsCollectionErrorCode? lastError = null) => new()
    {
        ServerId = id,
        Health = health,
        IsRefreshing = isRefreshing,
        IsStale = isStale,
        LastSuccessAt = lastSuccess,
        LastAttemptAt = lastAttempt,
        ConsecutiveFailures = consecutiveFailures,
        LastError = lastError,
    };
}
