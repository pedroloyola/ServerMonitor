using System.Globalization;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// The builder's long-standing semantics, migrated to the V3 fields in C1 (SPEC §9: the semantics of the
/// removed PrimarySummary/CountsSummary/OverflowText move to Title/Summary/RowsOfTotalText before the
/// fields go): Unknown is never hidden or folded into healthy, "All healthy" only when every server is
/// Healthy AND fresh, nothing is dropped without being counted, and the row caps are host-measured.
/// </summary>
public sealed class WidgetViewModelBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings En = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));

    private static int _next;

    private static WidgetServerState Server(
        string name = "Home",
        WidgetHealth health = WidgetHealth.Healthy,
        double? cpu = 10,
        double? mem = 20,
        double? disk = 30,
        DateTimeOffset? updated = null) => new()
    {
        Id = new Guid($"00000000-0000-0000-0000-{Interlocked.Increment(ref _next):D12}"),
        DisplayName = name,
        Health = health,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
        LastUpdatedUtc = updated ?? Now
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

    private static string RowsOfTotal(int shown, int total) =>
        string.Format(CultureInfo.InvariantCulture, En.OverflowFormat, shown, total);

    [Fact]
    public void Unavailable_read_maps_to_unavailable_state()
    {
        var vm = Build(WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing));
        Assert.Equal(WidgetCardState.Unavailable, vm.CardState);
        Assert.Equal(En.UnavailableTitle, vm.Title);
        Assert.Equal(string.Empty, vm.CtaText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Zero_servers_is_empty_not_unavailable()
    {
        var vm = Build(Read(Now));
        Assert.Equal(WidgetCardState.Empty, vm.CardState);
        Assert.Equal(En.EmptyTitle, vm.Title);
        Assert.Equal(En.EmptyCta, vm.CtaText);
    }

    [Fact]
    public void All_healthy_summary()
    {
        var vm = Build(Read(Now, Server(health: WidgetHealth.Healthy), Server(health: WidgetHealth.Healthy)));
        Assert.Equal(WidgetCardState.Healthy, vm.CardState);
        Assert.Equal(En.FleetAllHealthy, vm.Title);
        Assert.Equal("good", vm.StateColor);
    }

    [Fact]
    public void Healthy_plus_unknown_keeps_unknown_visible()
    {
        var vm = Build(Read(Now, Server(health: WidgetHealth.Healthy), Server(health: WidgetHealth.Unknown)));
        Assert.NotEqual(En.FleetAllHealthy, vm.Title);                // not "all healthy"
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, En.FleetUnknownOnly, 1), vm.Summary); // never hidden (§21)
        Assert.Equal(1, vm.HealthyFreshCount);                        // never folded into healthy
        Assert.Equal(WidgetCardState.NoCurrentData, vm.CardState);
    }

    [Fact]
    public void Attention_counts_warning_critical_and_offline_separately_and_keeps_healthy()
    {
        var vm = Build(Read(Now,
            Server(health: WidgetHealth.Warning),
            Server(health: WidgetHealth.Critical),
            Server(health: WidgetHealth.Offline),
            Server(health: WidgetHealth.Healthy)));
        Assert.Equal(WidgetCardState.Attention, vm.CardState);
        Assert.Equal("3 issues", vm.Title);
        // Prism P-C1-2: with problems the summary lists ONLY what needs the user.
        Assert.Equal("1\u00A0attention · 1\u00A0critical · 1\u00A0no\u00A0connection", vm.Summary);
        Assert.Equal("attention", vm.StateColor); // critical/offline outrank warning
    }

    [Fact]
    public void Null_metric_is_placeholder_not_zero()
    {
        var vm = Build(Read(Now, Server(cpu: null, mem: null, disk: null)));
        var row = Assert.Single(vm.Rows);
        foreach (var metric in new[] { row.Cpu, row.Memory, row.Disk })
        {
            Assert.Equal(En.MetricUnknown, metric.ValueText);
            Assert.Null(metric.Percent);
        }
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
        Assert.Equal(expected, Assert.Single(vm.Rows).Cpu.ValueText);
    }

    [Fact]
    public void Empty_name_falls_back_to_neutral_label_not_ip()
    {
        var vm = Build(Read(Now, Server(name: string.Empty)));
        Assert.Equal(En.NeutralServerName, Assert.Single(vm.Rows).DisplayName.Value);
    }

    [Fact]
    public void Long_name_is_truncated()
    {
        var vm = Build(Read(Now, Server(name: new string('x', 80))));
        var name = Assert.Single(vm.Rows).DisplayName.Value;
        Assert.True(name.Length <= 22);
        Assert.EndsWith("…", name);
    }

    [Fact]
    public void Unicode_name_survives()
    {
        var name = "Café 日本語 " + char.ConvertFromUtf32(0x1F600);
        var vm = Build(Read(Now, Server(name: name)));
        Assert.Equal(name, Assert.Single(vm.Rows).DisplayName.Value);
    }

    // M13-QA-4/QA-5 / P-017: a cap that is too generous makes servers vanish silently on the board.
    [Theory]
    [InlineData(WidgetSizeHint.Medium, 1, 1)]
    [InlineData(WidgetSizeHint.Medium, 2, 2)]
    [InlineData(WidgetSizeHint.Medium, 3, 2)]
    [InlineData(WidgetSizeHint.Medium, 100, 2)]
    [InlineData(WidgetSizeHint.Large, 3, 3)]
    [InlineData(WidgetSizeHint.Large, 4, 3)]
    [InlineData(WidgetSizeHint.Large, 100, 3)]
    public void List_sizes_cap_rows_and_always_state_rows_of_total(WidgetSizeHint size, int total, int expectedRows)
    {
        var servers = Enumerable.Range(0, total).Select(i => Server($"s{i:D3}")).ToArray();
        var vm = Build(Read(Now, servers), size);

        Assert.Equal(expectedRows, vm.Rows.Count);
        Assert.Equal(total, vm.Rows.Count + vm.OverflowCount); // nothing is ever dropped without being counted
        Assert.Equal(RowsOfTotal(expectedRows, total), vm.RowsOfTotalText); // also "3 of 3" (SPEC §3)
    }

    // ---- The shared invariant. MEASURED capacities, written as literals rather than read from MaxRowsFor:
    // asserting against the production constant would be circular (Atlas M1). V3 targets 3 on Medium; the
    // number changes only when C0 measures V3 rows on the real board.
    public const int MeasuredMediumCapacity = 2;
    public const int MeasuredLargeCapacity = 3;

    public static TheoryData<WidgetSizeHint, int> RowRenderingSizes() => new()
    {
        { WidgetSizeHint.Medium, MeasuredMediumCapacity },
        { WidgetSizeHint.Large, MeasuredLargeCapacity },
    };

    [Theory]
    [MemberData(nameof(RowRenderingSizes))]
    public void Sizes_that_render_rows_never_hide_a_server_silently(WidgetSizeHint size, int measuredCapacity)
    {
        Assert.Equal(measuredCapacity, WidgetLayout.MaxRowsFor(size));

        foreach (var total in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 12, 100 })
        {
            var servers = Enumerable.Range(0, total).Select(i => Server($"s{i:D3}")).ToArray();
            var vm = Build(Read(Now, servers), size);

            Assert.Equal(total, vm.Rows.Count + vm.OverflowCount);
            Assert.Equal(Math.Min(total, measuredCapacity), vm.Rows.Count);
            if (total == 0)
            {
                Assert.Equal(WidgetCardState.Empty, vm.CardState);
                Assert.Empty(vm.RowsOfTotalText);
            }
            else
            {
                Assert.Equal(RowsOfTotal(vm.Rows.Count, total), vm.RowsOfTotalText);
            }
        }
    }

    // Small renders no rows, so it never implies a list; its fraction states the whole fleet.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    public void Small_states_the_whole_fleet_and_carries_no_overflow(int total)
    {
        var servers = Enumerable.Range(0, total).Select(i => Server($"s{i:D3}")).ToArray();
        var vm = Build(Read(Now, servers), WidgetSizeHint.Small);

        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.OverflowCount);
        Assert.Empty(vm.RowsOfTotalText);
        Assert.Equal(total, vm.TotalServers);
        Assert.Equal($"{total}/{total}", vm.FractionText);
    }

    [Fact]
    public void Rows_of_total_is_localized()
    {
        var read = Read(Now, Enumerable.Range(0, 5).Select(i => Server($"s{i}")).ToArray());
        foreach (var (culture, expected) in new[] { ("en-US", "2 of 5 servers"), ("pt-BR", "2 de 5 servidores"), ("pt-PT", "2 de 5 servidores") })
        {
            var vm = WidgetViewModelBuilder.Build(read, WidgetSizeHint.Medium, Now, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture)));
            Assert.Equal(expected, vm.RowsOfTotalText);
        }
    }

    [Fact]
    public void Capped_rows_keep_severity_ordering()
    {
        var servers = new[]
        {
            Server("healthy-a"), Server("healthy-b"),
            Server("warning", WidgetHealth.Warning), Server("critical", WidgetHealth.Critical), Server("offline", WidgetHealth.Offline)
        };

        Assert.Equal(new[] { "offline", "critical", "warning" },
            Build(Read(Now, servers), WidgetSizeHint.Large).Rows.Select(r => r.DisplayName.Value).ToArray());
        Assert.Equal(new[] { "offline", "critical" },
            Build(Read(Now, servers), WidgetSizeHint.Medium).Rows.Select(r => r.DisplayName.Value).ToArray());
    }

    [Fact]
    public void Rows_are_ordered_problems_first()
    {
        var vm = Build(Read(Now, Server("h"), Server("o", WidgetHealth.Offline), Server("w", WidgetHealth.Warning)), WidgetSizeHint.Large);
        Assert.Equal(new[] { WidgetRowState.Offline, WidgetRowState.Warning, WidgetRowState.Healthy }, vm.Rows.Select(r => r.State));
    }

    [Fact]
    public void Fresh_snapshot_says_just_now()
    {
        var vm = Build(Read(Now.AddSeconds(-10), Server(updated: Now.AddSeconds(-10))));
        Assert.Equal(WidgetFreshnessState.Fresh, vm.Freshness);
        Assert.Equal(En.UpdatedJustNow, vm.FooterText);
    }

    [Fact]
    public void Stale_snapshot_shows_the_last_reading_and_never_a_worse_health()
    {
        var vm = Build(Read(Now.AddMinutes(-4), Server(health: WidgetHealth.Healthy, updated: Now.AddMinutes(-4))));
        Assert.Equal(WidgetFreshnessState.Stale, vm.Freshness);
        Assert.Equal("Last reading 4 min ago", vm.FooterText);
        Assert.Equal(WidgetRowState.NotUpdated, Assert.Single(vm.Rows).State); // not Warning/Critical (§12)
    }

    [Fact]
    public void Hour_scale_freshness()
    {
        var vm = Build(Read(Now.AddHours(-2), Server(updated: Now.AddHours(-2))));
        Assert.Equal("Last reading 2 hr ago", vm.FooterText);
    }
}
