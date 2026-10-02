using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

// QA-ONLY. Excluded from Release builds and only wired when launched with --qa-history. It lets the
// real HistoryPage / charts be inspected across every shape of data (spike, offline gap, null RAM,
// empty, unavailable, 1h/7d/30d) without waiting days or touching real servers. Nothing here ships.

internal enum QaHistoryKind
{
    Normal,
    CpuSpike,
    Warning,
    Critical,
    OfflineGap,
    Recovery,
    RamNull,
    Empty,
    Unavailable,
    // UI.3 phase 2 (Figma/QA shapes, selectable with the in-page server selector):
    Figma,
    EmptyPeriod,
    Loading,
    Extremes,
    SinglePoint,
    FewPoints
}

internal sealed record QaHistoryScenario
{
    public required string Label { get; init; }

    public required Server Server { get; init; }

    public ServerMetricsSnapshot? Snapshot { get; init; }

    public required ServerMonitoringState State { get; init; }

    public required QaHistoryKind Kind { get; init; }
}

/// <summary>
/// Deterministic history scenarios. Each server renders a distinct data shape; the sample generator
/// is a pure function of the requested window so every range (1h/6h/24h/7d/30d) is exercisable and
/// stable across a QA session.
/// </summary>
internal static class QaHistoryCatalog
{
    private const int PointsPerQuery = 600;

    // Fixed once so datasets and downsampling are stable for the whole QA session.
    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static IReadOnlyList<QaHistoryScenario> Scenarios { get; } = Build();

    public static IReadOnlyList<Server> Servers { get; } = Scenarios.Select(s => s.Server).ToList();

    public static QaHistoryScenario? For(Guid serverId) =>
        Scenarios.FirstOrDefault(s => s.Server.Id == serverId);

    public static ServerMetricsSnapshot? SnapshotFor(Guid serverId) => For(serverId)?.Snapshot;

    public static ServerMonitoringState StateFor(Guid serverId) =>
        For(serverId)?.State ?? ServerMonitoringState.Initial(serverId);

    /// <summary>Generates a deterministic sample series over [start,end] for a scenario. Offline and
    /// null-RAM regions preserve <c>null</c> (never 0); Empty yields no samples.</summary>
    public static IReadOnlyList<ServerHistorySample> Generate(
        QaHistoryScenario scenario,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        if (scenario.Kind is QaHistoryKind.Empty or QaHistoryKind.Unavailable or QaHistoryKind.Loading)
        {
            return Array.Empty<ServerHistorySample>();
        }

        if (scenario.Kind == QaHistoryKind.SinglePoint)
        {
            var single = startUtc + TimeSpan.FromTicks((long)((endUtc - startUtc).Ticks * 0.7));
            return [Value(QaHistoryKind.Normal, scenario.Server.Id, single, 0.7)];
        }

        if (scenario.Kind == QaHistoryKind.FewPoints)
        {
            // Six samples across the range: each sits alone between gaps wider than the connect gap.
            return Enumerable.Range(0, 6)
                .Select(i => Value(QaHistoryKind.Normal, scenario.Server.Id, startUtc + TimeSpan.FromTicks((endUtc - startUtc).Ticks / 6 * i + 1), i / 6.0))
                .ToArray();
        }

        if (scenario.Kind == QaHistoryKind.EmptyPeriod)
        {
            // Readings exist only older than two days: 1 h / 6 h / 24 h are empty, 7 / 30 days are not (D-UI3-10).
            endUtc = endUtc < Now.AddDays(-2) ? endUtc : Now.AddDays(-2);
            if (endUtc <= startUtc)
            {
                return Array.Empty<ServerHistorySample>();
            }
        }

        var samples = new List<ServerHistorySample>(PointsPerQuery);
        var totalTicks = (endUtc - startUtc).Ticks;
        for (var i = 0; i <= PointsPerQuery; i++)
        {
            var frac = (double)i / PointsPerQuery;
            var at = startUtc + TimeSpan.FromTicks((long)(totalTicks * frac));
            samples.Add(Value(scenario.Kind, scenario.Server.Id, at, frac));
        }

        return samples;
    }

    private static ServerHistorySample Value(QaHistoryKind kind, Guid id, DateTimeOffset at, double f)
    {
        double? cpu;
        double? mem;
        double? disk;
        ServerHealth health;

        switch (kind)
        {
            case QaHistoryKind.CpuSpike:
                var spiking = f is > 0.57 and < 0.63;
                cpu = spiking ? 94 : Clamp(16 + 6 * Math.Sin(f * 2 * Math.PI * 10), 2, 40);
                mem = Clamp(46 + 5 * Math.Sin(f * 2 * Math.PI * 2), 30, 70);
                disk = 61;
                health = spiking ? ServerHealth.Critical : ServerHealth.Healthy;
                break;

            case QaHistoryKind.Warning:
                cpu = Clamp(76 + 5 * Math.Sin(f * 2 * Math.PI * 4), 60, 88);
                mem = 55;
                disk = 62;
                health = ServerHealth.Warning;
                break;

            case QaHistoryKind.Critical:
                cpu = Clamp(92 + 4 * Math.Sin(f * 2 * Math.PI * 4), 80, 99);
                mem = 71;
                disk = Clamp(94 + 3 * Math.Sin(f * 2 * Math.PI * 3), 80, 99);
                health = ServerHealth.Critical;
                break;

            case QaHistoryKind.OfflineGap:
                if (f is >= 0.40 and <= 0.55)
                {
                    return Offline(id, at);
                }

                (cpu, mem, disk, health) = Steady(f);
                break;

            case QaHistoryKind.Recovery:
                if (f < 0.18)
                {
                    return Offline(id, at);
                }

                (cpu, mem, disk, health) = Steady(f);
                break;

            case QaHistoryKind.Figma:
                // Figma 112:2290: flat CPU with one spike ~78 %, memory drifting 52 -> 66 %, disk flat ~48 %.
                cpu = Clamp(24 + 3 * Math.Sin(f * 2 * Math.PI * 9) + (f is > 0.45 and < 0.52 ? 54 * (1 - Math.Abs(f - 0.485) / 0.035) : 0), 2, 100);
                mem = Clamp(52 + 14 * f + 2 * Math.Sin(f * 2 * Math.PI * 4), 0, 100);
                disk = Clamp(47 + 2 * f, 0, 100);
                health = ServerHealth.Healthy;
                break;

            case QaHistoryKind.Extremes:
                // 0 / 100 edges: CPU saturates then drops to idle, memory pinned at 100, disk at 0.
                cpu = f < 0.25 ? 0 : f < 0.5 ? 100 : f < 0.75 ? 50 + 50 * Math.Sin(f * 2 * Math.PI * 12) : 0;
                mem = 100;
                disk = 0;
                health = ServerHealth.Critical;
                break;

            case QaHistoryKind.RamNull:
                (cpu, _, disk, health) = Steady(f);
                mem = null; // memory unknown — the memory chart must show a gap, never 0.
                break;

            default: // Normal
                (cpu, mem, disk, health) = Steady(f);
                break;
        }

        return new ServerHistorySample
        {
            ServerId = id,
            CapturedAtUtc = at,
            Health = health,
            CpuPercent = cpu,
            MemoryPercent = mem,
            DiskPercent = disk
        };
    }

