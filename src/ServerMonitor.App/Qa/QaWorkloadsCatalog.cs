using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Qa;

// QA-ONLY. Excluded from Release builds (Qa\**\*.cs is Compile-Removed for non-Debug) and only wired
// when launched with --qa-workloads. It lets the real workload UI (Docker + services, read-only) be
// inspected across every shape — availability failures, empty/healthy/unhealthy/stopped, large lists,
// truncation above the cap, systemd/launchd/unsupported managers, stale carry-over, and hostile names —
// without SSH, a Docker host or any real server. Nothing here ships.

/// <summary>
/// One QA workload scenario: a server plus the exact <see cref="ServerWorkloadSnapshot"/> the UI should
/// render for it, plus a benign metrics snapshot and Healthy monitoring state so the card itself shows
/// normally (workloads never change host health).
/// </summary>
internal sealed record QaWorkloadScenario
{
    public required string Label { get; init; }

    public required Server Server { get; init; }

    public required ServerWorkloadSnapshot Workload { get; init; }

    public required ServerMetricsSnapshot Metrics { get; init; }

    public required ServerMonitoringState State { get; init; }
}

/// <summary>
/// Deterministic workload scenarios, one server each, so a QA inspector selects a shape simply by
/// picking its server. All display text is passed through <see cref="WorkloadTextSanitizer"/> exactly as
/// the real parser would, so the harness proves sanitization end to end (the sanitizer implementation is
/// owned by platform-infra; this only calls it).
/// </summary>
internal static class QaWorkloadsCatalog
{
    public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static IReadOnlyList<QaWorkloadScenario> Scenarios { get; } = Build();

    public static IReadOnlyList<Server> Servers { get; } = Scenarios.Select(s => s.Server).ToList();

    public static QaWorkloadScenario? For(Guid serverId) =>
        Scenarios.FirstOrDefault(s => s.Server.Id == serverId);

    public static ServerWorkloadSnapshot? WorkloadFor(Guid serverId) => For(serverId)?.Workload;

    public static ServerMetricsSnapshot? SnapshotFor(Guid serverId) => For(serverId)?.Metrics;

    // ---- builders -----------------------------------------------------------------------------

    private static ContainerInfo Container(
        string idSeed,
        string name,
        string image,
        ContainerState state,
        ContainerHealth health,
        string status,
        DateTimeOffset? created = null) => new()
    {
        ContainerId = WorkloadTextSanitizer.Sanitize(idSeed),
        Name = WorkloadTextSanitizer.Sanitize(name),
        Image = WorkloadTextSanitizer.Sanitize(image),
        State = state,
        StatusText = WorkloadTextSanitizer.Sanitize(status),
        Health = health,
        CreatedAt = created ?? Now.AddHours(-3)
    };

    private static ServiceInfo Service(
        string id,
        ServiceState state,
        string? display = null,
        string? sub = null,
        ServiceStartupState? startup = null) => new()
    {
        Id = WorkloadTextSanitizer.Sanitize(id),
        Name = WorkloadTextSanitizer.Sanitize(id.Split('.')[0]),
        DisplayName = WorkloadTextSanitizer.SanitizeOptional(display),
        State = state,
        SubState = WorkloadTextSanitizer.SanitizeOptional(sub),
        StartupState = startup
    };

    // launchd services mirror LaunchdPrintSystemParser: the Name is the FULL reverse-DNS label (never a
    // collapsed segment — the leading one is almost always "com" and the trailing one can collide), and
    // launchctl print exposes no description/sub-state/startup, so those stay null (§60/§61).
    private static ServiceInfo LaunchdService(string label, ServiceState state) => new()
    {
        Id = WorkloadTextSanitizer.Sanitize(label),
        Name = WorkloadTextSanitizer.Sanitize(label),
        DisplayName = null,
        State = state,
        SubState = null,
        StartupState = null
    };

    private static DockerSnapshot Docker(
        DockerAvailability availability,
        IReadOnlyList<ContainerInfo>? containers = null,
        bool truncated = false) => new()
    {
        Availability = availability,
        Containers = containers ?? [],
        Truncated = truncated
    };

    private static ServiceSnapshot Services(
        ServiceManager manager,
        WorkloadServiceAvailability availability,
        IReadOnlyList<ServiceInfo>? services = null,
        bool truncated = false) => new()
    {
        Manager = manager,
        Availability = availability,
        Services = services ?? [],
        Truncated = truncated
    };

    private static IReadOnlyList<ContainerInfo> HealthyFew() =>
    [
        Container("a1b2c3d4e5f6", "nginx-proxy", "nginx:1.27", ContainerState.Running, ContainerHealth.Healthy, "Up 3 days"),
        Container("b2c3d4e5f6a1", "api", "ghcr.io/acme/api:2.4.1", ContainerState.Running, ContainerHealth.Healthy, "Up 6 hours"),
        Container("c3d4e5f6a1b2", "redis", "redis:7-alpine", ContainerState.Running, ContainerHealth.None, "Up 3 days")
    ];

