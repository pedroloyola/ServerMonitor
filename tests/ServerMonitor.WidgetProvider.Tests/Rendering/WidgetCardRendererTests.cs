using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 C1 — the V3 template/data renderer (SPEC §6/§8, V-RC-3, V-RC-9, R-1, R-2, P-RC-2, RC-8/10).
/// Template-level properties are asserted on the raw constant templates; everything visible is asserted
/// on the card EXPANDED with <see cref="CardTemplateHarness"/>. Ids are fixed (FLAKE-WP-GUID) and every
/// age comes from explicit timestamps.
/// </summary>
public sealed class WidgetCardRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings En = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));
    private static readonly WidgetSizeHint[] Sizes = [WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large];
    private static readonly string[] Cultures = ["en-US", "pt-PT", "pt-BR"];

    /// <summary>Vigil's hostile fixture (V-RC-3 d) plus the TextBlock markdown/date vectors (R-1).</summary>
    public static readonly string[] HostileNames =
    [
        "${$host.hostTheme}", "${name}", "{\"$when\":\"true\"}", "\"}],\"x\":\"", "\\",
        "[x](https://example.invalid)", "**b**", "- a", "# h", new string('$', 60),
        "{{DATE(2017-02-14T06:08:00Z)}}", "${if(true,'EVAL','no')}", "@{name}", "_i_"
    ];

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    internal static WidgetServerState Server(
        int n,
        WidgetHealth health = WidgetHealth.Healthy,
        string? name = null,
        DateTimeOffset? updated = null,
        double? cpu = 41,
        double? mem = 73,
        double? disk = 92,
        string? metric = null) => new()
    {
        Id = Id(n),
        DisplayName = name ?? $"srv{n}",
        Health = health,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
        MemoryUsedGb = 3.2,
        MemoryTotalGb = 8,
        DiskUsedGb = 460,
        DiskTotalGb = 500,
        UptimeSeconds = 43L * 86400 + 18 * 3600,
        LastUpdatedUtc = updated ?? Now.AddSeconds(-5),
        StaleAfterSeconds = 60,
        AttentionMetric = metric
    };

    internal static WidgetReadResult Read(DateTimeOffset generatedAt, params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = generatedAt,
            OverallHealth = WidgetHealthPrecedence.Worst(servers.Select(s => s.Health)),
            Servers = servers
        });

    private static readonly WidgetReadResult Missing = WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing);

    /// <summary>Every card state, named, for a given set of names.</summary>
    internal static IEnumerable<(string State, WidgetReadResult Read)> States(string[]? names = null)
    {
        string N(int i) => names is null ? $"srv{i}" : names[i % names.Length];
        var old = Now.AddSeconds(-(WidgetSchema.MaxStaleAfterSeconds + 120));
        yield return ("healthy", Read(Now, Server(1, name: N(1)), Server(2, name: N(2)), Server(3, name: N(3))));
        yield return ("attention", Read(Now,
            Server(1, WidgetHealth.Critical, N(1), metric: "disk"),
            Server(2, WidgetHealth.Offline, N(2)),
            Server(3, WidgetHealth.Warning, N(3), metric: "cpu"),
            Server(4, name: N(4)), Server(5, name: N(5)), Server(6, name: N(6))));
        yield return ("nocurrentdata", Read(Now, Server(1, name: N(1)),
            Server(2, name: N(2), updated: Now.AddSeconds(-500)), Server(3, WidgetHealth.Unknown, N(3))));
        yield return ("stale", Read(old, Server(1, name: N(1), updated: old), Server(2, name: N(2), updated: old),
            Server(3, name: N(3), updated: old)));
        yield return ("empty", Read(Now));
        yield return ("empty-stale", Read(old));
        yield return ("unavailable", Missing);
        yield return ("overflow", Read(Now, Enumerable.Range(1, 12).Select(i => Server(i, name: N(i))).ToArray()));
    }

    internal static (WidgetViewModel Vm, WidgetCard Card, JsonObject Expanded) Render(
        WidgetReadResult read, WidgetSizeHint size, WidgetStrings? strings = null)
    {
        var vm = WidgetViewModelBuilder.Build(read, size, Now, strings ?? En);
        var card = WidgetCardRenderer.Render(vm);
        return (vm, card, CardTemplateHarness.Expand(card.TemplateJson, card.DataJson));
    }

    // =====================================================================================================
    // V-RC-3: constant templates, fixed data keys, untrusted names only in TextRuns
    // =====================================================================================================

    [Fact]
    public void Template_bytes_are_identical_across_every_state_and_hostile_snapshot()
    {
        foreach (var size in Sizes)
        {
            var templates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var names in new[] { null, HostileNames })
            {
                foreach (var culture in Cultures)
                {
                    foreach (var (_, read) in States(names))
                    {
                        templates.Add(Render(read, size, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture))).Card.TemplateJson);
                    }
                }
            }

            Assert.Equal(WidgetCardRenderer.TemplateFor(size), Assert.Single(templates));
        }
    }

    [Fact]
    public void Template_carries_no_literal_visible_text_only_bindings()
    {
        var binding = new Regex(@"^\$\{[A-Za-z]+\}$");
        foreach (var size in Sizes)
        {
            var template = JsonNode.Parse(WidgetCardRenderer.TemplateFor(size))!;
            var texts = CardTemplateHarness.Strings(template).Where(s => s.Key is "text" or "title" or "altText").ToList();

            Assert.NotEmpty(texts); // the walk is real
            Assert.All(texts, t => Assert.Matches(binding, t.Value));
        }
    }

    [Fact]
    public void Template_has_no_schema_header_url_image_or_open_url()
    {
        foreach (var size in Sizes)
        {
            var template = WidgetCardRenderer.TemplateFor(size);
            var keys = CardTemplateHarness.Objects(JsonNode.Parse(template)).SelectMany(o => o.Node.Select(p => p.Key)).ToHashSet();

            foreach (var forbidden in new[] { "$schema", "header", "url", "backgroundImage", "iconUrl" })
            {
                Assert.DoesNotContain(forbidden, keys);
            }

            Assert.DoesNotContain("\"Image\"", template);
            Assert.DoesNotContain("Action.OpenUrl", template);
            Assert.DoesNotContain("Action.Submit", template);
        }
    }

    [Fact]
    public void The_display_name_is_bound_exactly_once_and_only_as_a_TextRun()
    {
        foreach (var size in Sizes)
        {
            var template = JsonNode.Parse(WidgetCardRenderer.TemplateFor(size))!;
            var bindings = CardTemplateHarness.Objects(template)
                .Where(o => o.Node.Any(p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("${name}")))
                .ToList();

            if (size == WidgetSizeHint.Small)
            {
                Assert.Empty(bindings); // Small has no rows
                continue;
            }

            var (node, owner) = Assert.Single(bindings);
            Assert.Equal("TextRun", (string?)node["type"]);
            Assert.Equal("inlines", owner);                 // inside a RichTextBlock, never a TextBlock
            Assert.Equal("${name}", (string?)node["text"]);  // never concatenated with other text
        }
    }

    [Fact]
    public void Data_keys_are_fixed_whatever_the_state()
    {
        foreach (var size in Sizes)
        {
            var rootKeys = new HashSet<string>(StringComparer.Ordinal);
            var rowKeys = new HashSet<string>(StringComparer.Ordinal);
            var rowsSeen = 0;
            foreach (var names in new[] { null, HostileNames })
            {
                foreach (var (_, read) in States(names))
                {
                    var data = JsonNode.Parse(Render(read, size).Card.DataJson)!.AsObject();
                    rootKeys.Add(string.Join(",", data.Select(p => p.Key)));
                    foreach (var row in data["rows"]!.AsArray())
                    {
                        rowsSeen++;
                        rowKeys.Add(string.Join(",", row!.AsObject().Select(p => p.Key)));
                    }
                }
            }

            Assert.Single(rootKeys);
            if (size != WidgetSizeHint.Small)
            {
                Assert.True(rowsSeen > 0);
                Assert.Single(rowKeys);
            }
        }
    }

    [Theory]
    [MemberData(nameof(HostileNameData))]
    public void A_hostile_name_reaches_the_card_only_as_a_literal_TextRun(string hostile)
    {
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var (vm, card, expanded) = Render(Read(Now, Server(1, WidgetHealth.Warning, hostile, metric: "disk")), size);
            var shown = vm.Rows[0].DisplayName.Value;

            // Data: the name is a VALUE of exactly one key, "name" — never a key, never in another value.
            var data = JsonNode.Parse(card.DataJson)!;
            Assert.All(CardTemplateHarness.Strings(data).Where(s => s.Value.Contains(shown, StringComparison.Ordinal)),
                s => Assert.Equal("name", s.Key));

            // Expanded: literal, unevaluated, in a TextRun; no TextBlock carries it.
            var carriers = CardTemplateHarness.Objects(expanded)
                .Where(o => (string?)o.Node["text"] == shown).ToList();
            var (run, owner) = Assert.Single(carriers);
            Assert.Equal("TextRun", (string?)run["type"]);
            Assert.Equal("inlines", owner);
            Assert.DoesNotContain(CardTemplateHarness.Objects(expanded),
                o => (string?)o.Node["type"] == "TextBlock" && ((string?)o.Node["text"])?.Contains(shown, StringComparison.Ordinal) == true);

            // The card structure is unchanged by the name.
            Assert.Empty(CardTemplateHarness.ShapeErrors(expanded));
        }
    }

    public static TheoryData<string> HostileNameData()
    {
        var data = new TheoryData<string>();
        foreach (var name in HostileNames)
        {
            data.Add(name);
        }

        return data;
    }

    // =====================================================================================================
    // SPEC test 10: valid AC 1.6 per size × state × culture
    // =====================================================================================================

    [Fact]
    public void Every_size_state_and_culture_expands_to_a_valid_card()
    {
        var checkedCards = 0;
        foreach (var size in Sizes)
        {
            foreach (var culture in Cultures)
            {
                foreach (var (state, read) in States())
                {
                    var (_, _, expanded) = Render(read, size, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture)));
                    var errors = CardTemplateHarness.ShapeErrors(expanded);
                    Assert.True(errors.Count == 0, $"{size}/{state}/{culture}: {string.Join("; ", errors)}");
                    Assert.DoesNotContain(CardTemplateHarness.Strings(expanded), s => s.Value.Contains("${", StringComparison.Ordinal));
                    Assert.All(CardTemplateHarness.VisibleTexts(expanded), t => Assert.DoesNotContain("{0}", t));
                    Assert.DoesNotContain(CardTemplateHarness.Objects(expanded), o => (string?)o.Node["type"] == "TextRun" && (string?)o.Node["text"] == string.Empty);
                    checkedCards++;
                }
            }
        }

        Assert.Equal(3 * 3 * 8, checkedCards);
    }

    // =====================================================================================================
    // V-RC-9: no URL of any kind; only Action.Execute; verb set == {openDashboard, openServer}
    // =====================================================================================================

    [Fact]
    public void No_card_carries_a_url_and_the_verb_set_is_exactly_the_two_contract_verbs()
    {
        var url = new Regex(@"(?i)(https?:|file:|data:|ms-appx:|ms-appdata:|\\\\|//)");
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var size in Sizes)
        {
            Assert.DoesNotMatch(url, WidgetCardRenderer.TemplateFor(size));
            foreach (var culture in Cultures)
            {
                foreach (var (_, read) in States())
                {
                    var (_, card, expanded) = Render(read, size, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture)));
                    Assert.DoesNotMatch(url, card.DataJson);
                    foreach (var action in CardTemplateHarness.Actions(expanded))
                    {
                        Assert.Equal("Action.Execute", (string?)action["type"]);
                        verbs.Add((string)action["verb"]!);
                    }
                }
            }
        }

        Assert.Equal(new[] { "openDashboard", "openServer" }, verbs.Order(StringComparer.Ordinal));
    }

    // =====================================================================================================
    // SPEC test 4: activation keeps its target
    // =====================================================================================================

    [Theory]
    [InlineData(WidgetSizeHint.Medium)]
    [InlineData(WidgetSizeHint.Large)]
    public void Each_visible_row_opens_its_own_server_and_the_card_opens_the_dashboard(WidgetSizeHint size)
    {
        var (vm, _, expanded) = Render(Read(Now, Server(1), Server(2, WidgetHealth.Warning), Server(3, WidgetHealth.Critical)), size);

        Assert.Equal("openDashboard", (string?)expanded["selectAction"]!["verb"]);
        var rows = CardTemplateHarness.Rows(expanded);
        Assert.Equal(vm.Rows.Count, rows.Count);
        Assert.True(rows.Count >= 2);
        for (var i = 0; i < rows.Count; i++)
        {
            var action = rows[i]["selectAction"]!.AsObject();
            Assert.Equal(vm.Rows[i].ServerId.ToString("D"), (string?)action["data"]!["serverId"]);
            Assert.Equal(["data", "type", "verb"], action.Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.Contains(vm.Rows[i].DisplayName.Value, CardTemplateHarness.VisibleTexts(rows[i]));
        }
    }

    [Theory]
    [InlineData(WidgetSizeHint.Small)]
    [InlineData(WidgetSizeHint.Medium)]
    [InlineData(WidgetSizeHint.Large)]
    public void Empty_offers_the_dashboard_cta_and_unavailable_offers_none(WidgetSizeHint size)
    {
        var (_, _, empty) = Render(Read(Now), size);
        var cta = CardTemplateHarness.Objects(empty).Select(o => o.Node).Single(o => (string?)o["type"] == "ActionSet");
        var button = cta["actions"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Action.Execute", (string?)button["type"]);
        Assert.Equal("openDashboard", (string?)button["verb"]);
        Assert.Equal(En.EmptyCta, (string?)button["title"]);
        Assert.Null(button["data"]);

        var (_, _, unavailable) = Render(Missing, size);
        Assert.DoesNotContain(CardTemplateHarness.Objects(unavailable), o => (string?)o.Node["type"] == "ActionSet");
        Assert.Equal("openDashboard", (string?)unavailable["selectAction"]!["verb"]); // SPEC §3: card opens Dashboard
        Assert.Contains(En.UnavailableTitle, CardTemplateHarness.VisibleTexts(unavailable));
        Assert.DoesNotContain(En.FleetAllHealthy, CardTemplateHarness.VisibleTexts(unavailable));
    }

    // =====================================================================================================
    // SPEC test 3 + D-UI9-5 + DV-4 exit criterion: never healthy text when stale / not updated
    // =====================================================================================================

    [Fact]
    public void A_stale_card_never_paints_healthy_text_or_healthy_green()
    {
        foreach (var size in Sizes)
        {
            foreach (var culture in Cultures)
            {
                var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
                var (_, _, expanded) = Render(States().Single(s => s.State == "stale").Read, size, strings);
                var texts = CardTemplateHarness.VisibleTexts(expanded);

                Assert.Contains(strings.StaleTitle, texts);
                Assert.DoesNotContain(strings.FleetAllHealthy, texts);
                Assert.DoesNotContain(strings.StatusHealthy, texts);
                Assert.DoesNotContain(CardTemplateHarness.Strings(expanded), s => s is { Key: "color", Value: "good" });
            }
        }
    }

    [Fact]
    public void A_not_updated_row_in_a_fresh_snapshot_never_reads_healthy()
    {
        // Cortex DV-4 exit criterion: the per-row V3 status is RENDERED, not only modelled.
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var (_, _, expanded) = Render(Read(Now, Server(1), Server(2, updated: Now.AddSeconds(-500))), size);
            var stale = CardTemplateHarness.Rows(expanded).Single(r => (string?)r["selectAction"]!["data"]!["serverId"] == Id(2).ToString("D"));
            var texts = CardTemplateHarness.VisibleTexts(stale);

            Assert.Contains(En.StatusRowStale, texts);
            Assert.DoesNotContain(En.StatusHealthy, texts);
            Assert.DoesNotContain(CardTemplateHarness.Strings(stale), s => s is { Key: "color", Value: "good" or "accent" });
        }
    }

    [Fact]
    public void An_empty_snapshot_that_is_stale_does_not_claim_no_servers_yet()
    {
        // Vigil V-B2.
        foreach (var size in Sizes)
        {
            var (_, _, expanded) = Render(States().Single(s => s.State == "empty-stale").Read, size);
            var texts = CardTemplateHarness.VisibleTexts(expanded);
            Assert.Contains(En.StaleTitle, texts);
            Assert.DoesNotContain(En.EmptyTitleSmall, texts);
            Assert.DoesNotContain(En.EmptyTitle, texts);
            Assert.DoesNotContain(En.EmptyBody, texts);
        }
    }

    // =====================================================================================================
    // Overflow invariant: visible + overflow == total, "N of M" always on list sizes
    // =====================================================================================================

    [Theory]
    [InlineData(WidgetSizeHint.Small, 12)]
    [InlineData(WidgetSizeHint.Medium, 1)]
    [InlineData(WidgetSizeHint.Medium, 2)]
    [InlineData(WidgetSizeHint.Medium, 12)]
    [InlineData(WidgetSizeHint.Large, 3)]
    [InlineData(WidgetSizeHint.Large, 12)]
    public void Rows_on_the_card_plus_overflow_account_for_every_server(WidgetSizeHint size, int total)
    {
        var (vm, _, expanded) = Render(Read(Now, Enumerable.Range(1, total).Select(i => Server(i)).ToArray()), size);
        var rows = CardTemplateHarness.Rows(expanded);

        Assert.Equal(Math.Min(total, WidgetViewModelBuilder.MaxRowsFor(size)), rows.Count);
        if (size == WidgetSizeHint.Small)
        {
            Assert.Contains($"{total}/{total}", CardTemplateHarness.VisibleTexts(expanded)); // the fraction states the fleet
            return;
        }

        Assert.Equal(total, rows.Count + vm.OverflowCount);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, En.OverflowFormat, rows.Count, total),
            CardTemplateHarness.VisibleTexts(expanded));
    }

    // =====================================================================================================
    // Meters (R-2): shape-distinguished glyphs, neutral fill, % always text; Offline/Unknown → "—", none
    // =====================================================================================================

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(41, 2)]
    [InlineData(50, 3)]
    [InlineData(89, 4)]
    [InlineData(90, 5)]
    [InlineData(100, 5)]
    public void Filled_segments_round_to_the_nearest_fifth(int percent, int filled) =>
        Assert.Equal(filled, WidgetCardRenderer.FilledSegments(percent));

    private static List<JsonObject> Meters(JsonNode row) =>
        CardTemplateHarness.Objects(row).Select(o => o.Node)
            .Where(o => (string?)o["type"] == "RichTextBlock"
                && o["inlines"]!.AsArray().All(r => ((string?)r!["text"])!.All(c => c is '▰' or '▱')))
            .ToList();

    [Fact]
    public void A_fresh_meter_is_neutral_accent_fill_plus_subtle_track_and_never_a_health_colour()
    {
        var (_, _, expanded) = Render(Read(Now, Server(1, WidgetHealth.Critical, metric: "disk", cpu: 41)), WidgetSizeHint.Medium);
        var row = CardTemplateHarness.Rows(expanded).Single();
        var meters = Meters(row);

        Assert.Equal(3, meters.Count); // CPU, RAM, Disk
        foreach (var meter in meters)
        {
            var runs = meter["inlines"]!.AsArray().Select(r => r!.AsObject()).ToList();
            Assert.Equal(WidgetCardRenderer.MeterSegments, runs.Sum(r => ((string)r["text"]!).Length));
            Assert.All(runs.Where(r => ((string)r["text"]!).Contains('▰')), r => Assert.Equal("accent", (string?)r["color"]));
            Assert.All(runs.Where(r => ((string)r["text"]!).Contains('▱')), r => Assert.True((bool)r["isSubtle"]!));
            Assert.DoesNotContain(CardTemplateHarness.Strings(meter), s => s is { Key: "color", Value: "good" or "warning" or "attention" });
        }

        Assert.Contains("41%", CardTemplateHarness.VisibleTexts(row)); // the % is text
    }

    [Fact]
    public void A_stale_row_meter_is_muted()
    {
        var (_, _, expanded) = Render(Read(Now, Server(1, updated: Now.AddSeconds(-500))), WidgetSizeHint.Medium);
        var meters = Meters(CardTemplateHarness.Rows(expanded).Single());

        Assert.Equal(3, meters.Count);
        Assert.All(meters, m => Assert.All(m["inlines"]!.AsArray(), r =>
        {
            Assert.Null(r!["color"]);
            Assert.True((bool)r["isSubtle"]!);
        }));
    }

    [Fact]
    public void Offline_and_unknown_rows_show_dashes_and_no_meter_and_unknown_metrics_never_read_zero()
    {
        var (_, _, expanded) = Render(Read(Now, Server(1, WidgetHealth.Offline), Server(2, WidgetHealth.Unknown),
            Server(3, cpu: null)), WidgetSizeHint.Large);
        var rows = CardTemplateHarness.Rows(expanded);
        Assert.Equal(3, rows.Count);

        foreach (var row in rows.Take(2))
        {
            var texts = CardTemplateHarness.VisibleTexts(row);
            Assert.DoesNotContain(texts, t => t.EndsWith('%'));
            Assert.Empty(Meters(row));
            Assert.Equal(6, texts.Count(t => t == En.MetricUnknown)); // 3 values + 3 details
        }

        var partial = CardTemplateHarness.VisibleTexts(rows[2]);
        Assert.Contains(En.MetricUnknown, partial);
        Assert.DoesNotContain("0%", partial);
        Assert.Equal(2, Meters(rows[2]).Count); // RAM + Disk only
    }

    // =====================================================================================================
    // L detail (RC-10) and the Small fleet bar (P-RC-2)
    // =====================================================================================================

    [Theory]
    [InlineData("en-US", "Up 43d 18h", "3.2/8 GB", "460/500 GB")]
    [InlineData("pt-PT", "Ativo 43d 18h", "3,2/8 GB", "460/500 GB")]
    [InlineData("pt-BR", "Ativo 43d 18h", "3,2/8 GB", "460/500 GB")]
    public void Large_rows_carry_culture_aware_detail_and_medium_rows_do_not(string culture, string up, string ram, string disk)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        var large = CardTemplateHarness.VisibleTexts(CardTemplateHarness.Rows(Render(Read(Now, Server(1)), WidgetSizeHint.Large, strings).Expanded).Single());
        Assert.Contains(up, large);
        Assert.Contains(ram, large);
        Assert.Contains(disk, large);

        var medium = CardTemplateHarness.VisibleTexts(CardTemplateHarness.Rows(Render(Read(Now, Server(1)), WidgetSizeHint.Medium, strings).Expanded).Single());
        Assert.DoesNotContain(up, medium);
        Assert.DoesNotContain(medium, t => t.EndsWith(" GB", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void Small_fleet_bar_is_drawn_up_to_the_budget_and_omitted_above(int servers, bool drawn)
    {
        var (_, _, expanded) = Render(Read(Now, Enumerable.Range(1, servers).Select(i => Server(i)).ToArray()), WidgetSizeHint.Small);
        var glyphs = string.Concat(CardTemplateHarness.VisibleTexts(expanded).Where(t => t.Length > 0 && t.All(c => c is '▰' or '▱')));

        Assert.Equal(drawn ? servers : 0, glyphs.Length);
        Assert.Contains($"{servers}/{servers}", CardTemplateHarness.VisibleTexts(expanded));
    }

    [Fact]
    public void Small_fraction_counts_only_healthy_and_fresh_servers()
    {
        // P-RC-2 / Cortex N-4: a Healthy server with a stale reading never fills the fraction.
        var (vm, _, expanded) = Render(Read(Now, Server(1), Server(2, updated: Now.AddSeconds(-500)), Server(3)), WidgetSizeHint.Small);
        Assert.Equal("2/3", vm.FractionText);
        Assert.Contains("2/3", CardTemplateHarness.VisibleTexts(expanded));
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, En.RingAltFormat, 2, 3), vm.RingAltText);
    }

    // =====================================================================================================
    // a11y (RC-8/R-4): titles are headings; state is always text
    // =====================================================================================================

    [Fact]
    public void Titles_use_the_heading_style_and_every_coloured_text_carries_words()
    {
        foreach (var size in Sizes)
        {
            foreach (var (_, read) in States())
            {
                var (vm, _, expanded) = Render(read, size);
                var headings = CardTemplateHarness.Objects(expanded).Select(o => o.Node).Where(o => (string?)o["style"] == "heading").ToList();
                if (size == WidgetSizeHint.Small || vm.CardState is WidgetCardState.Empty or WidgetCardState.Unavailable)
                {
                    Assert.Contains(headings, h => (string?)h["text"] == vm.Title);
                }

                // Colour never stands alone: every coloured TextBlock has letters or digits.
                Assert.All(CardTemplateHarness.Objects(expanded).Select(o => o.Node)
                        .Where(o => (string?)o["type"] == "TextBlock" && o["color"] is not null),
                    o => Assert.Matches(@"[\p{L}\p{N}]", (string?)o["text"] ?? string.Empty));
            }
        }
    }
}
