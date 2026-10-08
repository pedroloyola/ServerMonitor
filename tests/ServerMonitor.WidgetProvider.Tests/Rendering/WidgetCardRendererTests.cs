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
        WidgetReadResult read, WidgetSizeHint size, WidgetStrings? strings = null, string hostTheme = "dark")
    {
        var vm = WidgetViewModelBuilder.Build(read, size, Now, strings ?? En);
        var card = WidgetCardRenderer.Render(vm);
        return (vm, card, CardTemplateHarness.Expand(card.TemplateJson, card.DataJson, hostTheme));
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
    public void Template_has_no_schema_header_or_open_url_and_every_url_is_a_literal_image_constant()
    {
        // UI.9 C3 (Vigil C0 §2): images exist now, but only as WidgetImages constants written literally.
        // The full scan (data JSON, expanded cards, PNG chunks) is WidgetImageScanTests.
        foreach (var size in Sizes)
        {
            var template = WidgetCardRenderer.TemplateFor(size);
            var keys = CardTemplateHarness.Objects(JsonNode.Parse(template)).SelectMany(o => o.Node.Select(p => p.Key)).ToHashSet();

            foreach (var forbidden in new[] { "$schema", "header", "iconUrl" })
            {
                Assert.DoesNotContain(forbidden, keys);
            }

            Assert.All(CardTemplateHarness.Strings(JsonNode.Parse(template)).Where(s => s.Key == "url"),
                s => Assert.Contains(s.Value, WidgetImages.All));
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
            var shown = vm.Rows[0].DisplayName.ForCard(); // M-3: the emitted (neutralised) form

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
    public void No_card_carries_a_url_beyond_the_image_constants_and_the_verb_set_is_exactly_the_two_contract_verbs()
    {
        var url = new Regex(@"(?i)(https?:|file:|data:|ms-appx:|ms-appdata:|\\\\|//)");
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var size in Sizes)
        {
            // C3: the only URLs are the WidgetImages constants; every other string value is URL-free. Compared on
            // the parsed values (what the host reads): the serializer writes '+' as + in the raw JSON.
            var values = CardTemplateHarness.Strings(JsonNode.Parse(WidgetCardRenderer.TemplateFor(size))).ToList();
            Assert.All(values.Where(s => !WidgetImages.All.Contains(s.Value)), s => Assert.DoesNotMatch(url, s.Value));
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
            Assert.Contains(vm.Rows[i].DisplayName.ForCard(), CardTemplateHarness.VisibleTexts(rows[i]));
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

        // Cortex L-1: on Small the CTA is the ONLY touch target; Medium/Large also keep the card action.
        var emptyActions = CardTemplateHarness.Actions(empty);
        Assert.Equal(size == WidgetSizeHint.Small ? 1 : 2, emptyActions.Count);
        Assert.Equal(size == WidgetSizeHint.Small, empty["selectAction"] is null);

        var (_, _, unavailable) = Render(Missing, size);
        Assert.DoesNotContain(CardTemplateHarness.Objects(unavailable), o => (string?)o.Node["type"] == "ActionSet");
        var only = Assert.Single(CardTemplateHarness.Actions(unavailable)); // SPEC §3: the card opens the Dashboard
        Assert.Equal("openDashboard", (string?)only["verb"]);
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

        Assert.Equal(Math.Min(total, WidgetLayout.MaxRowsFor(size)), rows.Count);
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
    // Meters. UI.9 C3 (Prism C0 §1): continuous bars, metric colour per theme, % always text;
    // Offline/Unknown → "—", no bar. The ▰/▱ glyph meter stays behind WidgetLayout.Meter as the fallback.
    // =====================================================================================================

    // Glyph fallback, Prism P-C1-5 (replaces DV-10): 0 only at 0 %, full only at 100 %, otherwise
    // clamp(round(p/20), 1, 4).
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 1)]
    [InlineData(41, 2)]
    [InlineData(50, 3)]
    [InlineData(89, 4)]
    [InlineData(90, 4)]
    [InlineData(99, 4)]
    [InlineData(100, 5)]
    public void Filled_segments_never_read_empty_or_full_unless_exactly_0_or_100(int percent, int filled) =>
        Assert.Equal(filled, WidgetLayout.FilledSegments(percent));

    // Prism C0 §1 weight rule: 0 and 100 exact; 1–2 → 3 and 98–99 → 97, so a tiny fill or a tiny track
    // stays visible and a near-full bar never reads full.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(50, 50)]
    [InlineData(97, 97)]
    [InlineData(98, 97)]
    [InlineData(99, 97)]
    [InlineData(100, 100)]
    public void Bar_fill_weight_follows_the_w_rule(int percent, int weight) =>
        Assert.Equal(weight, WidgetLayout.BarFillWeight(percent));

    /// <summary>One segment of an expanded bar: its constant url, column weight and height.</summary>
    internal sealed record BarSegment(string Url, int Weight, string MinHeight);

    /// <summary>An expanded bar: the fill and track segments, null when $when removed one.</summary>
    internal sealed record BarView(BarSegment? Fill, BarSegment? Track);

    internal static List<BarView> Bars(JsonNode node) =>
        CardTemplateHarness.Objects(node).Select(o => o.Node)
            .Where(o => (string?)o["type"] == "ColumnSet" && o["columns"]!.AsArray().Count > 0
                && o["columns"]!.AsArray().All(c => BarSegmentOf(c!.AsObject()) is not null))
            .Select(o =>
            {
                var segments = o["columns"]!.AsArray().Select(c => BarSegmentOf(c!.AsObject())!).ToList();
                var track = segments.SingleOrDefault(s => s.Url == WidgetImages.BarTrackDark || s.Url == WidgetImages.BarTrackLight);
                var fill = segments.SingleOrDefault(s => s != track);
                Assert.Equal(segments.Count, (fill is null ? 0 : 1) + (track is null ? 0 : 1));
                return new BarView(fill, track);
            })
            .ToList();

    private static BarSegment? BarSegmentOf(JsonObject column)
    {
        if ((string?)column["type"] != "Column" || column["items"] is not JsonArray { Count: 1 } items
            || items[0] is not JsonObject inner || inner["backgroundImage"] is not JsonObject bg)
        {
            return null;
        }

        Assert.Empty(inner["items"]!.AsArray()); // an empty container: the bar is pure background
        Assert.Equal("repeat", (string?)bg["fillMode"]);
        return new BarSegment((string)bg["url"]!, (int)column["width"]!, (string)inner["minHeight"]!);
    }

    public static TheoryData<string, string, string, string, string> ThemeBarColours() => new()
    {
        { "dark", WidgetImages.BarCpuDark, WidgetImages.BarRamDark, WidgetImages.BarDiskDark, WidgetImages.BarTrackDark },
        { "light", WidgetImages.BarCpuLight, WidgetImages.BarRamLight, WidgetImages.BarDiskLight, WidgetImages.BarTrackLight },
    };

    [Theory]
    [MemberData(nameof(ThemeBarColours))]
    public void A_fresh_bar_uses_the_metric_colour_of_the_host_theme_and_never_a_state_colour(
        string theme, string cpu, string ram, string disk, string track)
    {
        foreach (var (size, height) in new[] { (WidgetSizeHint.Medium, "4px"), (WidgetSizeHint.Large, "6px") })
        {
            // A Critical row with a Disk attention metric: the bar colours still say CPU/RAM/Disk, not Critical.
            var (_, _, expanded) = Render(Read(Now, Server(1, WidgetHealth.Critical, metric: "disk", cpu: 41)), size, hostTheme: theme);
            var row = CardTemplateHarness.Rows(expanded).Single();
            var bars = Bars(row);

            Assert.Equal(3, bars.Count); // only the host theme's set survives $when
            Assert.Equal(new[] { cpu, ram, disk }, bars.Select(b => b.Fill!.Url).ToArray());
            Assert.Equal(new[] { 41, 73, 92 }, bars.Select(b => b.Fill!.Weight).ToArray());
            Assert.All(bars, b =>
            {
                Assert.Equal(track, b.Track!.Url);
                Assert.Equal(100, b.Fill!.Weight + b.Track.Weight);
                Assert.Equal(height, b.Fill.MinHeight);
                Assert.Equal(height, b.Track.MinHeight);
            });
            Assert.Contains("41%", CardTemplateHarness.VisibleTexts(row)); // the % is text
        }
    }

    [Theory]
    [InlineData(0, null, 100)]
    [InlineData(1, 3, 97)]
    [InlineData(99, 97, 3)]
    [InlineData(100, 100, null)]
    public void A_bar_never_draws_a_zero_weight_segment(int percent, int? fill, int? track)
    {
        var (_, _, expanded) = Render(Read(Now, Server(1, cpu: percent)), WidgetSizeHint.Medium);
        var bar = Bars(CardTemplateHarness.Rows(expanded).Single())[0];

        Assert.Equal(fill, bar.Fill?.Weight);
        Assert.Equal(track, bar.Track?.Weight);
        Assert.Contains($"{percent}%", CardTemplateHarness.VisibleTexts(expanded));
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void A_stale_row_bar_is_muted(string theme)
    {
        var (_, _, expanded) = Render(Read(Now, Server(1, updated: Now.AddSeconds(-500))), WidgetSizeHint.Medium, hostTheme: theme);
        var bars = Bars(CardTemplateHarness.Rows(expanded).Single());

        Assert.Equal(3, bars.Count);
        var stale = theme == "dark" ? WidgetImages.BarStaleDark : WidgetImages.BarStaleLight;
        Assert.All(bars, b => Assert.Equal(stale, b.Fill!.Url));
    }

    [Fact]
    public void Bars_are_the_active_meter_and_the_glyph_meter_stays_only_as_the_fallback_seam()
    {
        Assert.Equal(WidgetLayout.MeterStyle.Bars, WidgetLayout.Meter);
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var (_, _, expanded) = Render(States().Single(s => s.State == "healthy").Read, size);
            Assert.DoesNotContain(CardTemplateHarness.VisibleTexts(expanded), t => t.Contains('▰') || t.Contains('▱'));
            Assert.Equal(9, Bars(expanded).Count); // 3 rows x 3 metrics
        }
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
            Assert.Empty(Bars(row));
            Assert.Equal(6, texts.Count(t => t == En.MetricUnknown)); // 3 values + 3 details
        }

        var partial = CardTemplateHarness.VisibleTexts(rows[2]);
        Assert.Contains(En.MetricUnknown, partial);
        Assert.DoesNotContain("0%", partial);
        Assert.Equal(2, Bars(rows[2]).Count); // RAM + Disk only
    }

    // =====================================================================================================
    // Prism C1 review: label+value in one block (P-C1-1), problem-only wrapping summary (P-C1-2)
    // =====================================================================================================

    [Fact]
    public void A_metric_cell_holds_label_and_value_in_one_text_block_never_a_label_value_column_set()
    {
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var template = JsonNode.Parse(WidgetCardRenderer.TemplateFor(size))!;
            foreach (var key in new[] { "cpu", "mem", "disk" })
            {
                var block = CardTemplateHarness.Objects(template).Select(o => o.Node)
                    .Single(o => (string?)o["type"] == "RichTextBlock"
                        && o["inlines"]!.AsArray().Any(r => (string?)r!["text"] == "${" + key + "Value}"));
                var runs = block["inlines"]!.AsArray().Select(r => (string)r!["text"]!).ToArray();
                Assert.Equal(["${" + key + "Label}", "${gap}", "${" + key + "Value}"], runs);

                // The metric cell (its column) has no ColumnSet holding text: the only ColumnSets in it are
                // the C3 bars, which hold empty background containers.
                var cell = CardTemplateHarness.Objects(template).Select(o => o.Node)
                    .Single(o => (string?)o["type"] == "Column" && CardTemplateHarness.Objects(o).Any(i => i.Node == block));
                Assert.All(CardTemplateHarness.Objects(cell["items"]).Where(o => (string?)o.Node["type"] == "ColumnSet"),
                    set => Assert.DoesNotContain(CardTemplateHarness.Objects(set.Node),
                        o => (string?)o.Node["type"] is "TextBlock" or "RichTextBlock" or "TextRun"));
            }
        }
    }

    [Theory]
    [InlineData(100.0, "100%")]
    [InlineData(null, "—")]
    public void Value_fixtures_stay_whole_inside_the_label_block(double? cpu, string expected)
    {
        var (_, _, expanded) = Render(Read(Now, Server(1, cpu: cpu)), WidgetSizeHint.Medium);
        var row = CardTemplateHarness.Rows(expanded).Single();
        var block = CardTemplateHarness.Objects(row).Select(o => o.Node)
            .First(o => (string?)o["type"] == "RichTextBlock" && (string?)o["inlines"]![0]!["text"] == "CPU");
        Assert.Equal(["CPU", " ", expected], block["inlines"]!.AsArray().Select(r => (string)r!["text"]!).ToArray());
    }

    [Fact]
    public void The_worst_case_summary_lists_every_problem_and_the_template_wraps_it()
    {
        var (vm, card, _) = Render(Read(Now,
            Server(1, WidgetHealth.Warning), Server(2, WidgetHealth.Critical), Server(3, WidgetHealth.Offline),
            Server(4, updated: Now.AddSeconds(-500)), Server(5)), WidgetSizeHint.Medium);

        Assert.Equal("1\u00A0attention · 1\u00A0critical · 1\u00A0no\u00A0connection · 1\u00A0without\u00A0recent\u00A0data", vm.Summary);
        Assert.Equal(vm.Summary, (string?)JsonNode.Parse(card.DataJson)!["summary"]);
        Assert.DoesNotContain("healthy", vm.Summary);

        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var template = JsonNode.Parse(WidgetCardRenderer.TemplateFor(size))!;
            var summary = CardTemplateHarness.Objects(template).Select(o => o.Node).Single(o => (string?)o["text"] == "${summary}");
            Assert.True((bool)summary["wrap"]!);
            var columns = CardTemplateHarness.Objects(template).Select(o => o.Node)
                .Where(o => (string?)o["type"] == "Column" && (CardTemplateHarness.Objects(o).Any(i => (string?)i.Node["text"] is "${summary}" or "${serversHeading}")))
                .ToList();
            Assert.Equal("auto", (string?)columns.Single(c => CardTemplateHarness.Objects(c).Any(i => (string?)i.Node["text"] == "${serversHeading}"))["width"]);
            Assert.Equal("stretch", (string?)columns.Single(c => CardTemplateHarness.Objects(c).Any(i => (string?)i.Node["text"] == "${summary}"))["width"]);
        }
    }

    [Fact]
    public void An_all_healthy_summary_states_the_healthy_count()
    {
        var (vm, _, _) = Render(Read(Now, Server(1), Server(2), Server(3)), WidgetSizeHint.Medium);
        Assert.Equal("3\u00A0healthy", vm.Summary);
    }

    // =====================================================================================================
    // Vigil C1 L-1/L-2: untrusted text never stringifies; colours only from a closed allowlist
    // =====================================================================================================

    [Fact]
    public void UntrustedText_never_turns_back_into_the_name_by_accident()
    {
        var name = new UntrustedText("[x](https://example.invalid)");
        Assert.Equal("[untrusted]", name.ToString());
        Assert.Equal("[untrusted]", $"{name}");
        Assert.Equal("[x](https://example.invalid)", name.Value);
    }

    [Fact]
    public void Every_colour_on_every_card_is_from_the_closed_allowlist()
    {
        string[] allowed = ["default", "good", "warning", "attention", "accent"];
        var seen = 0;
        foreach (var size in Sizes)
        {
            foreach (var (_, read) in States(HostileNames).Concat(States()))
            {
                var (_, _, expanded) = Render(read, size);
                foreach (var (_, value) in CardTemplateHarness.Strings(expanded).Where(s => s.Key == "color"))
                {
                    Assert.Contains(value, allowed);
                    seen++;
                }
            }
        }

        Assert.True(seen > 0); // the walk is real
    }

    // =====================================================================================================
    // L detail (RC-10) and the Small fallback (P-RC-2 as amended by P-C1-3/4/6)
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
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    public void Small_has_no_fleet_bar_and_always_shows_the_freshness_line(int servers)
    {
        // Prism P-C1-3: fraction + title + subtitle + footer; no glyph bar pushing the footer out of 146 px.
        var (vm, _, expanded) = Render(Read(Now, Enumerable.Range(1, servers).Select(i => Server(i)).ToArray()), WidgetSizeHint.Small);
        var texts = CardTemplateHarness.VisibleTexts(expanded);

        Assert.DoesNotContain(texts, t => t.Contains('▰') || t.Contains('▱'));
        Assert.Contains($"{servers}/{servers}", texts);
        Assert.Contains(vm.FooterText, texts);
        Assert.DoesNotContain("fleetBar", WidgetCardRenderer.TemplateFor(WidgetSizeHint.Small), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stale_small_card_quotes_the_last_state_in_the_default_colour()
    {
        // Prism P-C1-4: "0/3" would read as "none healthy"; the stale card shows the last state, neutrally.
        var (vm, _, expanded) = Render(States().Single(s => s.State == "stale").Read, WidgetSizeHint.Small);
        Assert.Equal("3/3", vm.FractionText);
        var fraction = CardTemplateHarness.Objects(expanded).Select(o => o.Node).Single(o => (string?)o["text"] == "3/3");
        Assert.Equal("default", (string?)fraction["color"]);
        Assert.Contains(En.StaleTitle, CardTemplateHarness.VisibleTexts(expanded));
    }

    [Fact]
    public void The_small_empty_card_has_no_freshness_line()
    {
        // Prism P-C1-6 (Figma 112:11594): title + CTA only.
        foreach (var state in new[] { "empty", "empty-stale" })
        {
            var (vm, _, expanded) = Render(States().Single(s => s.State == state).Read, WidgetSizeHint.Small);
            Assert.NotEmpty(vm.FooterText);
            Assert.DoesNotContain(vm.FooterText, CardTemplateHarness.VisibleTexts(expanded));
        }
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
    // a11y (RC-8/R-4): titles are headings on M/L (C3 / Prism C0 §4: not on Small); state is always text
    // =====================================================================================================

    [Fact]
    public void Titles_use_the_heading_style_on_medium_and_large_only_and_every_coloured_text_carries_words()
    {
        foreach (var size in Sizes)
        {
            foreach (var (_, read) in States())
            {
                var (vm, _, expanded) = Render(read, size);
                var headings = CardTemplateHarness.Objects(expanded).Select(o => o.Node).Where(o => (string?)o["style"] == "heading").ToList();
                if (size == WidgetSizeHint.Small)
                {
                    // The Small title is Medium Bolder without the heading style: the host maps heading to
                    // Large, which the ~85 px Small body cannot afford.
                    Assert.Empty(headings);
                    var title = CardTemplateHarness.Objects(expanded).Select(o => o.Node)
                        .Single(o => (string?)o["type"] == "TextBlock" && (string?)o["text"] == vm.Title);
                    Assert.Equal("Medium", (string?)title["size"]);
                    Assert.Equal("Bolder", (string?)title["weight"]);
                }
                else if (vm.CardState is WidgetCardState.Empty or WidgetCardState.Unavailable)
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
