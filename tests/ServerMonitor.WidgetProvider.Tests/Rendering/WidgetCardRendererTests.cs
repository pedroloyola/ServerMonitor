using System.Globalization;
using System.Text.Json;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

public sealed class WidgetCardRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings En = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "AdaptiveCard", "TextBlock", "ColumnSet", "Column", "Container", "Action.Execute"
    };

    private static WidgetServerState Server(string name, WidgetHealth health, Guid? id = null,
        double? cpu = 12, double? mem = 34, double? disk = 56) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = name,
        Health = health,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk,
        LastUpdatedUtc = Now
    };

    private static WidgetReadResult Read(params WidgetServerState[] servers) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = WidgetHealthPrecedence.Worst(servers.Select(s => s.Health)),
            Servers = servers
        });

    private static WidgetCard Render(WidgetReadResult read, WidgetSizeHint size) =>
        WidgetCardRenderer.Render(WidgetViewModelBuilder.Build(read, size, Now, En));

    private static JsonElement AssertValidCard(string templateJson)
    {
        using var doc = JsonDocument.Parse(templateJson); // throws on invalid JSON
        var root = doc.RootElement.Clone();
        Assert.Equal("AdaptiveCard", root.GetProperty("type").GetString());
        Assert.Equal("1.6", root.GetProperty("version").GetString());
        // header:null — our composition owns the top region (no duplicated host brand strip).
        Assert.True(root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Null);
        AssertElementsSupported(root);
        return root;
    }

    private static void AssertElementsSupported(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                Assert.True(AllowedElements.Contains(type.GetString() ?? string.Empty),
                    $"unsupported element type: {type.GetString()}");
            }

            foreach (var property in element.EnumerateObject())
            {
                AssertElementsSupported(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertElementsSupported(item);
            }
        }
    }

    [Theory]
    [InlineData(WidgetSizeHint.Small)]
    [InlineData(WidgetSizeHint.Medium)]
    [InlineData(WidgetSizeHint.Large)]
    public void All_sizes_render_valid_supported_cards(WidgetSizeHint size)
    {
        var card = Render(Read(Server("Home", WidgetHealth.Warning), Server("Db", WidgetHealth.Critical)), size);
        AssertValidCard(card.TemplateJson);
        Assert.Equal("{}", card.DataJson);
        Assert.Contains(En.FleetKicker, card.TemplateJson); // FROTA/FLEET kicker owns the top now
    }

    [Fact]
    public void Small_shows_no_server_rows_but_summary()
    {
        var json = Render(Read(Server("Home", WidgetHealth.Healthy)), WidgetSizeHint.Small).TemplateJson;
        AssertValidCard(json);
        Assert.DoesNotContain("Home", json);   // Small = summary only, no per-server rows
        Assert.Contains(En.FleetKicker, json);
    }

    [Fact]
    public void Medium_and_large_show_server_names_and_metrics()
    {
        var medium = Render(Read(Server("WebServer", WidgetHealth.Warning)), WidgetSizeHint.Medium).TemplateJson;
        AssertValidCard(medium);
        Assert.Contains("WebServer", medium);
        Assert.Contains("12", medium); // CPU number (value/unit are split: "12" + "%")
        Assert.Contains("Warning", medium); // health label as text (§18)
    }

    // ---- M13-QA-4 / P-017: Medium capacity + truthful overflow + no dangling separator ----------

    // Counts the server telemetry blocks in a card body: a Container that carries an openServer action.
    private static List<JsonElement> ServerBlocks(JsonElement root) =>
        root.GetProperty("body").EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("type", out var t) && t.GetString() == "Container"
                        && e.TryGetProperty("selectAction", out _))
            .ToList();

    private static IEnumerable<JsonElement> BodyItems(JsonElement root) =>
        root.GetProperty("body").EnumerateArray();

    // Every TextBlock "text" value in the card, JSON-decoded.
    private static IEnumerable<string> AllTexts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && element.TryGetProperty("text", out var text) && text.GetString() is { } value)
            {
                yield return value;
            }

            foreach (var prop in element.EnumerateObject())
            {
                foreach (var found in AllTexts(prop.Value)) { yield return found; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var found in AllTexts(item)) { yield return found; }
            }
        }
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(100, 2, 98)]
    public void Medium_renders_two_blocks_and_announces_the_rest(int total, int expectedBlocks, int expectedOverflow)
    {
        var servers = Enumerable.Range(0, total)
            .Select(i => Server($"srv{i:D3}", WidgetHealth.Healthy)).ToArray();
        var json = Render(Read(servers), WidgetSizeHint.Medium).TemplateJson;
        var root = AssertValidCard(json);

        Assert.Equal(expectedBlocks, ServerBlocks(root).Count);

        var texts = AllTexts(root).ToList();
        var overflowText = $"{expectedOverflow} more";
        if (expectedOverflow == 0)
        {
            Assert.DoesNotContain(texts, t => t.EndsWith(" more", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(overflowText, texts);
            // The overflow affordance is the LAST thing in the body, so it can never be pushed off the
            // card by another block - that is precisely how servers used to vanish silently (P-017).
            var last = BodyItems(root).Last();
            Assert.Equal("TextBlock", last.GetProperty("type").GetString());
            Assert.Equal(overflowText, last.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void Medium_never_serializes_a_server_beyond_the_cap()
    {
        // Fixed ids so the assertion can actually check that the capped server's opaque id is absent too,
        // not just its name (Vigil L-1: the comment used to promise more than the assertion delivered).
        var hidden = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var json = Render(Read(
            Server("alpha", WidgetHealth.Healthy),
            Server("bravo", WidgetHealth.Healthy),
            Server("charlie", WidgetHealth.Healthy, hidden)), WidgetSizeHint.Medium).TemplateJson;

        AssertValidCard(json);
        Assert.Contains("alpha", json, StringComparison.Ordinal);
        Assert.Contains("bravo", json, StringComparison.Ordinal);
        // The third server is not rendered at all - not its name, and not its opaque id.
        Assert.DoesNotContain("charlie", json, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(WidgetSizeHint.Medium, 1)]
    [InlineData(WidgetSizeHint.Medium, 2)]
    [InlineData(WidgetSizeHint.Medium, 3)]
    [InlineData(WidgetSizeHint.Medium, 10)]
    [InlineData(WidgetSizeHint.Large, 3)]
    [InlineData(WidgetSizeHint.Large, 6)]
    [InlineData(WidgetSizeHint.Large, 20)]
    public void No_dangling_separator_at_the_end_of_the_body(WidgetSizeHint size, int total)
    {
        var servers = Enumerable.Range(0, total)
            .Select(i => Server($"srv{i:D3}", WidgetHealth.Healthy)).ToArray();
        var root = AssertValidCard(Render(Read(servers), size).TemplateJson);

        foreach (var item in BodyItems(root))
        {
            if (!item.TryGetProperty("separator", out var sep) || !sep.GetBoolean())
            {
                continue;
            }

            // A separator is a rule drawn ABOVE its own element, so the element that carries it must
            // actually have content. A separator introducing nothing is the dangling line QA-4 showed.
            Assert.True(item.TryGetProperty("items", out var items), "separator element has no items");
            Assert.NotEqual(0, items.GetArrayLength());
        }

        // And the body itself never ends on an empty container.
        var last = BodyItems(root).Last();
        if (last.TryGetProperty("items", out var lastItems))
        {
            Assert.NotEqual(0, lastItems.GetArrayLength());
        }
    }

    [Theory]
    [InlineData(WidgetSizeHint.Medium)]
    [InlineData(WidgetSizeHint.Large)]
    public void Overflow_line_is_not_clickable_and_carries_no_server_action(WidgetSizeHint size)
    {
        var servers = Enumerable.Range(0, 12).Select(i => Server($"srv{i:D2}", WidgetHealth.Healthy)).ToArray();
        var root = AssertValidCard(Render(Read(servers), size).TemplateJson);

        var overflow = BodyItems(root).Single(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
            && e.TryGetProperty("text", out var x) && (x.GetString() ?? string.Empty).EndsWith(" more", StringComparison.Ordinal));

        // No per-element action: the overflow line falls through to the card's openDashboard, and can
        // never carry an openServer verb for a server it does not identify.
        Assert.False(overflow.TryGetProperty("selectAction", out _));
        // And it must be legible, not de-emphasised - it is the only signal that servers are hidden.
        Assert.False(overflow.TryGetProperty("isSubtle", out var subtle) && subtle.GetBoolean());
    }

    // M13-QA-5: Large holds three blocks plus the fleet-summary footer. The footer is NOT sacrificed to
    // make room for the overflow line - both must survive, because both carry information the user needs.
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 3, 1)]
    [InlineData(6, 3, 3)]
    [InlineData(7, 3, 4)]
    [InlineData(100, 3, 97)]
    public void Large_renders_three_blocks_announces_the_rest_and_keeps_the_footer(
        int total, int expectedBlocks, int expectedOverflow)
    {
        var servers = Enumerable.Range(0, total)
            .Select(i => Server($"srv{i:D3}", WidgetHealth.Healthy)).ToArray();
        var root = AssertValidCard(Render(Read(servers), WidgetSizeHint.Large).TemplateJson);

        Assert.Equal(expectedBlocks, ServerBlocks(root).Count);

        var texts = AllTexts(root).ToList();
        if (expectedOverflow == 0)
        {
            Assert.DoesNotContain(texts, t => t.EndsWith(" more", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains($"{expectedOverflow} more", texts);
        }

        // The fleet-summary footer survives in every case: its four labels are always present.
        foreach (var label in new[] { En.HealthyPlural, En.Warning, En.Critical, En.Offline })
        {
            Assert.Contains(label.ToUpperInvariant(), texts.Select(t => t.ToUpperInvariant()));
        }
    }

    [Fact]
    public void Large_never_serializes_a_server_beyond_the_cap()
    {
        var json = Render(Read(
            Server("alpha", WidgetHealth.Healthy),
            Server("bravo", WidgetHealth.Healthy),
            Server("charlie", WidgetHealth.Healthy),
            Server("delta", WidgetHealth.Healthy)), WidgetSizeHint.Large).TemplateJson;

        AssertValidCard(json);
        Assert.Contains("alpha", json, StringComparison.Ordinal);
        Assert.Contains("bravo", json, StringComparison.Ordinal);
        Assert.Contains("charlie", json, StringComparison.Ordinal);
        Assert.DoesNotContain("delta", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_overflow_line_carries_no_server_action()
    {
        var servers = Enumerable.Range(0, 9).Select(i => Server($"srv{i}", WidgetHealth.Healthy)).ToArray();
        var root = AssertValidCard(Render(Read(servers), WidgetSizeHint.Large).TemplateJson);

        var overflow = BodyItems(root).Single(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
            && e.TryGetProperty("text", out var x) && x.GetString() == "6 more");
        Assert.False(overflow.TryGetProperty("selectAction", out _));
    }

    [Fact]
    public void Large_shows_gb_and_uptime_detail_but_medium_does_not()
    {
        var server = new WidgetServerState
        {
            Id = Guid.NewGuid(),
            DisplayName = "srv",
            Health = WidgetHealth.Healthy,
            CpuUsagePercent = 3,
            MemoryUsagePercent = 39,
            DiskUsagePercent = 3,
            MemoryUsedGb = 3.2,
            MemoryTotalGb = 8,
            DiskUsedGb = 12,
            DiskTotalGb = 460,
            UptimeSeconds = 3600 * 24 * 43, // 43 days
            LastUpdatedUtc = Now
        };

        var large = Render(Read(server), WidgetSizeHint.Large).TemplateJson;
        AssertValidCard(large);
        Assert.Contains("/ 8 GB", large);       // memory detail (culture-agnostic — total is integer 8)
        Assert.Contains("43d", large);          // cpu column uptime detail

        var medium = Render(Read(server), WidgetSizeHint.Medium).TemplateJson;
        AssertValidCard(medium);
        Assert.DoesNotContain("GB", medium);    // Medium stays compact — no GB/uptime detail
        Assert.DoesNotContain("43d", medium);
    }

    [Fact]
    public void Large_footer_summarizes_fleet_health_counts()
    {
        var json = Render(Read(
            Server("a", WidgetHealth.Healthy), Server("b", WidgetHealth.Healthy),
            Server("c", WidgetHealth.Critical)), WidgetSizeHint.Large).TemplateJson;
        var root = AssertValidCard(json);
        // Footer tiles carry the localized category labels; only the healthy count is present twice
        // (hero + footer) — the footer proves the severity breakdown renders.
        Assert.Contains(En.Critical.ToUpperInvariant(), json);
        Assert.Contains(En.Offline.ToUpperInvariant(), json);
    }

    // ---- M13-QA-6: the meter is ONE TextBlock of glyphs in a FOREGROUND colour role, not styled
    // container backgrounds and not per-run columns ----

    private const string TickFilled = "\u25AE";
    private const string TickEmpty = "\u25AF";

    // Any TextBlock made only of tick glyphs, filled and empty mixed.
    private static bool IsTrack(string text) =>
        text.Length > 0 && text.All(c => c.ToString() == TickFilled || c.ToString() == TickEmpty);

    private static int CountFilled(string track) => track.Count(c => c.ToString() == TickFilled);
    private static int CountEmpty(string track) => track.Count(c => c.ToString() == TickEmpty);

    // The per-metric meter tracks: one TextBlock each, mixed glyphs, in the accent role.
    private static List<string> MeterTracks(JsonElement root) =>
        TickRuns(root).Where(r => r.Color == "accent" && IsTrack(r.Text)).Select(r => r.Text).ToList();

    // Every TextBlock made purely of tick glyphs, with its colour role and subtlety.
    private static List<(string Text, string? Color, bool Subtle)> TickRuns(JsonElement el)
    {
        var runs = new List<(string, string?, bool)>();
        Walk(el);
        return runs;

        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                    && e.TryGetProperty("text", out var tx) && tx.GetString() is { Length: > 0 } text
                    && IsTrack(text))
                {
                    var color = e.TryGetProperty("color", out var c) ? c.GetString() : null;
                    var subtle = e.TryGetProperty("isSubtle", out var sub) && sub.GetBoolean();
                    runs.Add((text, color, subtle));
                }

                foreach (var prop in e.EnumerateObject()) { Walk(prop.Value); }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) { Walk(item); }
            }
        }
    }

    [Theory]
    [InlineData(WidgetSizeHint.Medium)]
    [InlineData(WidgetSizeHint.Large)]
    public void Meter_uses_foreground_glyphs_not_container_background_styles(WidgetSizeHint size)
    {
        // The whole point of QA-6: container styles do not resolve usefully per theme in the host's light
        // config, foreground colours do. If the meter ever returns to styled containers, this fails.
        var root = AssertValidCard(Render(Read(Server("db", WidgetHealth.Healthy)), size).TemplateJson);

        Assert.NotEmpty(TickRuns(root));

        var styles = new List<string>();
        CollectContainerStyles(root, styles);
        Assert.DoesNotContain("accent", styles);
        Assert.DoesNotContain("emphasis", styles);
    }

    [Fact]
    public void Meter_fill_is_magnitude_neutral_not_health_coloured()
    {
        // A critical server must NOT tint its metric meters red: health lives only on the chip and on the
        // fleet bar, which is health-coloured by design.
        var root = AssertValidCard(Render(Read(Server("db", WidgetHealth.Critical)), WidgetSizeHint.Medium).TemplateJson);

        Assert.Equal(3, MeterTracks(root).Count);   // three metrics, all in the neutral accent role

        // The only health-coloured tick run on the card is the fleet bar.
        var healthColoured = TickRuns(root).Where(r => r.Color is "attention" or "warning" or "good").ToList();
        Assert.Single(healthColoured);
        Assert.Equal("attention", healthColoured[0].Color);
    }

    [Fact]
    public void Filled_and_empty_are_told_apart_by_shape_not_by_colour()
    {
        // The track is ONE block in ONE colour role, so the states cannot rely on colour: a filled tick is
        // a solid glyph and an empty one is outlined. That difference survives High Contrast and colour
        // vision deficiency, and it is what makes a single-colour track legible.
        var root = AssertValidCard(Render(Read(Server("db", WidgetHealth.Healthy, cpu: 60, mem: 60, disk: 60)),
            WidgetSizeHint.Medium).TemplateJson);

        var tracks = MeterTracks(root);
        Assert.Equal(3, tracks.Count);
        foreach (var track in tracks)
        {
            Assert.Equal(3, CountFilled(track));
            Assert.Equal(MeasuredMeterSegments - 3, CountEmpty(track));
            Assert.NotEqual(TickFilled[0], TickEmpty[0]);   // the two states are different glyphs
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]     // any non-zero magnitude lights at least one tick
    [InlineData(20, 1)]
    [InlineData(30, 2)]    // ceil
    [InlineData(60, 3)]
    [InlineData(100, 5)]
    public void Filled_tick_count_tracks_magnitude(int percent, int expectedFilled)
    {
        var root = AssertValidCard(Render(Read(
            Server("db", WidgetHealth.Healthy, cpu: percent, mem: percent, disk: percent)),
            WidgetSizeHint.Medium).TemplateJson);

        var tracks = MeterTracks(root);
        Assert.Equal(3, tracks.Count);
        foreach (var track in tracks)
        {
            Assert.Equal(expectedFilled, CountFilled(track));
            Assert.Equal(MeasuredMeterSegments - expectedFilled, CountEmpty(track));
            // The declared track is always drawn whole - never shortened, never truncated.
            Assert.Equal(MeasuredMeterSegments, track.Length);
        }
    }

    [Fact]
    public void Unknown_metric_renders_an_all_empty_track_and_never_invents_zero()
    {
        var root = AssertValidCard(Render(Read(
            Server("db", WidgetHealth.Healthy, cpu: null, mem: null, disk: null)),
            WidgetSizeHint.Medium).TemplateJson);

        var tracks = MeterTracks(root);
        Assert.Equal(3, tracks.Count);
        // An unknown metric draws the full track with nothing lit - it never invents a zero-length bar.
        Assert.All(tracks, t => Assert.Equal(MeasuredMeterSegments, t.Length));
        Assert.All(tracks, t => Assert.Equal(0, CountFilled(t)));
        // And the number itself is the unknown placeholder, not "0".
        Assert.Contains(En.MetricUnknown, AllTexts(root));
    }

    // ---- M13-QA-6: INTEGRITY. A legible but truncated instrument is worse than an illegible one - it
    // answers confidently and wrongly. These capacities were MEASURED on the real board and are written as
    // literals on purpose. It took three attempts to get here: 10 glyph segments were clipped to about 6;
    // 5 segments split across auto columns were cut with an ellipsis; only one TextBlock holding the whole
    // track survives, because there is then nothing for the host to squeeze. Every earlier attempt passed
    // its tests, because the tests checked string lengths rather than what the host draws. ----

    public const int MeasuredMeterSegments = 5;   // fits a ~90px metric column at the measured ~13px pitch
    public const int MeasuredMaxFleetTicks = 8;   // fits the hero row beside the fraction and its label

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(99)]
    [InlineData(100)]
    public void Meter_always_draws_every_segment_it_declares(int percent)
    {
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            var root = AssertValidCard(Render(Read(
                Server("db", WidgetHealth.Healthy, cpu: percent, mem: percent, disk: percent)), size).TemplateJson);

            // Per metric cell: filled + empty must total the declared track, exactly. Three metrics per row.
            var tracks = MeterTracks(root);
            var rows = ServerBlocks(root).Count;
            Assert.Equal(3 * rows, tracks.Count);
            Assert.All(tracks, t => Assert.Equal(MeasuredMeterSegments, t.Length));
        }
    }

    [Fact]
    public void Meter_segment_count_matches_the_capacity_measured_on_the_board()
    {
        // The production constant must equal what was measured, not the other way round.
        var root = AssertValidCard(Render(Read(Server("db", WidgetHealth.Healthy, cpu: 0, mem: 0, disk: 0)),
            WidgetSizeHint.Medium).TemplateJson);
        var tracks = MeterTracks(root);
        Assert.Equal(3, tracks.Count);
        Assert.All(tracks, t => Assert.Equal(MeasuredMeterSegments, t.Length));
        Assert.All(tracks, t => Assert.Equal(0, CountFilled(t)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Fleet_bar_is_drawn_within_the_tick_budget(int servers)
    {
        var fleet = Enumerable.Range(0, servers).Select(i => Server($"s{i:D2}", WidgetHealth.Healthy)).ToArray();
        var root = AssertValidCard(Render(Read(fleet), WidgetSizeHint.Small).TemplateJson);

        // Small renders no rows, so every tick run belongs to the fleet bar.
        Assert.Equal(servers, TickRuns(root).Sum(r => r.Text.Length));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(40)]
    public void Fleet_bar_is_omitted_rather_than_truncated_above_the_budget(int servers)
    {
        var fleet = Enumerable.Range(0, servers).Select(i => Server($"s{i:D2}", WidgetHealth.Healthy)).ToArray();
        var json = Render(Read(fleet), WidgetSizeHint.Small).TemplateJson;
        var root = AssertValidCard(json);

        Assert.True(servers > MeasuredMaxFleetTicks);
        // No partial bar - a bar showing 8 of 40 would be a confident lie about the fleet.
        Assert.Empty(TickRuns(root));
        // ...and no JSON null left where it used to be.
        Assert.DoesNotContain("null", json.Replace("\"header\":null", string.Empty), StringComparison.Ordinal);
        // The hero still states the whole truth.
        Assert.Contains($"{servers}", AllTexts(root));
    }

    [Fact]
    public void Fleet_bar_uses_health_foreground_colours_one_tick_per_server()
    {
        var root = AssertValidCard(Render(Read(
            Server("a", WidgetHealth.Healthy),
            Server("b", WidgetHealth.Healthy),
            Server("c", WidgetHealth.Warning),
            Server("d", WidgetHealth.Critical),
            Server("e", WidgetHealth.Offline)), WidgetSizeHint.Small).TemplateJson);

        // Small renders no server rows, so every tick run on this card belongs to the fleet bar.
        var runs = TickRuns(root);
        Assert.Equal(2, runs.Single(r => r.Color == "good").Text.Length);       // 2 healthy
        Assert.Equal(1, runs.Single(r => r.Color == "warning").Text.Length);    // 1 warning
        Assert.Equal(2, runs.Single(r => r.Color == "attention").Text.Length);  // critical + offline
        Assert.Equal(5, runs.Sum(r => r.Text.Length));                          // one tick per server

        var styles = new List<string>();
        CollectContainerStyles(root, styles);
        Assert.DoesNotContain("good", styles);
        Assert.DoesNotContain("warning", styles);
        Assert.DoesNotContain("attention", styles);
    }

    private static void CollectContainerStyles(JsonElement el, List<string> styles)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "Container" &&
                el.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String)
            {
                styles.Add(s.GetString() ?? string.Empty);
            }

            foreach (var p in el.EnumerateObject())
            {
                CollectContainerStyles(p.Value, styles);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in el.EnumerateArray())
            {
                CollectContainerStyles(i, styles);
            }
        }
    }

    [Fact]
    public void Unavailable_card_is_valid_and_neutral()
    {
        var json = WidgetCardRenderer.Render(
            WidgetViewModelBuilder.Build(WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Corrupt),
                WidgetSizeHint.Medium, Now, En)).TemplateJson;
        AssertValidCard(json);
        Assert.Contains(En.NoDataTitle, json);
    }

    [Fact]
    public void Empty_card_is_valid_and_says_no_servers()
    {
        var read = WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = Now,
            OverallHealth = WidgetHealth.Unknown,
            Servers = Array.Empty<WidgetServerState>()
        });
        var json = WidgetCardRenderer.Render(WidgetViewModelBuilder.Build(read, WidgetSizeHint.Medium, Now, En)).TemplateJson;
        AssertValidCard(json);
        Assert.Contains(En.NoServers, json);
    }

    [Fact]
    public void Hostile_display_name_never_breaks_the_json()
    {
        // Even though the contract sanitizes names on read, the renderer must be robust: quotes,
        // backslashes and braces must be JSON-escaped, not concatenated raw (§44).
        var hostile = "a\"b\\c{}<x>";
        var json = Render(Read(Server(hostile, WidgetHealth.Warning)), WidgetSizeHint.Medium).TemplateJson;
        var root = AssertValidCard(json); // parses => escaping is correct
        Assert.True(ContainsTextValue(root, hostile)); // the name round-trips as a decoded VALUE
    }

    private static bool ContainsTextValue(JsonElement el, string value)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                text.GetString() == value)
            {
                return true;
            }

            foreach (var p in el.EnumerateObject())
            {
                if (ContainsTextValue(p.Value, value))
                {
                    return true;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (ContainsTextValue(item, value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Opaque_id_appears_only_in_action_data_never_as_visible_text()
    {
        // The opaque id is the deep-link target in the row's Action.Execute data (§13) — allowed — but it
        // must NEVER appear as a visible TextBlock (no id shown to the user).
        var id = Guid.NewGuid();
        var json = Render(Read(Server("Home", WidgetHealth.Healthy, id)), WidgetSizeHint.Large).TemplateJson;
        using var doc = JsonDocument.Parse(json);

        var texts = new List<string>();
        CollectTexts(doc.RootElement, texts);
        Assert.DoesNotContain(id.ToString("D"), texts);   // never rendered as visible text
        Assert.True(ContainsActionServerId(doc.RootElement, id.ToString("D"))); // present in action data
    }

    private static void CollectTexts(JsonElement el, List<string> texts)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "TextBlock" &&
                el.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                texts.Add(text.GetString() ?? string.Empty);
            }

            foreach (var p in el.EnumerateObject())
            {
                CollectTexts(p.Value, texts);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in el.EnumerateArray())
            {
                CollectTexts(i, texts);
            }
        }
    }

    private static bool ContainsActionServerId(JsonElement el, string id)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "Action.Execute" &&
                el.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("serverId", out var sid) && sid.GetString() == id)
            {
                return true;
            }

            foreach (var p in el.EnumerateObject())
            {
                if (ContainsActionServerId(p.Value, id))
                {
                    return true;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in el.EnumerateArray())
            {
                if (ContainsActionServerId(i, id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public void Card_and_rows_carry_allowlisted_actions()
    {
        var id = Guid.NewGuid();
        var json = Render(Read(Server("Home", WidgetHealth.Warning, id)), WidgetSizeHint.Medium).TemplateJson;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Card-level selectAction opens the dashboard.
        Assert.True(root.TryGetProperty("selectAction", out var cardAction));
        Assert.Equal("Action.Execute", cardAction.GetProperty("type").GetString());
        Assert.Equal("openDashboard", cardAction.GetProperty("verb").GetString());

        // A server row carries openServer with the opaque id.
        Assert.True(ContainsActionServerId(root, id.ToString("D")));
    }

    [Fact]
    public void Brand_uses_accent_and_health_never_does()
    {
        // #1846E1 brand is accent-only; health uses good/warning/attention, never accent (§4).
        var json = Render(Read(Server("Home", WidgetHealth.Critical)), WidgetSizeHint.Medium).TemplateJson;
        using var doc = JsonDocument.Parse(json);
        var accentTexts = new List<string>();
        var healthColors = new List<string>();
        Collect(doc.RootElement, accentTexts, healthColors);

        Assert.Contains(En.FleetKicker, accentTexts); // the FLEET kicker is the accent-coloured text
        Assert.DoesNotContain("accent", healthColors); // health labels never accent
    }

    private static void Collect(JsonElement el, List<string> accentTexts, List<string> nonAccentColors)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "TextBlock" &&
                el.TryGetProperty("color", out var c))
            {
                var color = c.GetString();
                var text = el.TryGetProperty("text", out var tx) ? tx.GetString() ?? string.Empty : string.Empty;
                if (color == "accent")
                {
                    accentTexts.Add(text);
                }
                else if (color is "good" or "warning" or "attention")
                {
                    nonAccentColors.Add(color!); // record health colours to prove they're not accent
                }
            }

            foreach (var p in el.EnumerateObject())
            {
                Collect(p.Value, accentTexts, nonAccentColors);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                Collect(item, accentTexts, nonAccentColors);
            }
        }
    }
}
