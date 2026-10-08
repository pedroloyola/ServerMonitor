using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Rendering;
using ServerMonitor.WidgetProvider.Tests.Architecture;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

/// <summary>
/// UI.9 C3 — the image rule (Vigil C0 debrief §2/§3, which replaces V-RC-9). The board renders only constant
/// <c>data:</c> URIs, so:
/// <list type="bullet">
/// <item>every image is a compile-time constant in <see cref="WidgetImages"/>;</item>
/// <item>templates hold those constants literally, selected by <c>$when</c>, never bound;</item>
/// <item>the data JSON never carries a URI;</item>
/// <item>the PNG bytes carry no metadata and stay small; SVG (none today, C3 DV-1) must pass a strict
/// allowlist.</item>
/// </list>
/// </summary>
public sealed class WidgetImageScanTests
{
    private const string PngPrefix = "data:image/png;base64,";
    private const string SvgPrefix = "data:image/svg+xml;base64,";
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly WidgetSizeHint[] Sizes = [WidgetSizeHint.Small, WidgetSizeHint.Medium, WidgetSizeHint.Large];
    private static readonly string[] Cultures = ["en-US", "pt-PT", "pt-BR"];
    private static readonly string[] Themes = ["dark", "light"];
    private static readonly HashSet<string> Allowed = new(WidgetImages.All, StringComparer.Ordinal);

    // Outside the constants: any scheme, UNC path or protocol-relative reference fails.
    private static readonly Regex UrlLike = new(@"(?i)(https?:|file:|ms-appx:|ms-appdata:|\\\\|//)");

    private static readonly string[] ForbiddenActions =
        ["Action.OpenUrl", "Action.Submit", "Action.ShowCard", "Action.ToggleVisibility"];

    // ---- the constant set ------------------------------------------------------------------------------

    [Fact]
    public void Every_image_is_a_compile_time_constant_listed_in_All_and_nothing_else_is()
    {
        // The only non-const field is the compiler's backing field of All, which lists the constants (below).
        var fields = typeof(WidgetImages).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.Name != "<All>k__BackingField").ToArray();
        var constants = fields.Where(f => f.IsLiteral && f.FieldType == typeof(string)).ToList();

