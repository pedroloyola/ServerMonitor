using ServerMonitor.App.Services;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

// QA-ONLY. Excluded from Release with the rest of Qa/** (see ServerMonitor.App.csproj); wired only by --qa-overview.

/// <summary>One synthetic server of an overview scenario: configuration, retained snapshot (null = no data) and state.</summary>
internal sealed record QaOverviewServer(Server Server, ServerMetricsSnapshot? Snapshot, ServerMonitoringState State);

/// <summary>A whole overview scenario: the servers (hidden ones included), the discovery seed and whether the load hangs.</summary>
internal sealed record QaOverviewScenario(
    string Name,
    IReadOnlyList<QaOverviewServer> Servers,
    IReadOnlyList<DiscoveredService> Discovered,
    bool NeverLoads);

/// <summary>
/// UI.4 §6: deterministic, synthetic data for the Visão geral and Servidores screens. A FIXED clock (<see cref="Now"/>)
/// so "Atualizado há 8 s" (Figma 112:1000) renders the same on every run; every host is a <c>.local</c> name or a
/// documentation address (RFC 5737, 192.0.2.0/24) — no real host, path, credential or network. Health is the value the
/// engine would publish for the snapshot under <see cref="MonitoringThresholds.Default"/> (Offline/Unknown set directly,
/// as the engine does for reachability), so the overview's priority rule sees consistent input.
/// </summary>
internal static class QaOverviewCatalog
{
    public static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The most recent successful collection — 8 s before <see cref="Now"/>, as in the Figma header.</summary>
    public static readonly DateTimeOffset LastSuccess = Now.AddSeconds(-8);

