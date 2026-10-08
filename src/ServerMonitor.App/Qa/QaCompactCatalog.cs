using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

// QA-ONLY. Excluded from Release (see ServerMonitor.App.csproj); wired only under --qa-compact.

/// <summary>
/// UI.8: the deterministic catalogue of the Compact Mode harness, one closed list of named scenarios
/// (<see cref="Scenarios"/>). Ids, names, timestamps and readings are fixed (no <c>Guid.NewGuid</c>, no wall clock), so
/// every run and every test sees the same servers. Reachable health states are exactly what the engine derives from the
/// snapshot (<see cref="HealthEvaluator"/>), so nothing is invented for the screen. Purely in-memory.
/// </summary>
internal sealed class QaCompactCatalog
{
    /// <summary>The fixed instant every reading and the presentation clock are anchored to.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The scenario a launch without <c>--qa-compact-scenario</c> uses (the Figma frame's six servers).</summary>
    public const string DefaultScenario = "figma";

    /// <summary>The closed list (SPEC §3). Anything else is refused, never replaced by another scenario.</summary>
    public static IReadOnlyList<string> Scenarios { get; } =
    [
        "empty", "one", "many", "mixed", "figma", "offline", "stale", "loading", "all-hidden", "config-unavailable",
        "long-names", "n20", "n100", "n200"
    ];

    private QaCompactCatalog(string name, IReadOnlyList<QaHealthScenario> entries)
    {
        Name = name;
        Entries = entries;
        Servers = entries.Select(entry => entry.Server).ToList();
    }

    public string Name { get; }

    public IReadOnlyList<QaHealthScenario> Entries { get; }

    public IReadOnlyList<Server> Servers { get; }

    /// <summary>The load never completes, so the loading state can be inspected for as long as needed.</summary>
    public bool NeverLoads => Name == "loading";

    /// <summary>What the configuration diagnosis reports for this scenario.</summary>
    public ServerLoadStatus LoadStatus => Name == "config-unavailable" ? ServerLoadStatus.Unavailable : ServerLoadStatus.Loaded;

    public static bool IsKnown(string? name) => name is not null && Scenarios.Contains(name, StringComparer.Ordinal);

    public static QaCompactCatalog Build(string name)
    {
        if (!IsKnown(name))
        {
            throw new ArgumentException($"Unknown compact scenario '{name}'. Known: {string.Join(", ", Scenarios)}.", nameof(name));
        }

        List<QaHealthScenario> entries = name switch
        {
            "empty" or "config-unavailable" or "loading" => [],
            "one" => [Engine(0, "prod-web-01", 24, 62, 48)],
            "many" => Enumerable.Range(0, 12).Select(index => Engine(index, $"node-{index + 1:00}", 10 + index * 5, 30 + index * 3, 20 + index * 4)).ToList(),
            "mixed" => Enumerable.Range(0, 8).Select(Rotating).ToList(),
            "figma" => Figma(),
            "offline" => Enumerable.Range(0, 3).Select(index => Offline(index, $"edge-{index + 1:00}")).ToList(),
            "stale" => Enumerable.Range(0, 3).Select(index => Stale(index, $"archive-{index + 1:00}")).ToList(),
            "all-hidden" => Enumerable.Range(0, 3).Select(index => Hidden(Engine(index, $"hidden-{index + 1:00}", 20, 40, 50))).ToList(),
            "long-names" => LongNames(),
            "n20" => Enumerable.Range(0, 20).Select(Rotating).ToList(),
            "n100" => Enumerable.Range(0, 100).Select(Rotating).ToList(),
            "n200" => Enumerable.Range(0, 200).Select(Rotating).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };

        return new QaCompactCatalog(name, entries);
    }

    /// <summary>
    /// Figma 112:9397 (P§13): prod-web-01, prod-db-01 (attention on disk), staging-01, docker-host, mac-mini, backup-nas
    /// (without connection). Deviation, as UI.4's overview catalogue: the Figma's DISCO 92 is Critical under the engine's
    /// real thresholds (disk critical = 90), so the attention row uses 88 - the engine classification wins over the frame.
    /// </summary>
    private static List<QaHealthScenario> Figma() =>
    [
        Engine(0, "prod-web-01", 24, 62, 48),
        Engine(1, "prod-db-01", 46, 71, 88),
        Engine(2, "staging-01", 8, 34, 28),
        Engine(3, "docker-host", 36, 58, 67),
        Engine(4, "mac-mini", 12, 42, 35),
        Offline(5, "backup-nas")
    ];

