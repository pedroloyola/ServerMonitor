using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 CONTRAST-01 (Prism UI12A): the severity / metric DOT brushes are tuned for a 6-8 px dot, not for text. "Sem
/// ligação" in the Compact row used the Light coral dot colour (#BD5766, 3.80:1 composited) as 10 px text. Text uses a
/// text token; the dot keeps its colour. Computed from the token hexes on the Compact row over every surface; the real
/// window material stays a runtime check (Beacon R-10).
/// </summary>
public sealed partial class Ui12StatusTextContrastTests
{
    [Fact]
    public void SeverityDotBrushesAreNeverTextColours()
    {
        var failures = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => !f.StartsWith("Qa/", StringComparison.Ordinal)))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants())
            {
                if (element.Name.LocalName == "TextBlock" && DotBrush().IsMatch((string?)element.Attribute("Foreground") ?? ""))
                {
                    failures.Add($"{file}: TextBlock Foreground={(string?)element.Attribute("Foreground")}");
                }

                if (element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "TextBlock")
                {
                    failures.AddRange(element.Elements()
                        .Where(s => (string?)s.Attribute("Property") == "Foreground" && DotBrush().IsMatch((string?)s.Attribute("Value") ?? ""))
                        .Select(s => $"{file}: TextBlock style {(string?)element.Attribute(AppSourceTree.Xaml + "Key")} Foreground={(string?)s.Attribute("Value")}"));
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// R2 N1 (Cortex): the brush is resolved IN EACH THEME'S DICTIONARY and measured against THAT theme's row and surfaces -
    /// a Dark/Light swap of the token (Light text #FF858D on a light surface) must fail, not pass on "some entry matches".
    /// </summary>
    [Theory]
    [InlineData("Light", "SaColorCanvasLight")]
    [InlineData("Light", "SaColorSurfaceLight")]
    [InlineData("Light", "SaColorElevatedSurfaceLight")]
    [InlineData("Dark", "SaColorCanvasDark")]
    [InlineData("Dark", "SaColorSurfaceDark")]
    [InlineData("Dark", "SaColorElevatedSurfaceDark")]
    public void OfflineStatusTextMeetsTextContrastOnTheCompactRow(string theme, string surface)
    {
        var colours = AppSourceTree.LoadXaml("Styles/Tokens/Color.Primitives.xaml").Descendants()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, e => e.Value.Trim(), StringComparer.Ordinal);
        var text = colours[PrimitiveOf(ThemeBrushColour(theme, "SaOfflineTextBrush"))];
        var row = colours[PrimitiveOf(ThemeBrushColour(theme, "SaCompactRowBrush"))];

        var ratio = Contrast(text, Over(row, colours[surface]));
        Assert.True(ratio >= 4.5, $"{theme}: SaOfflineTextBrush {text} on the Compact row over {surface}: {ratio:0.00}:1");
    }

    [Fact]
    public void OfflineStatusTextIsWindowTextInHighContrast() =>
        Assert.Equal("{ThemeResource SystemColorWindowTextColor}", ThemeBrushColour("HighContrast", "SaOfflineTextBrush"));

    // The Color attribute of a brush key inside ONE theme dictionary of Color.Semantic.xaml.
    private static string ThemeBrushColour(string theme, string key) =>
        (string)AppSourceTree.LoadXaml("Styles/Tokens/Color.Semantic.xaml").Descendants()
            .Single(d => d.Name.LocalName == "ResourceDictionary" && (string?)d.Attribute(AppSourceTree.Xaml + "Key") == theme)
            .Elements().Single(b => (string?)b.Attribute(AppSourceTree.Xaml + "Key") == key)
            .Attribute("Color")!;

    private static string PrimitiveOf(string markup)
    {
        var match = Regex.Match(markup, @"\A\{StaticResource (Sa\w+)\}\z");
        Assert.True(match.Success, $"expected a primitive colour reference, got {markup}");
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"\{ThemeResource Sa(Healthy|Attention|Error|Offline|Cpu|Memory|Disk)Brush\}")]
    private static partial Regex DotBrush();

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