    private static IReadOnlyList<ContainerInfo> ManyContainers(int count)
    {
        var list = new List<ContainerInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var state = (i % 7) switch
            {
                0 => ContainerState.Exited,
                1 => ContainerState.Paused,
                6 => ContainerState.Restarting,
                _ => ContainerState.Running
            };
            var health = state == ContainerState.Running
                ? (i % 5 == 0 ? ContainerHealth.Unhealthy : i % 3 == 0 ? ContainerHealth.None : ContainerHealth.Healthy)
                : ContainerHealth.None;
            list.Add(Container(
                i.ToString("x12"),
                $"svc-{i:D4}",
                $"registry.local/app:{1 + i % 9}.{i % 7}",
                state,
                health,
                state == ContainerState.Exited ? $"Exited ({i % 3}) {i % 59} minutes ago" : $"Up {1 + i % 240} minutes"));
        }

        return list;
    }

    private static IReadOnlyList<ServiceInfo> SystemdNormal() =>
    [
        Service("ssh.service", ServiceState.Running, "OpenBSD Secure Shell server", "running", ServiceStartupState.Enabled),
        Service("cron.service", ServiceState.Running, "Regular background program processing daemon", "running", ServiceStartupState.Enabled),
        Service("nginx.service", ServiceState.Running, "A high performance web server", "running", ServiceStartupState.Enabled),
        Service("apt-daily.service", ServiceState.Stopped, "Daily apt download activities", "dead", ServiceStartupState.Static)
    ];

    private static IReadOnlyList<ServiceInfo> ManyServices(int count)
    {
        var list = new List<ServiceInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var state = (i % 11) switch
            {
                0 => ServiceState.Failed,
                1 => ServiceState.Stopped,
                10 => ServiceState.Starting,
                _ => ServiceState.Running
            };
            var startup = (i % 3) switch
            {
                0 => ServiceStartupState.Enabled,
                1 => ServiceStartupState.Disabled,
                _ => ServiceStartupState.Static
            };
            list.Add(Service(
                $"unit-{i:D4}.service",
                state,
                $"Managed unit number {i}",
                state == ServiceState.Running ? "running" : state == ServiceState.Failed ? "failed" : "dead",
                startup));
        }

        return list;
    }

    // Hostile raw strings; each is sanitized on the way into a ContainerInfo/ServiceInfo so the harness
    // demonstrates the sanitizer neutralizing terminal escapes, bidi overrides, control chars and
    // over-long input while preserving legitimate Unicode.
    private static IReadOnlyList<ContainerInfo> MaliciousContainers() =>
    [
        Container("dead beefcafe", "web\n\tserver-01", "nginx[31mRED[0m:latest", ContainerState.Running, ContainerHealth.Healthy, "Up\t3\ndays"),
        Container("f00dbabef00d", "invoice‮txt.exe", "img6n", ContainerState.Running, ContainerHealth.Unhealthy, "Up 1 hour"),
        Container("1234abcd5678", "🚀 payments 🔥", "acme/payments:2.0", ContainerState.Running, ContainerHealth.Healthy, "Up 9 days"),
        Container("aabbccddeeff", new string('Z', 400), "very/long:tag", ContainerState.Exited, ContainerHealth.None, new string('x', 400))
    ];

    private static IReadOnlyList<ServiceInfo> MaliciousServices() =>
    [
        Service("evil[2J[H.service", ServiceState.Running, "clear[2Jscreen", "running", ServiceStartupState.Enabled),
        Service("bidi‮spoof.service", ServiceState.Failed, "café ☕ monitor 🚀", "failed", ServiceStartupState.Disabled),
        Service("newline\n\tinject.service", ServiceState.Running, new string('L', 500), "running", ServiceStartupState.Static)
    ];

    private static IReadOnlyList<QaWorkloadScenario> Build()
    {
        var order = 0;
        var list = new List<QaWorkloadScenario>();

        void Add(
            string label,
            DockerSnapshot docker,
            ServiceSnapshot services,
            ServerOperatingSystem os = ServerOperatingSystem.Linux,
            bool stale = false)
        {
            list!.Add(Make(label, docker, services, ref order, os, stale));
        }

        // Docker-focused (services = normal systemd)
        Add("Docker: not installed", Docker(DockerAvailability.NotInstalled), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: permission denied", Docker(DockerAvailability.PermissionDenied), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: daemon unavailable", Docker(DockerAvailability.Unavailable), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: 0 containers", Docker(DockerAvailability.Available, []), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: healthy", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: unhealthy + exited", Docker(DockerAvailability.Available,
        [
            Container("11aa22bb33cc", "api", "acme/api:2.4.1", ContainerState.Running, ContainerHealth.Unhealthy, "Up 2 hours (unhealthy)"),
            Container("44dd55ee66ff", "worker", "acme/worker:2.4.1", ContainerState.Exited, ContainerHealth.None, "Exited (137) 5 minutes ago"),
            Container("77aa88bb99cc", "db", "postgres:16", ContainerState.Running, ContainerHealth.Healthy, "Up 5 days")
        ]), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: 50 containers", Docker(DockerAvailability.Available, ManyContainers(50)), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: 500 containers", Docker(DockerAvailability.Available, ManyContainers(500)), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("Docker: truncated (>cap)", Docker(DockerAvailability.Available, ManyContainers(WorkloadLimits.MaxContainers), truncated: true), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));

        // Services-focused (docker = healthy few)
        Add("systemd: running", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()));
        Add("systemd: failed unit", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available,
        [
            Service("ssh.service", ServiceState.Running, "OpenBSD Secure Shell server", "running", ServiceStartupState.Enabled),
            Service("myapp.service", ServiceState.Failed, "My application", "failed", ServiceStartupState.Enabled),
            Service("backup.service", ServiceState.Stopped, "Nightly backup", "dead", ServiceStartupState.Disabled)
        ]));
        Add("systemd: masked/inactive", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available,
        [
            Service("bluetooth.service", ServiceState.Stopped, "Bluetooth service", "dead", ServiceStartupState.Masked),
            Service("docker.socket", ServiceState.Running, "Docker Socket for the API", "running", ServiceStartupState.Enabled)
        ]));
        Add("services: unsupported init", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Unsupported, WorkloadServiceAvailability.NotInstalled));
        Add("launchd: running", Docker(DockerAvailability.NotInstalled), Services(ServiceManager.Launchd, WorkloadServiceAvailability.Available,
        [
            // Two distinct services sharing the trailing segment ("sshd") prove the full reverse-DNS label
            // is the identity — they must render as com.apple.sshd / org.openssh.sshd, never both "sshd"
            // (and never both "com" from the leading segment).
            LaunchdService("com.apple.sshd", ServiceState.Running),
            LaunchdService("org.openssh.sshd", ServiceState.Running),
            LaunchdService("com.acme.agent", ServiceState.Stopped)
        ]), os: ServerOperatingSystem.MacOS);
        Add("launchd: permission denied", Docker(DockerAvailability.NotInstalled), Services(ServiceManager.Launchd, WorkloadServiceAvailability.PermissionDenied), os: ServerOperatingSystem.MacOS);
        Add("services: 100 units", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, ManyServices(100)));
        Add("services: 2000 units", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, ManyServices(2000)));
        Add("services: truncated (>cap)", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, ManyServices(WorkloadLimits.MaxServices), truncated: true));

        // Cross-cutting
        Add("Hostile names (sanitized)", Docker(DockerAvailability.Available, MaliciousContainers()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, MaliciousServices()));
        Add("Stale (carried over)", Docker(DockerAvailability.Available, HealthyFew()), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Available, SystemdNormal()), stale: true);
        Add("All unknown (probe failed)", Docker(DockerAvailability.Unknown), Services(ServiceManager.Systemd, WorkloadServiceAvailability.Unknown));

        return list;
    }

    private static QaWorkloadScenario Make(
        string label,
        DockerSnapshot docker,
        ServiceSnapshot services,
        ref int order,
        ServerOperatingSystem os,
        bool stale)
    {
        var id = Guid.NewGuid();
        var server = new Server
        {
            Id = id,
            Name = $"QA · {label}",
            Host = $"qa-workloads-{order}.local",
            Port = 22,
            Username = "qa",
            OperatingSystem = os,
            RefreshIntervalSeconds = 30,
            CreatedAt = Now.AddSeconds(order++)
        };

        return new QaWorkloadScenario
        {
            Label = label,
            Server = server,
            Workload = new ServerWorkloadSnapshot
            {
                ServerId = id,
                CapturedAtUtc = stale ? Now.AddMinutes(-12) : Now,
                LastAttemptAtUtc = Now,
                IsStale = stale,
                Docker = docker,
                Services = services
            },
            Metrics = new ServerMetricsSnapshot
            {
                ServerId = id,
                CollectedAt = Now,
                CpuUsagePercent = 22,
                MemoryUsagePercent = 48,
                DiskUsagePercent = 61,
                Uptime = TimeSpan.FromDays(9),
                Hostname = server.Host
            },
            State = new ServerMonitoringState
            {
                ServerId = id,
                Health = ServerHealth.Healthy,
                LastSuccessAt = Now,
                LastAttemptAt = Now
            }
        };
    }
}