    private static List<QaHealthScenario> LongNames() =>
    [
        Engine(0, "production-postgresql-primary-eu-west-1a.internal.example", 24, 62, 48),
        Engine(1, "kubernetes-worker-node-with-an-unreasonably-long-hostname-07", 46, 71, 88),
        Offline(2, "backup-storage-array-in-the-basement-rack-number-twelve"),
        Engine(3, "x", 100, 100, 0)
    ];

    // Eight states in rotation: Healthy, Warning, Critical, Offline (retained), Stale, Unknown, Refreshing, Partial.
    private static QaHealthScenario Rotating(int index)
    {
        var name = $"qa-server-{index + 1:000}";
        return (index % 8) switch
        {
            0 => Engine(index, name, 22, 41, 52),
            1 => Engine(index, name, 84, 41, 52),
            2 => Engine(index, name, 20, 52, 93),
            3 => Offline(index, name),
            4 => Stale(index, name),
            5 => Unknown(index, name),
            6 => Refreshing(index, name),
            _ => Engine(index, name, 12, null, 51)
        };
    }

    // A fresh reading whose health is the engine's own rule over the snapshot.
    private static QaHealthScenario Engine(int index, string name, double? cpu, double? mem, double? disk)
    {
        var server = MakeServer(index, name);
        var snapshot = Snapshot(server.Id, cpu, mem, disk, Now);
        return Entry(server, snapshot, State(server.Id, HealthEvaluator.EvaluateFromMetrics(snapshot), Now, Now));
    }

    // Without connection, with a retained reading from 8 minutes before (not stale for the engine yet).
    private static QaHealthScenario Offline(int index, string name)
    {
        var server = MakeServer(index, name);
        var retained = Now.AddMinutes(-8);
        return Entry(
            server,
            Snapshot(server.Id, 30, 40, 50, retained),
            State(server.Id, ServerHealth.Offline, retained, Now, consecutiveFailures: 4, lastError: MetricsCollectionErrorCode.ConnectionFailed));
    }

    // Healthy by the retained reading, but stale: the last success is two hours older than the last attempt.
    private static QaHealthScenario Stale(int index, string name)
    {
        var server = MakeServer(index, name);
        var retained = Now.AddHours(-2);
        var snapshot = Snapshot(server.Id, 28, 44, 55, retained);
        return Entry(server, snapshot, State(server.Id, HealthEvaluator.EvaluateFromMetrics(snapshot), retained, Now, isStale: true));
    }

    private static QaHealthScenario Unknown(int index, string name)
    {
        var server = MakeServer(index, name);
        return Entry(server, null, State(server.Id, ServerHealth.Unknown));
    }

    private static QaHealthScenario Refreshing(int index, string name)
    {
        var server = MakeServer(index, name);
        var snapshot = Snapshot(server.Id, 35, 48, 60, Now);
        return Entry(server, snapshot, State(server.Id, HealthEvaluator.EvaluateFromMetrics(snapshot), Now, Now, isRefreshing: true));
    }

    private static QaHealthScenario Hidden(QaHealthScenario entry) => entry with { Server = entry.Server with { IsHidden = true } };

    private static QaHealthScenario Entry(Server server, ServerMetricsSnapshot? snapshot, ServerMonitoringState state) => new()
    {
        Label = server.Name,
        Server = server,
        Snapshot = snapshot,
        State = state
    };

    private static Server MakeServer(int index, string name) => new()
    {
        Id = StableId(index),
        Name = name,
        Host = $"qa-compact-{index + 1:000}.local",
        Port = 22,
        Username = "qa",
        OperatingSystem = index % 2 == 0 ? ServerOperatingSystem.Linux : ServerOperatingSystem.MacOS,
        RefreshIntervalSeconds = 30,
        CreatedAt = Now.AddSeconds(index)
    };

    /// <summary>Deterministic, harness-scoped ids: c0c0c0c0-0008-4000-8000-{index}.</summary>
    internal static Guid StableId(int index) => new($"c0c0c0c0-0008-4000-8000-{index:D12}");

    private static ServerMetricsSnapshot Snapshot(Guid id, double? cpu, double? mem, double? disk, DateTimeOffset collectedAt) => new()
    {
        ServerId = id,
        CollectedAt = collectedAt,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk
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
        LastError = lastError
    };
}
