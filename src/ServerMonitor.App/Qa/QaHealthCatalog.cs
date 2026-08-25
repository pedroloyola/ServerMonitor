using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

// QA-ONLY. This whole folder is excluded from Release builds (see ServerMonitor.App.csproj)
// and is only wired into DI when the app is launched with the --qa-health flag. It exists so
// the real ServerFullCard / DashboardPage can be inspected in every monitoring state without
// touching real servers, SSH, persistence or credentials. Nothing here is shipped.

/// <summary>One deterministic health scenario: a QA server plus the exact snapshot and
/// monitoring state the real card should render for it.</summary>
internal sealed record QaHealthScenario
{
    public required string Label { get; init; }

    public required Server Server { get; init; }

    /// <summary>Retained metrics snapshot, or <c>null</c> to exercise the "no data" paths
    /// (Unknown). A missing metric inside a snapshot stays <c>null</c> — never 0 (unknown ≠ zero).</summary>
    public ServerMetricsSnapshot? Snapshot { get; init; }

    public required ServerMonitoringState State { get; init; }
}

/// <summary>
/// Deterministic catalog of the eight monitoring scenarios required by the M6 visual health QA
/// (Healthy, Warning, Critical, Offline, Stale, Unknown, Refreshing, Partial), built once for
/// both Linux and macOS so the same card renders every state. Purely in-memory.
/// </summary>
internal static class QaHealthCatalog
{
    // Captured once so timestamps and stale ages are stable for the whole QA session.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static IReadOnlyList<QaHealthScenario> Scenarios { get; } = Build();

    public static IReadOnlyList<Server> Servers { get; } =
        Scenarios.Select(scenario => scenario.Server).ToList();

    public static ServerMetricsSnapshot? SnapshotFor(Guid serverId) =>
        Scenarios.FirstOrDefault(scenario => scenario.Server.Id == serverId)?.Snapshot;

    public static ServerMonitoringState StateFor(Guid serverId) =>
        Scenarios.FirstOrDefault(scenario => scenario.Server.Id == serverId)?.State
        ?? ServerMonitoringState.Initial(serverId);

    private static IReadOnlyList<QaHealthScenario> Build()
    {
        var scenarios = new List<QaHealthScenario>();
        var order = 0;

        foreach (var os in new[] { ServerOperatingSystem.Linux, ServerOperatingSystem.MacOS })
        {
            // Healthy — everything comfortably within warning thresholds.
            scenarios.Add(Make("Healthy", os, ref order,
                Snapshot(cpu: 22, mem: 41, disk: 52),
                State(ServerHealth.Healthy, lastSuccess: Now, lastAttempt: Now)));

            // Warning — CPU crossed the warning threshold; other metrics fine.
            scenarios.Add(Make("Warning", os, ref order,
                Snapshot(cpu: 84, mem: 41, disk: 52),
                State(ServerHealth.Warning, lastSuccess: Now, lastAttempt: Now)));

            // Critical — disk crossed the critical threshold.
            scenarios.Add(Make("Critical", os, ref order,
                Snapshot(cpu: 20, mem: 52, disk: 93),
                State(ServerHealth.Critical, lastSuccess: Now, lastAttempt: Now)));

            // Offline — retries exhausted, but a prior snapshot is retained and stays visible.
            scenarios.Add(Make("Offline", os, ref order,
                Snapshot(cpu: 30, mem: 40, disk: 50, collectedAt: Now.AddMinutes(-10)),
                State(ServerHealth.Offline, lastSuccess: Now.AddMinutes(-10), lastAttempt: Now,
                    consecutiveFailures: 4, lastError: MetricsCollectionErrorCode.ConnectionFailed)));

            // Stale — last success is old; the discreet "updated N ago" indicator should show.
            scenarios.Add(Make("Stale", os, ref order,
                Snapshot(cpu: 28, mem: 44, disk: 55, collectedAt: Now.AddHours(-2)),
                State(ServerHealth.Healthy, lastSuccess: Now.AddHours(-2), lastAttempt: Now,
                    isStale: true)));

            // Unknown — no usable data yet; the pending/unknown path, not zeroed metrics.
            scenarios.Add(Make("Unknown", os, ref order,
                snapshot: null,
                State(ServerHealth.Unknown)));

            // Refreshing — a cycle is in flight over existing data (ProgressRing visible).
            scenarios.Add(Make("Refreshing", os, ref order,
                Snapshot(cpu: 35, mem: 48, disk: 60),
                State(ServerHealth.Healthy, lastSuccess: Now, lastAttempt: Now, isRefreshing: true)));

            // Partial — CPU and disk known, memory unknown. Memory must render as absent, never 0.
            scenarios.Add(Make("Partial", os, ref order,
                Snapshot(cpu: 12, mem: null, disk: 51),
                State(ServerHealth.Healthy, lastSuccess: Now, lastAttempt: Now)));
        }

        return scenarios;
    }

    private static QaHealthScenario Make(
        string label,
        ServerOperatingSystem os,
        ref int order,
        ServerMetricsSnapshot? snapshot,
        ServerMonitoringState stateWithoutId)
    {
        var id = Guid.NewGuid();
        var osShort = os == ServerOperatingSystem.MacOS ? "macOS" : "Linux";
        var server = new Server
        {
            Id = id,
            Name = $"QA · {label} · {osShort}",
            Host = $"qa-{label.ToLowerInvariant()}-{osShort.ToLowerInvariant()}.local",
            Port = 22,
            Username = "qa",
            OperatingSystem = os,
            RefreshIntervalSeconds = 30,
            CreatedAt = Now.AddSeconds(order++),
        };

        return new QaHealthScenario
        {
            Label = label,
            Server = server,
            Snapshot = snapshot is null ? null : snapshot with { ServerId = id },
            State = stateWithoutId with { ServerId = id },
        };
    }

    private static ServerMetricsSnapshot Snapshot(
        double? cpu = null,
        double? mem = null,
        double? disk = null,
        DateTimeOffset? collectedAt = null) => new()
    {
        ServerId = Guid.Empty, // replaced with the scenario's server id in Make.
        CollectedAt = collectedAt ?? Now,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
    };

    private static ServerMonitoringState State(
        ServerHealth health,
        bool isRefreshing = false,
        bool isStale = false,
        DateTimeOffset? lastSuccess = null,
        DateTimeOffset? lastAttempt = null,
        int consecutiveFailures = 0,
        MetricsCollectionErrorCode? lastError = null) => new()
    {
        ServerId = Guid.Empty, // replaced with the scenario's server id in Make.
        Health = health,
        IsRefreshing = isRefreshing,
        IsStale = isStale,
        LastSuccessAt = lastSuccess,
        LastAttemptAt = lastAttempt,
        ConsecutiveFailures = consecutiveFailures,
        LastError = lastError,
    };
}
