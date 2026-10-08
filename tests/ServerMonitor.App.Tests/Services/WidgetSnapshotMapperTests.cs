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
        Func<Guid, ServerMetricsSnapshot?> metricsOf,
        MonitoringThresholds? thresholds = null) =>
        WidgetSnapshotMapper.Map(servers, stateOf, metricsOf, thresholds ?? MonitoringThresholds.Default, Now);

    // ---- UI.9 D-UI9-2: attentionMetric from the existing priority rule ------------------------------

    [Theory]
    [InlineData(ServerHealth.Warning, 10d, 20d, 85d, WidgetAttentionMetrics.Disk)]
    [InlineData(ServerHealth.Warning, 82d, 20d, 30d, WidgetAttentionMetrics.Cpu)]
    [InlineData(ServerHealth.Critical, 10d, 97d, 85d, WidgetAttentionMetrics.Memory)] // critical beats warning
    [InlineData(ServerHealth.Critical, 96d, 20d, 92d, WidgetAttentionMetrics.Cpu)]     // tie on severity → higher %
    public void Attention_metric_is_the_priority_rule_metric_for_this_server(
        ServerHealth health, double cpu, double mem, double disk, string expected)
    {
        var snapshot = Map([NewServer()], _ => State(health, Success), _ => Metrics(cpu, mem, disk));
        Assert.Equal(expected, Assert.Single(snapshot.Servers).AttentionMetric);
    }

    [Theory]
    [InlineData(ServerHealth.Healthy)]
    [InlineData(ServerHealth.Offline)]
    [InlineData(ServerHealth.Unknown)]
    public void Attention_metric_is_null_unless_the_engine_says_warning_or_critical(ServerHealth health)
    {
        // High values alone never invent a reason: the engine's health decides (§20).
        var snapshot = Map([NewServer()], _ => State(health, Success), _ => Metrics(99, 99, 99));
        Assert.Null(Assert.Single(snapshot.Servers).AttentionMetric);
    }

    [Fact]
    public void Attention_metric_is_null_without_a_metrics_snapshot()
    {
        var snapshot = Map([NewServer()], _ => State(ServerHealth.Critical, Success), _ => null);
        Assert.Null(Assert.Single(snapshot.Servers).AttentionMetric);
    }

    [Fact]
    public void Attention_metric_uses_the_injected_thresholds_not_a_default()
    {
        // 60% disk is below the default warning (80) but above this injected one: only the injected
        // thresholds can produce "disk", so a mapper that silently fell back to the default would fail.
        var custom = MonitoringThresholds.Default with { DiskWarning = 50, DiskCritical = 70 };
        var snapshot = Map([NewServer()], _ => State(ServerHealth.Warning, Success), _ => Metrics(10, 10, 60), custom);
        Assert.Equal(WidgetAttentionMetrics.Disk, Assert.Single(snapshot.Servers).AttentionMetric);

        var withDefault = Map([NewServer()], _ => State(ServerHealth.Warning, Success), _ => Metrics(10, 10, 60));
        Assert.Null(Assert.Single(withDefault.Servers).AttentionMetric);
    }

    [Fact]
    public void Attention_metric_never_copies_the_server_name_or_severity_text()
    {
        var snapshot = Map([NewServer("Secret Name")], _ => State(ServerHealth.Warning, Success), _ => Metrics(10, 10, 85));
        var mapped = Assert.Single(snapshot.Servers);
        Assert.Equal(WidgetAttentionMetrics.Disk, mapped.AttentionMetric);
        Assert.NotNull(WidgetAttentionMetrics.TryParse(mapped.AttentionMetric)); // always an allowlisted value
    }

    // ---- UI.9 D-UI9-3: staleAfterSeconds from the existing stale policy -----------------------------

    [Theory]
    [InlineData(10, 20)]   // floor
    [InlineData(30, 60)]
    [InlineData(60, 120)]
    [InlineData(300, 600)]
    [InlineData(0, 60)]    // unset → default 30 s
    [InlineData(7, 20)]    // below the minimum → 10 s → floor
    [InlineData(100, 120)] // off-catalog → snapped to 60 s
    public void Stale_after_seconds_is_the_stale_policy_over_the_normalized_interval(int interval, int expected)
    {
        var server = NewServer() with { RefreshIntervalSeconds = interval };
        var snapshot = Map([server], _ => State(ServerHealth.Healthy, Success), _ => null);
        Assert.Equal(expected, Assert.Single(snapshot.Servers).StaleAfterSeconds);
    }

    [Fact]
    public void Stale_policy_over_every_supported_interval_stays_inside_the_contract_bounds()
    {
        // V-RC-4: the contract's [20, 600] is the policy's own domain. Adding a slower interval (e.g. 3600)
        // to RefreshIntervalPolicy without revisiting the contract must turn this red. Also sweeps raw
        // values, since Normalize decides what reaches the policy.
        var raw = RefreshIntervalPolicy.SupportedSeconds.Concat([int.MinValue, -1, 0, 1, 9, 11, 45, 299, 301, 3600, int.MaxValue]);
        foreach (var seconds in raw)
        {
            var value = WidgetSnapshotMapper.StaleAfterSeconds(seconds);
            Assert.InRange(value, WidgetSchema.MinStaleAfterSeconds, WidgetSchema.MaxStaleAfterSeconds);
        }

        Assert.Equal(WidgetSchema.MinStaleAfterSeconds,
            RefreshIntervalPolicy.SupportedSeconds.Min(WidgetSnapshotMapper.StaleAfterSeconds));
        Assert.Equal(WidgetSchema.MaxStaleAfterSeconds,
            RefreshIntervalPolicy.SupportedSeconds.Max(WidgetSnapshotMapper.StaleAfterSeconds));
    }

    [Fact]
    public void Mapped_snapshot_with_both_new_fields_passes_the_contract_validator()
    {
        var snapshot = Map(
            [NewServer() with { RefreshIntervalSeconds = 300 }],
            _ => State(ServerHealth.Critical, Success),
            _ => Metrics(10, 10, 95));
        Assert.True(WidgetStateValidator.Validate(snapshot, Now).IsValid);
    }

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