    public static QaOverviewScenario Build(string name) => name switch
    {
        "healthy" => Scenario(name,
            Healthy("prod-web-01", "prod-web-01.local", cpu: 22, mem: 41, disk: 52),
            Healthy("prod-db-01", "prod-db-01.local", cpu: 31, mem: 63, disk: 71),
            Healthy("staging-01", "staging-01.local", cpu: 12, mem: 38, disk: 44, port: 2222),
            Healthy("docker-host", "192.0.2.10", cpu: 47, mem: 58, disk: 66),
            Healthy("mac-mini", "mac-mini.local", cpu: 9, mem: 52, disk: 39, os: ServerOperatingSystem.MacOS),
            Healthy("backup-nas", "backup-nas.local", cpu: 4, mem: 27, disk: 78)),

        // Figma 112:930 / 112:1353: 4 healthy, prod-db-01 in attention (disk), backup-nas without connection. The Figma
        // shows disk 92% as "Atenção"; under the engine's limits 92% disk is CRITICAL (≥ 90), so the harness uses 88%
        // (Warning) to keep the frame's states — a deliberate, documented difference (nothing fabricated in the app).
        "mixed" => Scenario(name,
            Healthy("prod-web-01", "prod-web-01.local", cpu: 22, mem: 41, disk: 52),
            Make("prod-db-01", "prod-db-01.local", cpu: 46, mem: 71, disk: 88, ServerHealth.Warning),
            Healthy("staging-01", "staging-01.local", cpu: 12, mem: 38, disk: 44, port: 2222),
            Healthy("docker-host", "192.0.2.10", cpu: 47, mem: 58, disk: 66),
            Healthy("mac-mini", "mac-mini.local", cpu: 9, mem: 52, disk: 39, os: ServerOperatingSystem.MacOS),
            Offline("backup-nas", "backup-nas.local", withPriorSnapshot: true),
            Hidden("old-build-01", "old-build-01.local")),

        // Several warnings, two at the SAME percentage (tie → list order), none critical.
        "attention" => Scenario(name,
            Make("api-01", "api-01.local", cpu: 84, mem: 52, disk: 61, ServerHealth.Warning),
            Make("api-02", "api-02.local", cpu: 40, mem: 86, disk: 61, ServerHealth.Warning),
            Make("files-01", "files-01.local", cpu: 18, mem: 44, disk: 86, ServerHealth.Warning),
            Healthy("web-01", "web-01.local", cpu: 25, mem: 41, disk: 50)),

        "critical" => Scenario(name,
            Make("api-01", "api-01.local", cpu: 88, mem: 52, disk: 61, ServerHealth.Warning),
            Make("db-01", "db-01.local", cpu: 35, mem: 61, disk: 93, ServerHealth.Critical),
            Make("cache-01", "cache-01.local", cpu: 97, mem: 70, disk: 30, ServerHealth.Critical),
            Healthy("web-01", "web-01.local", cpu: 25, mem: 41, disk: 50)),

        // Offline servers never become the priority problem: "Sem problemas" with two "sem ligação".
        "offline" => Scenario(name,
            Healthy("web-01", "web-01.local", cpu: 25, mem: 41, disk: 50),
            Offline("vpn-gw", "vpn-gw.local", withPriorSnapshot: true),
            Offline("lab-pi", "lab-pi.local", withPriorSnapshot: false),
            Healthy("web-02", "web-02.local", cpu: 33, mem: 47, disk: 58)),

        "empty" => Scenario(name),

        "loading" => new QaOverviewScenario(name, [], [], NeverLoads: true),

        // Configured servers without metrics yet ("sem dados"): no snapshot, nothing collected — never 0%.
        "unavailable" => Scenario(name,
            Unknown("new-01", "new-01.local", lastError: null),
            Unknown("new-02", "new-02.local", lastError: MetricsCollectionErrorCode.InvalidConfiguration),
            Unknown("new-mac", "new-mac.local", lastError: null, os: ServerOperatingSystem.MacOS)),

        "discovery" => new QaOverviewScenario(name, [], QaDiscoveryCatalog.Seed(), NeverLoads: false),

        "many-100" => Scenario(name, [.. Many(100)]),

        "many-500" => Scenario(name, [.. Many(500)]),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name,
            $"Unknown overview scenario. Known: {string.Join(", ", QaOverviewScenarioPolicy.Scenarios)}.")
    };

    private static QaOverviewScenario Scenario(string name, params QaOverviewServer[] servers)
    {
        // CreatedAt is the dashboard's list order: assign it from the declaration order, deterministically.
        var ordered = servers
            .Select((server, index) => server with { Server = server.Server with { CreatedAt = Now.AddDays(-30).AddMinutes(index) } })
            .ToList();
        return new QaOverviewScenario(name, ordered, [], NeverLoads: false);
    }

    /// <summary>Deterministic large fleet: mostly healthy, with every other state spread through it.</summary>
    private static IEnumerable<QaOverviewServer> Many(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var name = $"node-{i:000}";
            var host = $"node-{i:000}.local";
            var load = (i * 37) % 60;
            if (i % 41 == 0)
            {
                yield return Make(name, host, cpu: 20 + load / 3, mem: 50, disk: 90 + (i % 7), ServerHealth.Critical);
            }
            else if (i % 29 == 0)
            {
                yield return Offline(name, host, withPriorSnapshot: i % 2 == 0);
            }
            else if (i % 17 == 0)
            {
                yield return Make(name, host, cpu: 80 + (i % 10), mem: 45, disk: 55, ServerHealth.Warning);
            }
            else if (i % 13 == 0)
            {
                yield return Unknown(name, host, lastError: null);
            }
            else
            {
                yield return Healthy(name, host, cpu: 5 + load, mem: 20 + load, disk: 15 + load,
                    os: i % 5 == 0 ? ServerOperatingSystem.MacOS : ServerOperatingSystem.Linux);
            }
        }
    }

    private static QaOverviewServer Healthy(
        string name, string host, double cpu, double mem, double disk, int port = 22,
        ServerOperatingSystem os = ServerOperatingSystem.Linux) =>
        Make(name, host, cpu, mem, disk, ServerHealth.Healthy, port, os);

    private static QaOverviewServer Make(
        string name, string host, double? cpu, double? mem, double? disk, ServerHealth health, int port = 22,
        ServerOperatingSystem os = ServerOperatingSystem.Linux)
    {
        var server = NewServer(name, host, port, os);
        return new QaOverviewServer(
            server,
            new ServerMetricsSnapshot
            {
                ServerId = server.Id,
                CollectedAt = LastSuccess,
                CpuUsagePercent = cpu,
                MemoryUsagePercent = mem,
                DiskUsagePercent = disk
            },
            new ServerMonitoringState
            {
                ServerId = server.Id,
                Health = health,
                LastAttemptAt = LastSuccess,
                LastSuccessAt = LastSuccess
            });
    }

    private static QaOverviewServer Offline(string name, string host, bool withPriorSnapshot)
    {
        var server = NewServer(name, host, 22, ServerOperatingSystem.Linux);
        var before = Now.AddMinutes(-42);
        return new QaOverviewServer(
            server,
            withPriorSnapshot
                ? new ServerMetricsSnapshot { ServerId = server.Id, CollectedAt = before, CpuUsagePercent = 12, MemoryUsagePercent = 33, DiskUsagePercent = 61 }
                : null,
            new ServerMonitoringState
            {
                ServerId = server.Id,
                Health = ServerHealth.Offline,
                LastAttemptAt = LastSuccess,
                LastSuccessAt = withPriorSnapshot ? before : null,
                ConsecutiveFailures = 4,
                LastError = MetricsCollectionErrorCode.ConnectionFailed,
                IsStale = withPriorSnapshot
            });
    }

    private static QaOverviewServer Unknown(
        string name, string host, MetricsCollectionErrorCode? lastError, ServerOperatingSystem os = ServerOperatingSystem.Linux)
    {
        var server = NewServer(name, host, 22, os);
        return new QaOverviewServer(
            server,
            null,
            new ServerMonitoringState
            {
                ServerId = server.Id,
                Health = ServerHealth.Unknown,
                LastAttemptAt = lastError is null ? null : LastSuccess,
                LastError = lastError
            });
    }

    private static QaOverviewServer Hidden(string name, string host) =>
        Make(name, host, cpu: 10, mem: 20, disk: 30, ServerHealth.Healthy) is var hidden
            ? hidden with { Server = hidden.Server with { IsHidden = true } }
            : throw new InvalidOperationException();

    // Ids are derived from the name, so every Build of a scenario yields the same ids (structurally deterministic).
    private static Server NewServer(string name, string host, int port, ServerOperatingSystem os) => new()
    {
        Id = StableId(name),
        Name = name,
        Host = host,
        Port = port,
        Username = "qa",
        OperatingSystem = os,
        RefreshIntervalSeconds = 30
    };

    private static Guid StableId(string name)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("qa-overview:" + name))[..16];
        return new Guid(bytes);
    }
}
