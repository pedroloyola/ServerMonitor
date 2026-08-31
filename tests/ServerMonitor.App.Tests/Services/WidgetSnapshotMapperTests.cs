using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Tests.Services;

public sealed class WidgetSnapshotMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Success = new(2026, 8, 30, 11, 59, 30, TimeSpan.Zero);

    private static Server NewServer(string name = "Home Server", bool hidden = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = "10.0.0.20",
        Port = 2222,
        Username = "root",
        PrivateKeyPath = @"C:\keys\id_ed25519",
        CredentialReferenceId = Guid.NewGuid(),
        IsHidden = hidden
    };

    private static ServerMonitoringState State(ServerHealth health, DateTimeOffset? lastSuccess = null) => new()
    {
        ServerId = Guid.NewGuid(),
        Health = health,
        LastSuccessAt = lastSuccess
    };

    private static ServerMetricsSnapshot Metrics(double? cpu, double? mem, double? disk) => new()
    {
        ServerId = Guid.NewGuid(),
        CollectedAt = Success,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk
    };

    private static WidgetStateSnapshot Map(
        IReadOnlyList<Server> servers,
        Func<Guid, ServerMonitoringState> stateOf,
        Func<Guid, ServerMetricsSnapshot?> metricsOf) =>
        WidgetSnapshotMapper.Map(servers, stateOf, metricsOf, Now);

    [Fact]
    public void Absolute_memory_disk_gb_and_uptime_are_mapped_but_host_os_never_leak()
    {
        const long gib = 1073741824;
        var server = NewServer();
        var metrics = new ServerMetricsSnapshot
        {
            ServerId = server.Id,
            CollectedAt = Success,
            CpuUsagePercent = 12,
            MemoryUsedBytes = 3 * gib,
            MemoryTotalBytes = 8 * gib,
            MemoryUsagePercent = 37.5,
            DiskUsedBytes = 50 * gib,
            DiskTotalBytes = 200 * gib,
            DiskUsagePercent = 25,
            Uptime = TimeSpan.FromDays(43) + TimeSpan.FromHours(18),
            Hostname = "prod-db-01.internal",           // must NOT reach the widget
            OperatingSystemName = "Debian GNU/Linux 13"  // must NOT reach the widget
        };

        var snapshot = Map([server], _ => State(ServerHealth.Healthy, Success), _ => metrics);
        var s = Assert.Single(snapshot.Servers);

        Assert.Equal(3d, s.MemoryUsedGb);
        Assert.Equal(8d, s.MemoryTotalGb);
        Assert.Equal(50d, s.DiskUsedGb);
        Assert.Equal(200d, s.DiskTotalGb);
        Assert.Equal((long)metrics.Uptime.Value.TotalSeconds, s.UptimeSeconds);

        // Privacy holds by construction (WidgetServerState has no such field): the serialized snapshot
        // carries neither the hostname nor the OS string.
        var json = WidgetStateSerializer.Serialize(snapshot);
        Assert.DoesNotContain("prod-db-01", json);
        Assert.DoesNotContain("Debian", json);
    }

    [Fact]
    public void Unknown_metrics_leave_gb_and_uptime_null_not_zero()
    {
        var server = NewServer();
        var snapshot = Map([server], _ => State(ServerHealth.Unknown, null), _ => null);
        var s = Assert.Single(snapshot.Servers);
        Assert.Null(s.MemoryUsedGb);
        Assert.Null(s.DiskTotalGb);
        Assert.Null(s.UptimeSeconds);
    }

    [Theory]
    [InlineData(ServerHealth.Unknown, WidgetHealth.Unknown)]
    [InlineData(ServerHealth.Healthy, WidgetHealth.Healthy)]
    [InlineData(ServerHealth.Warning, WidgetHealth.Warning)]
    [InlineData(ServerHealth.Critical, WidgetHealth.Critical)]
    [InlineData(ServerHealth.Offline, WidgetHealth.Offline)]
    public void Health_maps_one_to_one(ServerHealth domain, WidgetHealth wire)
    {
        Assert.Equal(wire, WidgetSnapshotMapper.MapHealth(domain));
    }

    [Fact]
    public void Basic_fields_are_mapped()
    {
        var server = NewServer("Prod Web");
        var snapshot = Map(
            new[] { server },
            _ => State(ServerHealth.Warning, Success),
            _ => Metrics(12.5, 40, 55));

        var mapped = Assert.Single(snapshot.Servers);
        Assert.Equal(server.Id, mapped.Id);
        Assert.Equal("Prod Web", mapped.DisplayName);
        Assert.Equal(WidgetHealth.Warning, mapped.Health);
        Assert.Equal(12.5, mapped.CpuUsagePercent);
        Assert.Equal(40, mapped.MemoryUsagePercent);
        Assert.Equal(55, mapped.DiskUsagePercent);
        Assert.Equal(Success, mapped.LastUpdatedUtc);
        Assert.Equal(WidgetSchema.CurrentVersion, snapshot.SchemaVersion);
        Assert.Equal(Now, snapshot.GeneratedAtUtc);
    }

    [Fact]
    public void Null_metrics_stay_null_not_zero()
    {
        var snapshot = Map(
            new[] { NewServer() },
            _ => State(ServerHealth.Offline, Success),
            _ => null);

        var mapped = Assert.Single(snapshot.Servers);
        Assert.Null(mapped.CpuUsagePercent);
        Assert.Null(mapped.MemoryUsagePercent);
        Assert.Null(mapped.DiskUsagePercent);
    }

    [Fact]
    public void Metrics_are_clamped_and_non_finite_becomes_null()
    {
        var snapshot = Map(
            new[] { NewServer() },
            _ => State(ServerHealth.Healthy, Success),
            _ => Metrics(120, -5, double.NaN));

        var mapped = Assert.Single(snapshot.Servers);
        Assert.Equal(100, mapped.CpuUsagePercent);
        Assert.Equal(0, mapped.MemoryUsagePercent);
        Assert.Null(mapped.DiskUsagePercent);
    }

    [Fact]
    public void Hidden_servers_are_excluded()
    {
        var visible = NewServer("Visible");
        var hidden = NewServer("Hidden", hidden: true);

        var snapshot = Map(
            new[] { visible, hidden },
            _ => State(ServerHealth.Healthy, Success),
            _ => Metrics(1, 1, 1));

        var mapped = Assert.Single(snapshot.Servers);
        Assert.Equal("Visible", mapped.DisplayName);
    }

    [Fact]
    public void Server_count_is_capped_at_max()
    {
        var servers = Enumerable.Range(0, WidgetSchema.MaxServers + 10)
            .Select(i => NewServer($"srv{i}"))
            .ToArray();

        var snapshot = Map(servers, _ => State(ServerHealth.Healthy, Success), _ => Metrics(1, 1, 1));

        Assert.Equal(WidgetSchema.MaxServers, snapshot.Servers.Count);
    }

    [Fact]
    public void Overall_health_is_worst_of_fleet()
    {
        var healthy = NewServer("h");
        var warning = NewServer("w");
        var offline = NewServer("o");

        ServerMonitoringState StateFor(Guid id) =>
            id == healthy.Id ? State(ServerHealth.Healthy, Success)
            : id == warning.Id ? State(ServerHealth.Warning, Success)
            : State(ServerHealth.Offline, Success);

        var snapshot = Map(new[] { healthy, warning, offline }, StateFor, _ => Metrics(1, 1, 1));

        Assert.Equal(WidgetHealth.Offline, snapshot.OverallHealth);
    }

    [Fact]
    public void Empty_fleet_is_valid_and_unknown()
    {
        var snapshot = Map(Array.Empty<Server>(), _ => State(ServerHealth.Healthy), _ => null);

        Assert.Empty(snapshot.Servers);
        Assert.Equal(WidgetHealth.Unknown, snapshot.OverallHealth);
    }

    [Fact]
    public void Display_name_is_sanitized()
    {
        var server = NewServer("bad" + (char)0x07 + "  name");
        var snapshot = Map(new[] { server }, _ => State(ServerHealth.Healthy, Success), _ => Metrics(1, 1, 1));

        var mapped = Assert.Single(snapshot.Servers);
        Assert.Equal("bad name", mapped.DisplayName);
        Assert.True(WidgetDisplayName.IsSanitized(mapped.DisplayName));
    }

    [Fact]
    public void Mapped_snapshot_leaks_no_infrastructure_identifiers()
    {
        // The source Server carries host/user/key/credential; none may reach the serialized wire.
        var server = NewServer("Prod");
        var snapshot = Map(new[] { server }, _ => State(ServerHealth.Healthy, Success), _ => Metrics(1, 1, 1));

        var json = WidgetStateSerializer.Serialize(snapshot);
        Assert.DoesNotContain("10.0.0.20", json);
        Assert.DoesNotContain("root", json);
        Assert.DoesNotContain("id_ed25519", json);
        Assert.DoesNotContain("2222", json);
        Assert.DoesNotContain(server.CredentialReferenceId!.Value.ToString(), json);
    }

    [Fact]
    public void Mapped_snapshot_passes_the_read_validator()
    {
        var snapshot = Map(
            new[] { NewServer(), NewServer("second") },
            _ => State(ServerHealth.Warning, Success),
            _ => Metrics(50, 60, 70));

        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
    }
}
