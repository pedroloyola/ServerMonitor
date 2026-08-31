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

    private static WidgetServerState Server(string name, WidgetHealth health, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = name,
        Health = health,
        CpuUsagePercent = 12,
        MemoryUsagePercent = 34,
        DiskUsagePercent = 56,
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

    [Fact]
    public void Meter_fill_is_magnitude_neutral_accent_not_health_coloured()
    {
        // The meter fill is a magnitude-neutral "accent" Container style regardless of health; health lives
        // only on the "● Health" chip. A critical server must NOT tint its meters red (Prism M2).
        // For a Critical server the meter fill must still be the magnitude-neutral "accent" (the fleet bar
        // legitimately uses health colours, but the per-metric meters must not). The healthy server below
        // has NO health-coloured containers at all, so its meter fill is unambiguously accent.
        var critical = Render(Read(Server("db", WidgetHealth.Critical)), WidgetSizeHint.Medium).TemplateJson;
        using (var doc = JsonDocument.Parse(critical))
        {
            var styles = new List<string>();
            CollectContainerStyles(doc.RootElement, styles);
            Assert.Contains("accent", styles); // meters still use accent even for a critical server
        }

        var healthy = Render(Read(Server("ok", WidgetHealth.Healthy)), WidgetSizeHint.Medium).TemplateJson;
        using (var doc = JsonDocument.Parse(healthy))
        {
            var styles = new List<string>();
            CollectContainerStyles(doc.RootElement, styles);
            // A healthy fleet's only "coloured" container is the good fleet tick; meters are accent/emphasis.
            Assert.DoesNotContain("attention", styles);
            Assert.DoesNotContain("warning", styles);
            Assert.Contains("accent", styles);
        }
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
