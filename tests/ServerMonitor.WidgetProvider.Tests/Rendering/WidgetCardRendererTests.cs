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
        "AdaptiveCard", "TextBlock", "ColumnSet", "Column", "Container"
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
        Assert.Equal("1.5", root.GetProperty("version").GetString());
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
        Assert.Contains("ServerAlyzer", card.TemplateJson);
    }

    [Fact]
    public void Small_shows_no_server_rows_but_summary()
    {
        var json = Render(Read(Server("Home", WidgetHealth.Healthy)), WidgetSizeHint.Small).TemplateJson;
        AssertValidCard(json);
        Assert.DoesNotContain("Home", json);   // Small = summary only, no per-server rows
        Assert.Contains("ServerAlyzer", json);
    }

    [Fact]
    public void Medium_and_large_show_server_names_and_metrics()
    {
        var medium = Render(Read(Server("WebServer", WidgetHealth.Warning)), WidgetSizeHint.Medium).TemplateJson;
        AssertValidCard(medium);
        Assert.Contains("WebServer", medium);
        Assert.Contains("12%", medium); // CPU
        Assert.Contains("Warning", medium); // health label as text (§18)
    }

    [Fact]
    public void Large_caps_rows_and_shows_more()
    {
        var servers = Enumerable.Range(0, 20).Select(i => Server($"srv{i}", WidgetHealth.Healthy)).ToArray();
        var json = Render(Read(servers), WidgetSizeHint.Large).TemplateJson;
        AssertValidCard(json);
        // 6 rows rendered, "14 more" affordance.
        Assert.Contains("14", json);
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
    public void Card_does_not_leak_the_opaque_server_id()
    {
        var id = Guid.NewGuid();
        var json = Render(Read(Server("Home", WidgetHealth.Healthy, id)), WidgetSizeHint.Large).TemplateJson;
        Assert.DoesNotContain(id.ToString(), json);
        Assert.DoesNotContain(id.ToString("N"), json);
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

        Assert.Contains("ServerAlyzer", accentTexts); // brand is the accent-coloured text
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
