using System.Globalization;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

public sealed class WidgetViewModelBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings En = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));

    private static WidgetServerState Server(
        string name = "Home",
        WidgetHealth health = WidgetHealth.Healthy,
        double? cpu = 10,
        double? mem = 20,
        double? disk = 30,
        DateTimeOffset? updated = null) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        Health = health,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
        LastUpdatedUtc = updated
    };

    private static WidgetReadResult Read(DateTimeOffset generatedAt, params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = generatedAt,
            OverallHealth = WidgetHealthPrecedence.Worst(servers.Select(s => s.Health)),
            Servers = servers
        });

    private static WidgetViewModel Build(WidgetReadResult read, WidgetSizeHint size = WidgetSizeHint.Medium) =>
        WidgetViewModelBuilder.Build(read, size, Now, En);

    [Fact]
    public void Unavailable_read_maps_to_unavailable_state()
    {
        var vm = Build(WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing));
        Assert.Equal(WidgetDisplayState.Unavailable, vm.DisplayState);
        Assert.Equal(En.NoDataTitle, vm.NoDataTitle);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Zero_servers_is_empty_not_unavailable()
    {
        var vm = Build(Read(Now));
        Assert.Equal(WidgetDisplayState.Empty, vm.DisplayState);
        Assert.Equal(En.NoServers, vm.NoServersText);
    }

    [Fact]
    public void All_healthy_summary()
    {
        var vm = Build(Read(Now, Server(health: WidgetHealth.Healthy), Server(health: WidgetHealth.Healthy)));
        Assert.Equal(WidgetDisplayState.Available, vm.DisplayState);
        Assert.Equal(En.AllHealthy, vm.PrimarySummary);
        Assert.Equal(WidgetHealth.Healthy, vm.OverallHealth);
        Assert.Equal("good", vm.OverallHealthColor);
    }

    [Fact]
    public void Healthy_plus_unknown_keeps_unknown_visible()
    {
        var vm = Build(Read(Now, Server(health: WidgetHealth.Healthy), Server(health: WidgetHealth.Unknown)));
        Assert.NotEqual(En.AllHealthy, vm.PrimarySummary);      // not "all healthy"
        Assert.Contains("1 unknown", vm.CountsSummary);          // unknown never hidden (§21)
        Assert.Equal(WidgetHealth.Unknown, vm.OverallHealth);   // overall precedence
    }

    [Fact]
    public void Need_attention_groups_warning_critical_offline()
    {
        var vm = Build(Read(Now,
            Server(health: WidgetHealth.Warning),
            Server(health: WidgetHealth.Critical),
            Server(health: WidgetHealth.Offline),
            Server(health: WidgetHealth.Healthy)));
        Assert.Contains("3 need attention", vm.CountsSummary);
        Assert.Contains("1 healthy", vm.CountsSummary);
    }

    [Fact]
    public void Null_metric_is_placeholder_not_zero()
    {
        var vm = Build(Read(Now, Server(cpu: null, mem: null, disk: null, health: WidgetHealth.Offline)));
        var row = Assert.Single(vm.Rows);
        Assert.Equal(En.MetricUnknown, row.CpuText);
        Assert.Equal(En.MetricUnknown, row.MemoryText);
        Assert.Equal(En.MetricUnknown, row.DiskText);
        Assert.NotEqual("0%", row.CpuText);
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(100.0, "100%")]
    [InlineData(42.4, "42%")]
    [InlineData(42.6, "43%")]
    [InlineData(150.0, "100%")] // clamped
    public void Metric_is_rounded_integer_percent(double value, string expected)
    {
        var vm = Build(Read(Now, Server(cpu: value)));
        Assert.Equal(expected, Assert.Single(vm.Rows).CpuText);
    }

    [Fact]
    public void Empty_name_falls_back_to_neutral_label_not_ip()
    {
        var vm = Build(Read(Now, Server(name: string.Empty)));
        Assert.Equal(En.NeutralServerName, Assert.Single(vm.Rows).DisplayName);
    }

    [Fact]
    public void Long_name_is_truncated()
    {
        var vm = Build(Read(Now, Server(name: new string('x', 80))));
        var row = Assert.Single(vm.Rows);
        Assert.True(row.DisplayName.Length <= 22);
        Assert.EndsWith("…", row.DisplayName);
    }

    [Fact]
    public void Unicode_name_survives()
    {
        var name = "Café 日本語 " + char.ConvertFromUtf32(0x1F600);
        var vm = Build(Read(Now, Server(name: name)));
        Assert.Equal(name, Assert.Single(vm.Rows).DisplayName);
    }

    [Theory]
    [InlineData(WidgetSizeHint.Small, 0)]
    [InlineData(WidgetSizeHint.Medium, 3)]
    [InlineData(WidgetSizeHint.Large, 6)]
    public void Rows_are_capped_per_size_with_overflow(WidgetSizeHint size, int maxRows)
    {
        var servers = Enumerable.Range(0, 100).Select(i => Server($"s{i}", WidgetHealth.Healthy)).ToArray();
        var vm = Build(Read(Now, servers), size);

        Assert.Equal(maxRows, vm.Rows.Count);
        Assert.Equal(100 - maxRows, vm.OverflowCount);
        if (maxRows < 100)
        {
            Assert.Contains((100 - maxRows).ToString(CultureInfo.InvariantCulture), vm.OverflowText);
        }
    }

    [Fact]
    public void Rows_are_ordered_problems_first()
    {
        var vm = Build(Read(Now,
            Server("h", WidgetHealth.Healthy),
            Server("o", WidgetHealth.Offline),
            Server("w", WidgetHealth.Warning)), WidgetSizeHint.Large);
        Assert.Equal(WidgetHealth.Offline, vm.Rows[0].Health);
        Assert.Equal(WidgetHealth.Warning, vm.Rows[1].Health);
        Assert.Equal(WidgetHealth.Healthy, vm.Rows[2].Health);
    }

    [Fact]
    public void Fresh_snapshot_says_just_now()
    {
        var vm = Build(Read(Now.AddSeconds(-10), Server()));
        Assert.Equal(WidgetFreshnessState.Fresh, vm.Freshness);
        Assert.Equal(En.UpdatedJustNow, vm.FreshnessText);
    }

    [Fact]
    public void Stale_snapshot_shows_relative_time_not_a_worse_health()
    {
        var vm = Build(Read(Now.AddMinutes(-4), Server(health: WidgetHealth.Healthy)));
        Assert.Equal(WidgetFreshnessState.Stale, vm.Freshness);
        Assert.Equal("Updated 4 min ago", vm.FreshnessText);
        Assert.Equal(WidgetHealth.Healthy, vm.OverallHealth); // stale never escalates health (§12)
    }

    [Fact]
    public void Hour_scale_freshness()
    {
        var vm = Build(Read(Now.AddHours(-2), Server()));
        Assert.Equal("Updated 2 hr ago", vm.FreshnessText);
    }
}