    private static (double? Cpu, double? Mem, double? Disk, ServerHealth Health) Steady(double f) =>
    (
        Clamp(20 + 12 * Math.Sin(f * 2 * Math.PI * 3) + 5 * Math.Sin(f * 2 * Math.PI * 20), 2, 60),
        Clamp(50 + 6 * Math.Sin(f * 2 * Math.PI * 1.5), 30, 70),
        Clamp(58 + f * 4, 0, 100),
        ServerHealth.Healthy
    );

    private static ServerHistorySample Offline(Guid id, DateTimeOffset at) => new()
    {
        ServerId = id,
        CapturedAtUtc = at,
        Health = ServerHealth.Offline,
        CpuPercent = null,
        MemoryPercent = null,
        DiskPercent = null
    };

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    private static IReadOnlyList<QaHistoryScenario> Build()
    {
        var order = 0;
        var scenarios = new List<QaHistoryScenario>
        {
            Make("Normal", QaHistoryKind.Normal, ref order, Snapshot(24, 50, 60), ServerHealth.Healthy),
            Make("CPU spike", QaHistoryKind.CpuSpike, ref order, Snapshot(18, 45, 61), ServerHealth.Healthy),
            Make("Warning", QaHistoryKind.Warning, ref order, Snapshot(80, 55, 62), ServerHealth.Warning),
            Make("Critical", QaHistoryKind.Critical, ref order, Snapshot(95, 71, 95), ServerHealth.Critical),
            Make("Offline gap", QaHistoryKind.OfflineGap, ref order, Snapshot(22, 50, 60), ServerHealth.Offline),
            Make("Recovery", QaHistoryKind.Recovery, ref order, Snapshot(24, 50, 60), ServerHealth.Healthy),
            Make("RAM null", QaHistoryKind.RamNull, ref order, Snapshot(24, null, 60), ServerHealth.Healthy),
            Make("Empty", QaHistoryKind.Empty, ref order, snapshot: null, ServerHealth.Unknown),
            Make("DB unavailable", QaHistoryKind.Unavailable, ref order, snapshot: null, ServerHealth.Unknown),
            Make("Figma", QaHistoryKind.Figma, ref order, Snapshot(24, 62, 48), ServerHealth.Healthy),
            Make("Empty period", QaHistoryKind.EmptyPeriod, ref order, Snapshot(24, 50, 60), ServerHealth.Healthy),
            Make("Loading", QaHistoryKind.Loading, ref order, Snapshot(24, 50, 60), ServerHealth.Healthy),
            Make("Extremes 0-100", QaHistoryKind.Extremes, ref order, Snapshot(0, 100, 0), ServerHealth.Critical),
            Make("Single point", QaHistoryKind.SinglePoint, ref order, Snapshot(24, 50, 60), ServerHealth.Healthy),
            Make("Few points", QaHistoryKind.FewPoints, ref order, Snapshot(24, 50, 60), ServerHealth.Healthy)
        };

        return scenarios;
    }

    private static QaHistoryScenario Make(
        string label,
        QaHistoryKind kind,
        ref int order,
        ServerMetricsSnapshot? snapshot,
        ServerHealth health)
    {
        var id = Guid.NewGuid();
        var server = new Server
        {
            Id = id,
            Name = $"QA · {label}",
            Host = $"qa-history-{order}.local",
            Port = 22,
            Username = "qa",
            OperatingSystem = ServerOperatingSystem.Linux,
            RefreshIntervalSeconds = 30,
            CreatedAt = Now.AddSeconds(order++)
        };

        return new QaHistoryScenario
        {
            Label = label,
            Server = server,
            Snapshot = snapshot is null ? null : snapshot with { ServerId = id },
            State = new ServerMonitoringState
            {
                ServerId = id,
                Health = health,
                LastSuccessAt = Now,
                LastAttemptAt = Now
            },
            Kind = kind
        };
    }

    private static ServerMetricsSnapshot Snapshot(double? cpu, double? mem, double? disk) => new()
    {
        ServerId = Guid.Empty,
        CollectedAt = Now,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
        OperatingSystemName = "Ubuntu",
        OperatingSystemVersion = "24.04 LTS"
    };
}
