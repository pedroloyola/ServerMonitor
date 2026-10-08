using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 C3 — the V3 adjustments from the real-board C0 session (Prism C0 debrief §1–§5):
/// <list type="bullet">
/// <item>bar colours and their computed WCAG contrast;</item>
/// <item>the exact Small layout;</item>
/// <item>the empty-state icon on M/L only.</item>
/// </list>
/// Rows (M = 3) are covered in <c>WidgetViewModelBuilderTests</c>, bars in <c>WidgetCardRendererTests</c>.
/// </summary>
public sealed class WidgetBoardAdjustmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetStrings En = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo("en-US"));

    // =====================================================================================================
    // Bar colours (Prism §1): the constants decode to exactly Prism's table, and the contrast holds.
    // =====================================================================================================

    /// <summary>The single RGB pixel of a 1×1 bar constant, decoded from its IDAT.</summary>
    internal static string BarColour(string uri)
    {
        var chunks = WidgetImageScanTests.PngChunks(WidgetImageScanTests.Decode(uri));
        var ihdr = chunks.Single(c => c.Type == "IHDR").Data;
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0 }, ihdr); // 1×1, 8-bit RGB, no interlace

        using var zlib = new ZLibStream(new MemoryStream(chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray()), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var scanline = raw.ToArray();
        Assert.Equal(4, scanline.Length);
        Assert.Equal(0, scanline[0]); // filter None
        return $"#{scanline[1]:X2}{scanline[2]:X2}{scanline[3]:X2}";
    }

    public static TheoryData<string, string> PrismPalette() => new()
    {
        { WidgetImages.BarCpuDark, "#B69AF8" }, { WidgetImages.BarCpuLight, "#8668CA" },
        { WidgetImages.BarRamDark, "#7DB8FF" }, { WidgetImages.BarRamLight, "#427EC5" },
        { WidgetImages.BarDiskDark, "#FFC16E" }, { WidgetImages.BarDiskLight, "#A86A1F" },
        { WidgetImages.BarStaleDark, "#ADADAD" }, { WidgetImages.BarStaleLight, "#626262" },
        { WidgetImages.BarTrackDark, "#484848" }, { WidgetImages.BarTrackLight, "#E8E8E8" },
    };

    [Theory]
    [MemberData(nameof(PrismPalette))]
    public void Each_bar_constant_is_exactly_the_colour_in_Prisms_table(string uri, string colour) =>
        Assert.Equal(colour, BarColour(uri));

    private static double Luminance(string hex)
    {
        static double Channel(int v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var rgb = Convert.FromHexString(hex[1..]);
        return 0.2126 * Channel(rgb[0]) + 0.7152 * Channel(rgb[1]) + 0.0722 * Channel(rgb[2]);
    }

    internal static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Card surfaces sampled on the real board (Prism C0 §0): dark #323232 (wallpaper tint up to #353329),
    // light #FDFDFD (tints/hover #EFEEE8, #E8E8E8 = worst case).
    private static readonly string[] DarkCards = ["#323232", "#353329"];
    private static readonly string[] LightCards = ["#FDFDFD", "#EFEEE8", "#E8E8E8"];

    public static TheoryData<string, string, bool> Fills() => new()
    {
        { "CPU", WidgetImages.BarCpuDark, true }, { "RAM", WidgetImages.BarRamDark, true },
        { "Disk", WidgetImages.BarDiskDark, true }, { "Stale", WidgetImages.BarStaleDark, true },
        { "CPU", WidgetImages.BarCpuLight, false }, { "RAM", WidgetImages.BarRamLight, false },
        { "Disk", WidgetImages.BarDiskLight, false }, { "Stale", WidgetImages.BarStaleLight, false },
    };

    [Theory]
    [MemberData(nameof(Fills))]
    public void Every_bar_fill_reaches_3_to_1_against_every_card_surface_and_its_track(string role, string fill, bool dark)
    {
        // Prism's revised P-RC-1: fill↔card AND fill↔track ≥ 3:1 in both themes; the track itself is exempt
        // (a decorative scale — the % is always text next to it).
        var colour = BarColour(fill);
        var track = BarColour(dark ? WidgetImages.BarTrackDark : WidgetImages.BarTrackLight);
        foreach (var card in dark ? DarkCards : LightCards)
        {
            Assert.True(Contrast(colour, card) >= 3.0, $"{role} {colour} vs card {card}: {Contrast(colour, card):F2}");
        }

        Assert.True(Contrast(colour, track) >= 3.0, $"{role} {colour} vs track {track}: {Contrast(colour, track):F2}");
    }

    [Fact]
    public void The_contrast_formula_matches_the_wcag_reference_points()
    {
        Assert.Equal(21.0, Contrast("#000000", "#FFFFFF"), 2);
        Assert.Equal(1.0, Contrast("#777777", "#777777"), 2);
        // Prism's table, worst light case: Disk #A86A1F on #E8E8E8 = 3.61.
        Assert.Equal(3.61, Contrast("#A86A1F", "#E8E8E8"), 2);
        // The Figma disk colour Prism replaced fails on the tinted light card (2.90).
        Assert.True(Contrast("#B87B2F", "#E8E8E8") < 3.0);
    }

    // =====================================================================================================
    // Small (Prism §3/§4): fraction auto | [title Medium Bolder, no heading + subtitle] stretch; footer;
    // nothing wraps.
    // =====================================================================================================

    private static JsonObject Expand(WidgetReadResult read, WidgetSizeHint size, string theme = "dark", WidgetStrings? strings = null)
    {
        var card = WidgetCardRenderer.Render(WidgetViewModelBuilder.Build(read, size, Now, strings ?? En));
        return CardTemplateHarness.Expand(card.TemplateJson, card.DataJson, theme);
    }

    [Fact]
    public void The_small_fleet_card_has_the_exact_C0_layout_and_nothing_wraps()
    {
        var template = JsonNode.Parse(WidgetCardRenderer.TemplateFor(WidgetSizeHint.Small))!;
        var fleet = CardTemplateHarness.Objects(template).Select(o => o.Node).Single(o => (string?)o["$when"] == "${isFleet}");
        var items = fleet["items"]!.AsArray();
        Assert.Equal(2, items.Count);

        var columns = items[0]!["columns"]!.AsArray();
        Assert.Equal(2, columns.Count);
        Assert.Equal("auto", (string?)columns[0]!["width"]);
        var fraction = columns[0]!["items"]!.AsArray().Single()!;
        Assert.Equal(("${fraction}", "ExtraLarge", "Bolder"), ((string?)fraction["text"], (string?)fraction["size"], (string?)fraction["weight"]));

        Assert.Equal("stretch", (string?)columns[1]!["width"]);
        var stack = columns[1]!["items"]!.AsArray();
        var title = stack[0]!;
        Assert.Equal(("${title}", "Medium", "Bolder"), ((string?)title["text"], (string?)title["size"], (string?)title["weight"]));
        Assert.Null(title["style"]); // no heading on Small
        var subtitle = stack[1]!;
        Assert.Equal("${hasSubtitle}", (string?)subtitle["$when"]);
        Assert.Equal(("${subtitle}", "Small", true), ((string?)subtitle["text"], (string?)subtitle["size"], (bool?)subtitle["isSubtle"]));

        var footer = items[1]!;
        Assert.Equal(("${footer}", "Small", true, "Small"),
            ((string?)footer["text"], (string?)footer["size"], (bool?)footer["isSubtle"], (string?)footer["spacing"]));

        var texts = CardTemplateHarness.Objects(fleet).Select(o => o.Node).Where(o => (string?)o["type"] == "TextBlock").ToList();
        Assert.Equal(4, texts.Count);
        Assert.All(texts, t => Assert.False((bool)t["wrap"]!));
    }

    [Fact]
    public void No_small_card_in_any_state_carries_the_heading_style_or_an_image()
    {
        foreach (var (state, read) in WidgetCardRendererTests.States())
        {
            foreach (var theme in new[] { "dark", "light" })
            {
                var expanded = Expand(read, WidgetSizeHint.Small, theme);
                Assert.DoesNotContain(CardTemplateHarness.Strings(expanded), s => s is { Key: "style", Value: "heading" });
                Assert.DoesNotContain(CardTemplateHarness.Objects(expanded), o => (string?)o.Node["type"] == "Image");
                Assert.DoesNotContain(CardTemplateHarness.Strings(expanded), s => s.Key == "url");
            }
        }
    }

    // =====================================================================================================
    // Empty icon (Prism §5): ServerStack01 40×40, theme variant, M/L only, altText = the empty title.
    // =====================================================================================================

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void The_empty_card_shows_the_server_stack_icon_on_medium_and_large_with_the_title_as_alt_text(string culture)
    {
        var strings = WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture));
        var empty = WidgetCardRendererTests.States().Single(s => s.State == "empty").Read;
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            foreach (var (theme, url) in new[] { ("dark", WidgetImages.EmptyIconDark), ("light", WidgetImages.EmptyIconLight) })
            {
                var expanded = Expand(empty, size, theme, strings);
                var image = CardTemplateHarness.Objects(expanded).Select(o => o.Node).Single(o => (string?)o["type"] == "Image");

                Assert.Equal(url, (string?)image["url"]);
                Assert.Equal(("40px", "40px", "Center"), ((string?)image["width"], (string?)image["height"], (string?)image["horizontalAlignment"]));
                Assert.Equal(strings.EmptyTitle, (string?)image["altText"]); // no new copy: the empty title
                Assert.Null(image["selectAction"]); // decorative: the CTA stays the only action target
            }
        }
    }

    [Fact]
    public void The_icon_appears_only_on_the_empty_state()
    {
        foreach (var size in new[] { WidgetSizeHint.Medium, WidgetSizeHint.Large })
        {
            foreach (var (state, read) in WidgetCardRendererTests.States())
            {
                var images = CardTemplateHarness.Objects(Expand(read, size)).Count(o => (string?)o.Node["type"] == "Image");
                Assert.Equal(state is "empty" or "empty-stale" ? 1 : 0, images);
            }
        }
    }

    [Fact]
    public void The_icon_constants_are_80px_rgba_for_a_crisp_40px_display()
    {
        foreach (var uri in new[] { WidgetImages.EmptyIconDark, WidgetImages.EmptyIconLight })
        {
            var ihdr = WidgetImageScanTests.PngChunks(WidgetImageScanTests.Decode(uri)).Single(c => c.Type == "IHDR").Data;
            Assert.Equal(new byte[] { 0, 0, 0, 80, 0, 0, 0, 80, 8, 6, 0, 0, 0 }, ihdr);
        }
    }
}