        // Every field is a literal const string: no static readonly, nothing computed at type init.
        Assert.Equal(fields.Length, constants.Count);
        Assert.Equal(12, constants.Count); // 10 bar fills/tracks + 2 empty-icon variants
        var values = constants.Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(values.Order(StringComparer.Ordinal), WidgetImages.All.Order(StringComparer.Ordinal));
        Assert.Equal(values.Count, Allowed.Count); // distinct
        Assert.All(values, v => Assert.True(v.StartsWith(PngPrefix, StringComparison.Ordinal) || v.StartsWith(SvgPrefix, StringComparison.Ordinal), v[..30]));
    }

    [Fact]
    public void No_provider_code_outside_WidgetImages_builds_image_bytes_or_data_uris()
    {
        var source = Path.Combine(ProviderBoundaryTests.RepositoryRoot(), "src", "ServerMonitor.WidgetProvider");
        var files = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "obj" or "bin"))
            .ToList();

        Assert.Contains(files, f => Path.GetFileName(f) == "WidgetImages.cs"); // the walk is real
        foreach (var file in files.Where(f => Path.GetFileName(f) != "WidgetImages.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("data:image", text, StringComparison.Ordinal);
            Assert.DoesNotContain("base64", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- templates: literal constants only, never a binding ---------------------------------------------

    [Fact]
    public void Template_urls_are_literal_constants_never_bindings()
    {
        foreach (var size in Sizes)
        {
            var template = JsonNode.Parse(WidgetCardRenderer.TemplateFor(size))!;
            var urls = CardTemplateHarness.Strings(template).Where(s => s.Key == "url").Select(s => s.Value).ToList();

            if (size == WidgetSizeHint.Small)
            {
                Assert.Empty(urls); // no bars, no empty icon on Small
            }
            else
            {
                // 3 metrics x (fresh + stale) x 2 themes x (fill + track) = 24 bar segments, + 2 icon variants.
                Assert.Equal(26, urls.Count);
            }

            Assert.All(urls, u =>
            {
                Assert.Contains(u, Allowed);
                Assert.DoesNotContain("${", u, StringComparison.Ordinal);
            });

            // backgroundImage is always an object holding a constant url, never a bound string.
            Assert.All(CardTemplateHarness.Objects(template).Where(o => o.Node["backgroundImage"] is not null),
                o => Assert.IsType<JsonObject>(o.Node["backgroundImage"]));

            // Every object that holds an image sits under a $when on the host theme (selection by $when).
            Assert.All(ImageHolders(template), path => Assert.Contains(path, p =>
                (string?)p["$when"] is CardTemplateHarness.DarkTheme or CardTemplateHarness.LightTheme));
        }
    }

    private static IEnumerable<List<JsonObject>> ImageHolders(JsonNode template)
    {
        var found = new List<List<JsonObject>>();
        void Walk(JsonNode? node, List<JsonObject> ancestors)
        {
            switch (node)
            {
                case JsonObject obj:
                    var path = new List<JsonObject>(ancestors) { obj };
                    if (obj["url"] is JsonValue)
                    {
                        found.Add(path);
                    }

                    foreach (var (_, child) in obj)
                    {
                        Walk(child, path);
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        Walk(child, ancestors);
                    }

                    break;
            }
        }

        Walk(template, []);
        return found;
    }

    // ---- the full scan: template + data + expanded, size x state x culture x theme -----------------------

    [Fact]
    public void Every_url_on_every_card_is_a_constant_and_the_data_never_carries_a_uri()
    {
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        var expandedUrls = 0;
        foreach (var size in Sizes)
        {
            var templateText = WidgetCardRenderer.TemplateFor(size);
            foreach (var action in ForbiddenActions)
            {
                Assert.DoesNotContain(action, templateText, StringComparison.Ordinal);
            }

            var templateValues = CardTemplateHarness.Strings(JsonNode.Parse(templateText)).ToList();
            Assert.All(templateValues.Where(s => !Allowed.Contains(s.Value)), s => Assert.DoesNotMatch(UrlLike, s.Value));
            Assert.All(templateValues.Where(s => !Allowed.Contains(s.Value)),
                s => Assert.DoesNotContain("data:", s.Value, StringComparison.OrdinalIgnoreCase));

            foreach (var names in new[] { null, WidgetCardRendererTests.HostileNames })
            {
                foreach (var (state, read) in WidgetCardRendererTests.States(names))
                {
                    foreach (var culture in Cultures)
                    {
                        var vm = WidgetViewModelBuilder.Build(read, size, Now, WidgetStrings.ForCulture(CultureInfo.GetCultureInfo(culture)));
                        var card = WidgetCardRenderer.Render(vm);

                        Assert.DoesNotContain("data:", card.DataJson, StringComparison.OrdinalIgnoreCase);
                        if (names is null)
                        {
                            // (A hostile name may LOOK like a link; it is literal TextRun text, WidgetCardRendererTests.)
                            Assert.All(CardTemplateHarness.Strings(JsonNode.Parse(card.DataJson)), s => Assert.DoesNotMatch(UrlLike, s.Value));
                        }

                        foreach (var theme in Themes)
                        {
                            var expanded = CardTemplateHarness.Expand(card.TemplateJson, card.DataJson, theme);
                            foreach (var (key, value) in CardTemplateHarness.Strings(expanded).Where(s => s.Key == "url"))
                            {
                                Assert.True(Allowed.Contains(value), $"{size}/{state}/{culture}/{theme}: {key} is not a WidgetImages constant");
                                Assert.Equal(theme == "dark", IsDarkVariant(value));
                                expandedUrls++;
                            }

                            foreach (var action in CardTemplateHarness.Actions(expanded))
                            {
                                Assert.DoesNotContain((string?)action["type"], ForbiddenActions);
                                verbs.Add((string)action["verb"]!);
                            }
                        }
                    }
                }
            }
        }

        Assert.True(expandedUrls > 0); // the walk is real
        Assert.Equal(new[] { "openDashboard", "openServer" }, verbs.Order(StringComparer.Ordinal));
    }

    private static bool IsDarkVariant(string url) =>
        typeof(WidgetImages).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Single(f => f.IsLiteral && (string)f.GetRawConstantValue()! == url).Name.EndsWith("Dark", StringComparison.Ordinal);

    // ---- PNG bytes: valid, critical/transparency chunks only, small --------------------------------------

    private static readonly string[] AllowedChunks = ["IHDR", "PLTE", "tRNS", "IDAT", "IEND"];

    internal static List<(string Type, byte[] Data)> PngChunks(byte[] png)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.AsSpan(0, 8).SequenceEqual(signature), "PNG signature");
        var chunks = new List<(string, byte[])>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(offset + 4, 4 + length)), crc);
            chunks.Add((type, data));
            offset += 12 + length;
        }

        Assert.Equal(png.Length, offset); // nothing trailing after IEND
        return chunks;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    internal static byte[] Decode(string uri) => Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);

    public static TheoryData<string> PngConstants()
    {
        var data = new TheoryData<string>();
        foreach (var f in typeof(WidgetImages).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            if (((string)f.GetRawConstantValue()!).StartsWith(PngPrefix, StringComparison.Ordinal))
            {
                data.Add(f.Name);
            }
        }

        return data;
    }

    private static string ConstantNamed(string name) =>
        (string)typeof(WidgetImages).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    [Theory]
    [MemberData(nameof(PngConstants))]
    public void Each_png_is_valid_small_and_carries_no_metadata_chunk(string name)
    {
        var uri = ConstantNamed(name);
        Assert.True(uri.Length - PngPrefix.Length <= 4096, $"{name}: {uri.Length - PngPrefix.Length} base64 chars > 4 KB");

        var chunks = PngChunks(Decode(uri));
        Assert.Equal("IHDR", chunks[0].Type);
        Assert.Equal("IEND", chunks[^1].Type);
        Assert.All(chunks, c => Assert.Contains(c.Type, AllowedChunks)); // so no tEXt/iTXt/zTXt/eXIf/tIME
        Assert.Contains(chunks, c => c.Type == "IDAT");
    }

    [Fact]
    public void The_png_check_rejects_a_text_chunk()
    {
        // Anti-vacuity: the same walk on a bar constant with a tEXt chunk spliced in before IEND.
        var png = Decode(WidgetImages.BarCpuDark);
        var text = Encoding.ASCII.GetBytes("tEXtComment\0C:\\Users\\x");
        var chunk = new byte[8 + text.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)(text.Length - 4));
        text.CopyTo(chunk, 4);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4 + text.Length), Crc32(text));
        var spliced = png[..^12].Concat(chunk).Concat(png[^12..]).ToArray();

        Assert.Contains(PngChunks(spliced), c => !AllowedChunks.Contains(c.Type));
    }

    [Fact]
    public void The_image_payload_of_the_largest_template_stays_under_32_KB()
    {
        var largest = Sizes.Max(size => CardTemplateHarness.Strings(JsonNode.Parse(WidgetCardRenderer.TemplateFor(size)))
            .Where(s => s.Key == "url").Sum(s => s.Value.Length));
        Assert.InRange(largest, 1, 32 * 1024);
        Assert.InRange(WidgetImages.All.Sum(u => u.Length), 1, 32 * 1024);
    }

    // ---- SVG: strict allowlist (Vigil §3). No SVG ships today (C3 DV-1); the checker is proven on samples.

    private static readonly HashSet<string> SvgElements =
        ["svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon"];

    internal static List<string> SvgViolations(string svg)
    {
        var violations = new List<string>();
        if (svg.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || svg.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("doctype/entity");
            return violations;
        }

        if (svg.Contains("url(", StringComparison.OrdinalIgnoreCase)) violations.Add("url(");
        if (svg.Contains("http", StringComparison.OrdinalIgnoreCase)) violations.Add("http");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(svg);
        }
        catch (XmlException)
        {
            violations.Add("not well-formed");
            return violations;
        }

        foreach (var element in doc.Descendants())
        {
            if (!SvgElements.Contains(element.Name.LocalName)) violations.Add("element " + element.Name.LocalName);
            foreach (var attribute in element.Attributes())
            {
                var local = attribute.Name.LocalName;
                if (local is "href" || local.StartsWith("on", StringComparison.OrdinalIgnoreCase) || local == "style")
                {
                    violations.Add("attribute " + attribute.Name);
                }
            }
        }

        return violations;
    }

    [Fact]
    public void Every_svg_constant_passes_the_strict_allowlist()
    {
        var svgs = WidgetImages.All.Where(u => u.StartsWith(SvgPrefix, StringComparison.Ordinal)).ToList();
        Assert.Empty(svgs); // C3 DV-1: PNG only — the SVG namespace itself needs "http", which the allowlist forbids
        Assert.All(svgs, u => Assert.Empty(SvgViolations(Encoding.UTF8.GetString(Decode(u)))));
    }

    [Theory]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<svg><use href=\"#a\"/></svg>")]
    [InlineData("<svg><image href=\"x.png\"/></svg>")]
    [InlineData("<svg><foreignObject/></svg>")]
    [InlineData("<svg><a><path d=\"M0 0\"/></a></svg>")]
    [InlineData("<svg><style>p{}</style></svg>")]
    [InlineData("<svg><path onload=\"x()\" d=\"M0 0\"/></svg>")]
    [InlineData("<svg><rect fill=\"url(#g)\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x \"y\">]><svg/>")]
    public void The_svg_allowlist_rejects_every_forbidden_construct(string svg) =>
        Assert.NotEmpty(SvgViolations(svg));

    [Fact]
    public void The_svg_allowlist_accepts_plain_shapes()
    {
        Assert.Empty(SvgViolations("<svg viewBox=\"0 0 4 4\"><g><path d=\"M0 0h4\" stroke=\"#fff\"/><rect width=\"1\" height=\"1\"/></g></svg>"));
    }
}
