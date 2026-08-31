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

    // Small is deliberately absent: it renders no rows and therefore carries no overflow at all - see
    // Small_states_the_whole_fleet_and_carries_no_overflow for its own contract (Prism L3).
    [Theory]
    [InlineData(WidgetSizeHint.Medium, 2)]
    [InlineData(WidgetSizeHint.Large, 3)]
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

    // M13-QA-4 / P-017. Medium holds two server blocks; anything beyond that MUST be announced by the
    // overflow affordance. The board clips whatever exceeds the fixed card height, so a cap that is too
    // generous makes servers vanish silently - the exact opposite of the product's honest degradation.
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(100, 2, 98)]
    public void Medium_shows_two_rows_and_accounts_for_every_other_server(
        int total, int expectedRows, int expectedOverflow)
    {
        var servers = Enumerable.Range(0, total).Select(i => Server($"s{i:D3}", WidgetHealth.Healthy)).ToArray();
        var vm = Build(Read(Now, servers), WidgetSizeHint.Medium);

        Assert.Equal(expectedRows, vm.Rows.Count);
        Assert.Equal(expectedOverflow, vm.OverflowCount);
        Assert.Equal(total, vm.TotalServers);

        // The truthful-UI invariant: nothing is ever dropped without being counted.
        Assert.Equal(total, vm.Rows.Count + vm.OverflowCount);

        if (expectedOverflow == 0)
        {
            Assert.Empty(vm.OverflowText);
        }
        else
        {
            Assert.Contains(expectedOverflow.ToString(CultureInfo.InvariantCulture), vm.OverflowText);
        }
    }

    // M13-QA-5 / P-017. Large holds three blocks plus the fleet-summary footer - not six. The old cap made
    // overflow zero for 4-6 servers, so nothing was announced and the extra servers AND the footer were
    // clipped away in silence.
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 3, 1)]
    [InlineData(6, 3, 3)]
    [InlineData(7, 3, 4)]
    [InlineData(100, 3, 97)]
    public void Large_shows_three_rows_and_accounts_for_every_other_server(
        int total, int expectedRows, int expectedOverflow)
    {
        var servers = Enumerable.Range(0, total).Select(i => Server($"s{i:D3}", WidgetHealth.Healthy)).ToArray();
        var vm = Build(Read(Now, servers), WidgetSizeHint.Large);

        Assert.Equal(expectedRows, vm.Rows.Count);
        Assert.Equal(expectedOverflow, vm.OverflowCount);
        Assert.Equal(total, vm.TotalServers);
        Assert.Equal(total, vm.Rows.Count + vm.OverflowCount);

        if (expectedOverflow == 0)
        {
            Assert.Empty(vm.OverflowText);
        }
        else
        {
            Assert.Contains(expectedOverflow.ToString(CultureInfo.InvariantCulture), vm.OverflowText);
        }
    }

    [Fact]
    public void Large_with_no_servers_is_empty_not_overflowing()
    {
        var vm = Build(Read(Now), WidgetSizeHint.Large);

        Assert.Equal(WidgetDisplayState.Empty, vm.DisplayState);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.OverflowCount);
        Assert.Empty(vm.OverflowText);
    }

    [Fact]
    public void Large_keeps_severity_ordering_when_capped()
    {
        var vm = Build(Read(Now,
            Server("healthy-a", WidgetHealth.Healthy),
            Server("healthy-b", WidgetHealth.Healthy),
            Server("warning", WidgetHealth.Warning),
            Server("critical", WidgetHealth.Critical),
            Server("offline", WidgetHealth.Offline)), WidgetSizeHint.Large);

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(2, vm.OverflowCount);
        Assert.Equal(new[] { "offline", "critical", "warning" }, vm.Rows.Select(r => r.DisplayName).ToArray());
    }

    // ---- The shared invariant. This is the guard that stops QA-4/QA-5 recurring per size. ----
    //
    // MEASURED capacities, deliberately written out as literals rather than read from MaxRowsFor. Asserting
    // against the production constant would be circular: raising Medium back to 3 or Large back to 6 would
    // move the expectation with the code and the test would still pass, which is exactly how both defects
    // shipped with a green suite in the first place (Atlas M1). These numbers came from the real Windows
    // Widgets board and may only be changed by re-measuring there.
    public const int MeasuredMediumCapacity = 2;
    public const int MeasuredLargeCapacity = 3;

    public static TheoryData<WidgetSizeHint, int> RowRenderingSizes() => new()
    {
        { WidgetSizeHint.Medium, MeasuredMediumCapacity },
        { WidgetSizeHint.Large, MeasuredLargeCapacity },
    };

    // For every size that renders server rows, a fleet larger than what is shown MUST be announced. The two
    // shipped defects were independent instances of this one rule being broken, so it is asserted once,
    // across both sizes and a wide range of fleet sizes, rather than per size.
    [Theory]
    [MemberData(nameof(RowRenderingSizes))]
    public void Sizes_that_render_rows_never_hide_a_server_silently(WidgetSizeHint size, int measuredCapacity)
    {
        // The production constant must equal what was measured on the host - not the other way round.
        Assert.Equal(measuredCapacity, WidgetViewModelBuilder.MaxRowsFor(size));

        foreach (var total in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 12, 100 })
        {
            var servers = Enumerable.Range(0, total)
                .Select(i => Server($"s{i:D3}", WidgetHealth.Healthy)).ToArray();
            var vm = Build(Read(Now, servers), size);

            // Nothing is ever unaccounted for.
            Assert.Equal(total, vm.Rows.Count + vm.OverflowCount);
            // Never render more than the capacity measured on the real board.
            Assert.True(vm.Rows.Count <= measuredCapacity,
                $"{size} rendered {vm.Rows.Count} rows, above its measured host capacity of {measuredCapacity}");
            Assert.Equal(Math.Min(total, measuredCapacity), vm.Rows.Count);
            // And the headline invariant: hidden servers are always announced.
            if (total > vm.Rows.Count)
            {
                Assert.True(vm.OverflowText.Length > 0,
                    $"{size} with {total} servers showed {vm.Rows.Count} rows and announced nothing");
            }
            else
            {
                Assert.Empty(vm.OverflowText);
            }
        }
    }

    // Small is exempt from the overflow rule, and this pins WHY: it renders no rows, so it never implies a
    // list, and its hero states the whole fleet. It must not carry a phantom overflow it never draws.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    public void Small_states_the_whole_fleet_and_carries_no_overflow(int total)
    {
        var servers = Enumerable.Range(0, total).Select(i => Server($"s{i:D3}", WidgetHealth.Healthy)).ToArray();
        var vm = Build(Read(Now, servers), WidgetSizeHint.Small);

        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.OverflowCount);
        Assert.Empty(vm.OverflowText);
        // The honesty comes from the hero: it names the full fleet size.
        Assert.Equal(total, vm.TotalServers);
        Assert.Equal($"{total}/{total}", vm.HeroValue);
    }

    [Fact]
    public void Medium_with_no_servers_is_empty_not_overflowing()
    {
        var vm = Build(Read(Now), WidgetSizeHint.Medium);

        Assert.Equal(WidgetDisplayState.Empty, vm.DisplayState);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.OverflowCount);
        Assert.Empty(vm.OverflowText);
    }

    [Fact]
    public void Medium_overflow_is_localized()
    {
        var servers = Enumerable.Range(0, 5).Select(i => Server($"s{i}", WidgetHealth.Healthy)).ToArray();
        var read = Read(Now, servers);

        foreach (var culture in new[] { "en-US", "pt-BR", "pt-PT" })
        {
            var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
            var vm = WidgetViewModelBuilder.Build(read, WidgetSizeHint.Medium, Now, strings);

            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal(3, vm.OverflowCount);
            // Localized, and never a bare number: the user must be able to read it as "more servers".
            Assert.Contains("3", vm.OverflowText);
            Assert.NotEqual("3", vm.OverflowText);
            Assert.NotEqual("+3", vm.OverflowText);
            Assert.DoesNotContain("{0}", vm.OverflowText, StringComparison.Ordinal);
            // Each locale gets its own idiomatic phrasing, not a shared template with a swapped word.
            // Both Portuguese variants say "mais 3". European Portuguese "3 a mais" would mean "3 too
            // many", not "3 more" - the variants genuinely do not diverge here (Prism L2).
            Assert.Contains(culture switch
            {
                "pt-BR" => "mais 3",
                "pt-PT" => "mais 3",
                _ => "3 more"
            }, vm.OverflowText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Medium_keeps_severity_ordering_when_capped()
    {
        // Two problems plus healthy noise: the problems must be the two that survive the cap.
        var vm = Build(Read(Now,
            Server("healthy-a", WidgetHealth.Healthy),
            Server("healthy-b", WidgetHealth.Healthy),
            Server("critical", WidgetHealth.Critical),
            Server("offline", WidgetHealth.Offline)), WidgetSizeHint.Medium);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(2, vm.OverflowCount);
        Assert.Equal(new[] { "offline", "critical" }, vm.Rows.Select(r => r.DisplayName).ToArray());
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
