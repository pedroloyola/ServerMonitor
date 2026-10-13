using System.Globalization;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 B12-01 (Beacon runtime 1.04:1, Prism spec prism-ui12-decisions.md §1): in Light the selected segment's white
/// fill on a white track is not a perceivable boundary. The sliding indicator's existing Light-only 1 px inner outline
/// now uses SaSegmentedSelectedOutlineBrush (#7E7E7E), which must reach the 3:1 non-text contrast against both the
/// composited track and the composited fill on every Light surface. Computed from the token hexes; runtime capture is
/// Beacon's (Settings theme, History, Services, editor auth + jump auth; Light, unfocused).
/// </summary>
public sealed class Ui12SegmentedOutlineContrastTests
{
    private static readonly string[] LightSurfaces = ["SaColorCanvasLight", "SaColorSurfaceLight", "SaColorElevatedSurfaceLight"];

    [Theory]
    [InlineData("SaColorSegmentedTrackLight", "SaColorSelectedLight")]                    // rect: History, Workloads
    [InlineData("SaColorSegmentedPillTrackLight", "SaColorSegmentedPillSelectedLight")]   // pill: editor auth, jump auth
    [InlineData("SaColorSegmentedThemeTrackLight", "SaColorSegmentedThemeSelectedLight")] // theme: Settings
    public void SegmentedSelectedOutlineMeetsNonTextContrast(string track, string fill)
    {
        var colours = Primitives();
        var outline = colours["SaColorSegmentedSelectedOutlineLight"];
        foreach (var surface in LightSurfaces)
        {
            var composedTrack = Over(colours[track], colours[surface]);
            var composedFill = Over(colours[fill], composedTrack);
            Assert.True(Contrast(outline, composedTrack) >= 3.0, $"{track} on {surface}: {Contrast(outline, composedTrack):0.00}:1");
            Assert.True(Contrast(outline, composedFill) >= 3.0, $"{fill} on {track} on {surface}: {Contrast(outline, composedFill):0.00}:1");
        }

        Assert.Equal("#7E7E7E", outline); // Prism spec value (lightest passing grey; = SaColorToggleOffTrackLight)
    }

    [Fact]
    public void OutlineTokenIsLightOnlyAndCoveredInHighContrast()
    {
        var themes = AppSourceTree.LoadXaml("Styles/Tokens/Color.Semantic.xaml").Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(AppSourceTree.Xaml + "Key") is not null)
            .ToDictionary(
                e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!,
                e => (string)e.Elements().Single(b => (string?)b.Attribute(AppSourceTree.Xaml + "Key") == "SaSegmentedSelectedOutlineBrush").Attribute("Color")!);
        Assert.Equal("{StaticResource SaColorSegmentedSelectedOutlineLight}", themes["Light"]);
        Assert.Equal("Transparent", themes["Dark"]);
        Assert.Equal("{ThemeResource SystemColorWindowColor}", themes["HighContrast"]);
    }

    /// <summary>The 5 segmented selector hosts (the sidebar is out of B12-01 scope) all pass the outline token.</summary>
    [Fact]
    public void EverySegmentedSelectorPassesTheOutlineToken()
    {
        var indicators = AppSourceTree.Files(".xaml")
            .Where(f => f.StartsWith("Views/", StringComparison.Ordinal) || f == "Controls/ServerFormControl.xaml")
            .SelectMany(f => AppSourceTree.LoadXaml(f).Descendants()
                .Where(e => e.Attributes().Any(a => a.Name.LocalName == "SaSlidingSelection.IndicatorBrush"))
                .Select(e => (File: f, Outline: e.Attributes().SingleOrDefault(a => a.Name.LocalName == "SaSlidingSelection.LightOutlineBrush")?.Value)))
            .ToList();

        Assert.Equal(5, indicators.Count);
        Assert.All(indicators, i => Assert.Equal("{ThemeResource SaSegmentedSelectedOutlineBrush}", i.Outline));
    }

    private static Dictionary<string, string> Primitives() =>
        AppSourceTree.LoadXaml("Styles/Tokens/Color.Primitives.xaml").Descendants()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, e => e.Value.Trim().ToUpperInvariant(), StringComparer.Ordinal);

    // Source-over of #AARRGGBB on an opaque #RRGGBB (or #FFRRGGBB) background; returns opaque #RRGGBB.
    private static string Over(string top, string bottom)
    {
        var (a, tr, tg, tb) = Argb(top);
        var (_, br, bg, bb) = Argb(bottom);
        int Mix(int t, int b) => (int)Math.Round((t * a) + (b * (1 - a)));
        return $"#{Mix(tr, br):X2}{Mix(tg, bg):X2}{Mix(tb, bb):X2}";
    }

    private static (double A, int R, int G, int B) Argb(string hex)
    {
        var v = hex.TrimStart('#');
        var alpha = v.Length == 8 ? Hex(v[..2]) / 255.0 : 1.0;
        v = v.Length == 8 ? v[2..] : v;
        return (alpha, Hex(v[..2]), Hex(v[2..4]), Hex(v[4..6]));
    }

    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (_, r, g, b) = Argb(hex);
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }
}
