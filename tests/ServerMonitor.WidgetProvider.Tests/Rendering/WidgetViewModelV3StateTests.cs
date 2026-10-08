using System.Globalization;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 B2 — V3 states at view-model level (SPEC §3, D-UI9-4/5, test 3; V-RC-5/6; Prism §B copy).
/// Every id is fixed (FLAKE-WP-GUID) and every age comes from explicit timestamps, never a clock read.
/// </summary>
public sealed class WidgetViewModelV3StateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings En = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));

    private static readonly string[] Cultures = { "en-US", "pt-PT", "pt-BR" };

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static WidgetServerState Server(
        int n,
        WidgetHealth health = WidgetHealth.Healthy,
        DateTimeOffset? updated = null,
        int? staleAfter = 60,
        string? metric = null,
        bool neverRead = false) => new()
    {
        Id = Id(n),
        DisplayName = $"srv{n}",
        Health = health,
        CpuUsagePercent = 10,
        MemoryUsagePercent = 20,
        DiskUsagePercent = 30,
        LastUpdatedUtc = neverRead ? null : updated ?? Now.AddSeconds(-5),
        StaleAfterSeconds = staleAfter,
        AttentionMetric = metric
    };

    private static WidgetReadResult Read(DateTimeOffset generatedAt, params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = generatedAt,
            OverallHealth = WidgetHealthPrecedence.Worst(servers.Select(s => s.Health)),
            Servers = servers
        });

    private static WidgetViewModel Build(WidgetReadResult read, WidgetSizeHint size = WidgetSizeHint.Medium,
        WidgetStrings? strings = null) =>
        WidgetViewModelBuilder.Build(read, size, Now, strings ?? En);

    private static string Rendered(WidgetViewModel vm)
    {
        var card = WidgetCardRenderer.Render(vm);
        return card.TemplateJson + card.DataJson;
    }

    // ---- Healthy -----------------------------------------------------------------------------------

    [Fact]
    public void All_healthy_and_fresh_is_the_only_healthy_card()
    {
        var vm = Build(Read(Now, Server(1), Server(2)));

        Assert.Equal(WidgetCardState.Healthy, vm.CardState);
        Assert.Equal("All healthy", vm.Title);
        Assert.Equal("2 servers connected", vm.Subtitle);
        Assert.Equal("2 healthy", vm.Summary);
        Assert.Equal(2, vm.HealthyFreshCount);
        Assert.Equal("2 of 2 healthy", vm.RingAltText);
        Assert.All(vm.Rows, r => Assert.Equal(WidgetRowState.Healthy, r.State));
        Assert.All(vm.Rows, r => Assert.Equal("Healthy", r.StatusText));
    }

    // ---- Stale snapshot (test 3 / D-UI9-5) -----------------------------------------------------------

    public static TheoryData<string, WidgetSizeHint> CultureBySize()
    {
        var data = new TheoryData<string, WidgetSizeHint>();
        foreach (var culture in Cultures)
        {
            foreach (var size in new[] { WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large })
            {
                data.Add(culture, size);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CultureBySize))]
    public void Stale_snapshot_never_claims_current_health_in_the_view_model_or_the_rendered_card(
        string culture, WidgetSizeHint size)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        // 600 s is the largest derivable threshold: one second past it is stale whatever the fleet.
        var generated = Now.AddSeconds(-(WidgetSchema.MaxStaleAfterSeconds + 1));
        var read = Read(generated,
            Server(1, updated: generated.AddSeconds(-1), staleAfter: WidgetSchema.MaxStaleAfterSeconds),
            Server(2, updated: generated.AddSeconds(-1)),
            Server(3, updated: generated.AddSeconds(-1)));

        var vm = Build(read, size, strings);
        var rendered = Rendered(vm);

        Assert.Equal(WidgetFreshnessState.Stale, vm.Freshness);
        Assert.Equal(WidgetCardState.Stale, vm.CardState);
        Assert.Equal(strings.StaleTitle, vm.Title);
        Assert.Equal(strings.StaleTitle, vm.Summary);
        Assert.Equal(0, vm.HealthyFreshCount);
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, strings.LastReadingMinutesAgo, 10), vm.FooterText);
        Assert.All(vm.Rows, r =>
        {
            Assert.True(r.IsStale);                              // V-RC-5c: never fresher than the snapshot
            Assert.Equal(WidgetRowState.NotUpdated, r.State);
            Assert.NotEqual(strings.StatusHealthy, r.StatusText);
            Assert.NotEqual(strings.Healthy, r.HealthLabel);     // legacy renderer label too
        });

        // The stale mark is on the card (the legacy hero upper-cases it), nothing claims "all healthy", and
        // nothing is painted "healthy" green (the legacy Large tiles keep the LAST-state counts, neutral).
        Assert.Equal(strings.StaleTitle, vm.HeroLabel);
        Assert.Contains(strings.StaleTitle.ToUpperInvariant(), rendered.ToUpperInvariant());
        Assert.DoesNotContain(strings.FleetAllHealthy, rendered);
        Assert.DoesNotContain(strings.AllHealthy, rendered);
        Assert.DoesNotContain("\"color\":\"good\"", rendered);
    }

    [Fact]
    public void Missing_snapshot_is_unavailable_never_healthy()
    {
        foreach (var culture in Cultures)
        {
            var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
            var vm = Build(WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing), WidgetSizeHint.Large, strings);

            Assert.Equal(WidgetCardState.Unavailable, vm.CardState);
            Assert.Equal(strings.UnavailableTitle, vm.Title);
            Assert.Equal(strings.UnavailableBody, vm.Body);
            Assert.Equal(string.Empty, vm.CtaText); // no add-server CTA on Unavailable
            var rendered = Rendered(vm);
            Assert.DoesNotContain(strings.FleetAllHealthy, rendered);
            Assert.DoesNotContain(strings.StatusHealthy, rendered);
        }
    }

    // ---- per-server freshness under a fresh snapshot (D-UI9-5, V-RC-5a) ------------------------------

    [Fact]
    public void Healthy_server_with_an_old_reading_is_not_updated_and_not_counted_healthy()
    {
        var vm = Build(Read(Now, Server(1), Server(2, updated: Now.AddSeconds(-61), staleAfter: 60)));

        var stale = Assert.Single(vm.Rows, r => r.ServerId == Id(2));
        Assert.Equal(WidgetRowState.NotUpdated, stale.State);
        Assert.Equal("Not updated", stale.StatusText);
        Assert.True(stale.IsStale);
        Assert.True(stale.ShowsMetrics); // last values kept (muted), parity with the app
        Assert.Equal(WidgetCardState.NoCurrentData, vm.CardState);
        Assert.Equal("1 without recent data", vm.Title);
        Assert.Equal(1, vm.HealthyFreshCount);
        Assert.Equal("1 healthy · 1 no data", vm.Summary);
    }

    [Fact]
    public void Connected_subtitle_counts_only_servers_with_a_fresh_reading()
    {
        // Cortex N-2: "N servers connected" must not vouch for a server whose reading is stale.
        var vm = Build(Read(Now, Server(1), Server(2, updated: Now.AddSeconds(-61), staleAfter: 60),
            Server(3, WidgetHealth.Warning, updated: Now.AddSeconds(-500))));
        Assert.Equal("1 server connected", vm.Subtitle);

        vm = Build(Read(Now, Server(1, updated: Now.AddSeconds(-61), staleAfter: 60)));
        Assert.Equal(string.Empty, vm.Subtitle); // omitted at 0 rather than "0 servers connected"
    }

    [Fact]
    public void Per_server_threshold_boundary_is_inclusive()
    {
        var vm = Build(Read(Now, Server(1, updated: Now.AddSeconds(-60), staleAfter: 60)));
        Assert.Equal(WidgetRowState.Healthy, Assert.Single(vm.Rows).State);

        vm = Build(Read(Now, Server(1, updated: Now.AddSeconds(-60).AddTicks(-1), staleAfter: 60)));
        Assert.Equal(WidgetRowState.NotUpdated, Assert.Single(vm.Rows).State);
    }

    [Fact]
    public void Healthy_server_never_read_is_never_fresh()
    {
        var vm = Build(Read(Now, Server(1, neverRead: true)));

        var row = Assert.Single(vm.Rows);
        Assert.Equal(WidgetRowState.NotUpdated, row.State);
        Assert.NotEqual(WidgetCardState.Healthy, vm.CardState);
        Assert.Equal(0, vm.HealthyFreshCount);
    }

    [Fact]
    public void Old_file_without_stale_after_falls_back_to_the_snapshot_threshold()
    {
        var vm = Build(Read(Now, Server(1, updated: Now.AddSeconds(-85), staleAfter: null)));
        Assert.Equal(WidgetRowState.Healthy, Assert.Single(vm.Rows).State); // 85 s ≤ 90 s

        vm = Build(Read(Now, Server(1, updated: Now.AddSeconds(-95), staleAfter: null)));
        Assert.Equal(WidgetRowState.NotUpdated, Assert.Single(vm.Rows).State);
    }

    // ---- precedence: staleness never hides attention ------------------------------------------------

    [Theory]
    [InlineData(WidgetHealth.Warning, WidgetAttentionMetrics.Disk, WidgetRowState.Warning, "High disk", "warning")]
    [InlineData(WidgetHealth.Critical, WidgetAttentionMetrics.Disk, WidgetRowState.Critical, "Critical disk", "attention")]
    [InlineData(WidgetHealth.Warning, WidgetAttentionMetrics.Cpu, WidgetRowState.Warning, "High CPU", "warning")]
    [InlineData(WidgetHealth.Critical, WidgetAttentionMetrics.Memory, WidgetRowState.Critical, "Critical RAM", "attention")]
    [InlineData(WidgetHealth.Warning, null, WidgetRowState.Warning, "Attention", "warning")]
    [InlineData(WidgetHealth.Critical, null, WidgetRowState.Critical, "Critical", "attention")]
    [InlineData(WidgetHealth.Offline, null, WidgetRowState.Offline, "No connection", "attention")]
    public void Attention_rows_keep_their_label_whether_fresh_or_stale(
        WidgetHealth health, string? metric, WidgetRowState expectedState, string expectedText, string expectedColor)
    {
        foreach (var updated in new[] { Now.AddSeconds(-5), Now.AddSeconds(-500) })
        {
            var vm = Build(Read(Now, Server(1, health, updated: updated, metric: metric)));
            var row = Assert.Single(vm.Rows);
            Assert.Equal(expectedState, row.State);
            Assert.Equal(expectedText, row.StatusText);
            Assert.Equal(expectedColor, row.StatusColor);
            Assert.Equal(WidgetCardState.Attention, vm.CardState);
            Assert.Equal("1 issue", vm.Title);
        }
    }

    [Fact]
    public void Critical_and_warning_text_differ_for_the_same_metric_in_every_culture()
    {
        foreach (var culture in Cultures)
        {
            var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
            var warning = Assert.Single(Build(Read(Now, Server(1, WidgetHealth.Warning, metric: "disk")), strings: strings).Rows);
            var critical = Assert.Single(Build(Read(Now, Server(1, WidgetHealth.Critical, metric: "disk")), strings: strings).Rows);
            Assert.NotEqual(warning.StatusText, critical.StatusText);
        }
    }

    [Fact]
    public void Offline_and_unknown_rows_hide_metrics()
    {
        var vm = Build(Read(Now, Server(1, WidgetHealth.Offline), Server(2, WidgetHealth.Unknown, neverRead: true)), WidgetSizeHint.Large);

        Assert.All(vm.Rows, r => Assert.False(r.ShowsMetrics));
        Assert.Equal("No data", Assert.Single(vm.Rows, r => r.ServerId == Id(2)).StatusText);
    }

    [Fact]
    public void Unknown_with_an_old_reading_is_not_updated_but_never_read_is_no_data()
    {
        var vm = Build(Read(Now,
            Server(1, WidgetHealth.Unknown, updated: Now.AddSeconds(-500)),
            Server(2, WidgetHealth.Unknown, neverRead: true)), WidgetSizeHint.Large);

        Assert.Equal(WidgetRowState.NotUpdated, Assert.Single(vm.Rows, r => r.ServerId == Id(1)).State);
        Assert.Equal(WidgetRowState.Unknown, Assert.Single(vm.Rows, r => r.ServerId == Id(2)).State);
        Assert.Equal(WidgetCardState.NoCurrentData, vm.CardState);
        Assert.Equal("2 without recent data", vm.Title);
    }

    [Fact]
    public void Unknown_is_never_folded_into_healthy_counts()
    {
        var vm = Build(Read(Now, Server(1), Server(2), Server(3, WidgetHealth.Unknown, neverRead: true)));

        Assert.Equal(2, vm.HealthyFreshCount);
        Assert.Equal("2 healthy · 1 no data", vm.Summary);
        Assert.Equal("2 of 3 healthy", vm.RingAltText);
        Assert.NotEqual("All healthy", vm.Title);
    }

    // ---- raw attentionMetric never reaches output (V-RC-6a) -----------------------------------------

    public static TheoryData<string> HostileMetrics => new()
    {
        // ("CPU"/"cpu " are covered by the contract allowlist test; the card legitimately shows the label "CPU".)
        "${x}", "${$host.hostTheme}", "", "[x](https://example.invalid)", "{\"$when\":\"true\"}", new string('$', 10 * 1024)
    };

    [Theory]
    [MemberData(nameof(HostileMetrics))]
    public void Unknown_attention_metric_falls_back_to_the_status_word_and_never_reaches_the_card(string raw)
    {
        foreach (var size in new[] { WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var vm = Build(Read(Now, Server(1, WidgetHealth.Warning, metric: raw)), size);
            Assert.All(vm.Rows, r => Assert.Equal("Attention", r.StatusText));

            if (raw.Length > 0)
            {
                Assert.DoesNotContain(raw, Rendered(vm));
            }
        }
    }

    [Fact]
    public void Attention_metric_is_ignored_when_the_engine_says_healthy()
    {
        var vm = Build(Read(Now, Server(1, WidgetHealth.Healthy, metric: "disk")));
        Assert.Equal("Healthy", Assert.Single(vm.Rows).StatusText);
        Assert.Equal(WidgetCardState.Healthy, vm.CardState);
    }

    // ---- Empty / overflow / footer -----------------------------------------------------------------

    [Fact]
    public void Empty_fleet_offers_the_dashboard_cta_with_size_specific_title()
    {
        var small = Build(Read(Now), WidgetSizeHint.Small);
        var medium = Build(Read(Now), WidgetSizeHint.Medium);

        Assert.Equal(WidgetCardState.Empty, small.CardState);
        Assert.Equal("No servers yet", small.Title);
        Assert.Equal("Your first server", medium.Title);
        Assert.Equal("Open ServerAlyzer", medium.CtaText);
        Assert.Equal(En.EmptyBody, medium.Body);
    }

    [Theory]
    [InlineData(WidgetSizeHint.Medium, 5, "2 of 5 servers")]
    [InlineData(WidgetSizeHint.Medium, 2, "2 of 2 servers")]
    [InlineData(WidgetSizeHint.Large, 1, "1 of 1 servers")]
    [InlineData(WidgetSizeHint.Large, 7, "3 of 7 servers")]
    [InlineData(WidgetSizeHint.Small, 7, "")]
    public void Rows_of_total_is_always_stated_for_list_sizes(WidgetSizeHint size, int count, string expected)
    {
        var servers = Enumerable.Range(1, count).Select(n => Server(n)).ToArray();
        var vm = Build(Read(Now, servers), size);

        Assert.Equal(expected, vm.RowsOfTotalText);
        Assert.Equal(count, vm.Rows.Count + vm.OverflowCount + (size == WidgetSizeHint.Small ? count : 0));
    }

    [Theory]
    [InlineData(30, null, "Updated just now")]
    [InlineData(125, 600, "Updated 2 min ago")]     // fresh under a derived 600 s threshold
    [InlineData(125, null, "Last reading 2 min ago")] // stale under the 90 s fallback
    [InlineData(7300, null, "Last reading 2 hr ago")]
    public void Footer_has_minute_granularity_and_switches_to_last_reading_when_stale(
        int ageSeconds, int? staleAfter, string expected)
    {
        var generated = Now.AddSeconds(-ageSeconds);
        var vm = Build(Read(generated, Server(1, updated: generated, staleAfter: staleAfter)));
        Assert.Equal(expected, vm.FooterText);
    }

    // ---- D-UI9-4: derived snapshot threshold ---------------------------------------------------------

    [Fact]
    public void A_five_minute_fleet_is_not_falsely_stale_between_its_own_cycles()
    {
        var generated = Now.AddMinutes(-5);
        var vm = Build(Read(generated, Server(1, updated: generated, staleAfter: 600)));

        Assert.Equal(WidgetFreshnessState.Fresh, vm.Freshness);
        Assert.Equal(WidgetCardState.Healthy, vm.CardState);
    }

    [Fact]
    public void The_same_age_is_stale_for_an_old_file_without_per_server_thresholds()
    {
        var generated = Now.AddMinutes(-5);
        var vm = Build(Read(generated, Server(1, updated: generated, staleAfter: null)));

        Assert.Equal(WidgetFreshnessState.Stale, vm.Freshness);
        Assert.Equal(WidgetCardState.Stale, vm.CardState);
    }
}
