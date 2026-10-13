using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 HC-01 / HC-02 (Prism UI12A): in High Contrast SaHoverBrush, SaSelectedBrush and the segmented selection brushes
/// are Highlight. A state that paints one of them as a fill must also switch the text to the matching *TextBrush
/// (HighlightText in HC) - otherwise WindowText lands on Highlight (computed 1.44-2.15:1). Templates whose content keeps
/// its own text colour use the veil tokens instead (same colour in Dark/Light, Transparent in HC), and a persistent
/// selection is drawn as an HC-only Highlight outline. Static guard (Prism hc_states logic); real HC stays NOT_RUN.
/// </summary>
public sealed class Ui12HighContrastStateGuardTests
{
    private static readonly string[] HighlightFills =
        ["SaHoverBrush", "SaSelectedBrush", "SaSegmentedPillSelectedBrush", "SaSegmentedThemeSelectedBrush"];

    private static readonly string[] StateTextBrushes = ["SaHoverTextBrush", "SaSelectedTextBrush", "SaPrimaryTextBrush"];

    [Fact]
    public void EveryStateThatPaintsAHighlightFillAlsoSwapsTheText()
    {
        var failures = new List<string>();
        var states = 0;
        foreach (var file in ProductXaml())
        {
            foreach (var state in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "VisualState"))
            {
                var setters = state.Descendants().Where(e => e.Name.LocalName == "Setter").ToList();
                var fills = setters.Where(s => !Target(s).EndsWith("Foreground", StringComparison.Ordinal) && IsHighlightFill(Value(s))).ToList();
                if (fills.Count == 0)
                {
                    continue;
                }

                states++;
                if (!setters.Any(s => StateTextBrushes.Any(t => Value(s).Contains("{ThemeResource " + t + "}", StringComparison.Ordinal))))
                {
                    failures.Add($"{file}: VisualState {(string?)state.Attribute(AppSourceTree.Xaml + "Name")} paints "
                        + string.Join(", ", fills.Select(s => Target(s) + "=" + Value(s))) + " without a *TextBrush Foreground");
                }
            }
        }

        Assert.True(states > 0, "No VisualState paints a Highlight fill; the guard would be vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>An overlay element declaring a Highlight fill and shown by an Opacity setter escapes the setter rule above
    /// (the SSH-config import's SelectedOverlay, HC-01): no product element may declare one.</summary>
    [Fact]
    public void NoProductElementDeclaresAHighlightFill()
    {
        var failures = ProductXaml()
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants()
                .Where(e => e.Name.LocalName != "Setter")
                .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "Background" or "Fill" && IsHighlightFill(a.Value)))
                .Select(a => $"{file}: <{a.Parent!.Name.LocalName} {(string?)a.Parent.Attribute(AppSourceTree.Xaml + "Name")}> {a.Name.LocalName}={a.Value}"))
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Dark/Light must stay pixel-identical: the veils are the very colours of the state brushes they replace.</summary>
    [Theory]
    [InlineData("SaHoverVeilBrush", "SaHoverBrush")]
    [InlineData("SaSelectedVeilBrush", "SaSelectedBrush")]
    public void VeilsKeepTheStateColourOutsideHighContrastAndVanishInIt(string veil, string original)
    {
        var themes = SemanticThemes();
        Assert.Equal(themes["Dark"][original], themes["Dark"][veil]);
        Assert.Equal(themes["Light"][original], themes["Light"][veil]);
        Assert.Equal("Transparent", themes["HighContrast"][veil]);
    }

    [Fact]
    public void SelectionOutlineIsHighContrastOnly()
    {
        var themes = SemanticThemes();
        Assert.Equal("Transparent", themes["Dark"]["SaSelectedOutlineBrush"]);
        Assert.Equal("Transparent", themes["Light"]["SaSelectedOutlineBrush"]);
        Assert.Equal("{ThemeResource SystemColorHighlightColor}", themes["HighContrast"]["SaSelectedOutlineBrush"]);
    }

    /// <summary>HC-01: every selected state of the import row shows the HC outline together with the (veil) fill.</summary>
    [Fact]
    public void InsetListSelectionShowsTheOutlineInEverySelectedState()
    {
        var template = AppSourceTree.LoadXaml("Styles/Components/Sa.Surfaces.xaml").Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaInsetListItemStyle");
        var selected = template.Descendants().Where(e => e.Name.LocalName == "VisualState"
            && ((string?)e.Attribute(AppSourceTree.Xaml + "Name"))?.Contains("Selected", StringComparison.Ordinal) == true).ToList();

        Assert.Equal(3, selected.Count);
        Assert.All(selected, state => Assert.Contains(state.Descendants(), s => s.Name.LocalName == "Setter" && Target(s) == "SelectedOutline.Opacity"));
        var outline = template.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "SelectedOutline");
        Assert.Equal("{ThemeResource SaSelectedOutlineBrush}", (string?)outline.Attribute("BorderBrush"));
    }

    private static IEnumerable<string> ProductXaml() => AppSourceTree.Files(".xaml")
        .Where(f => f.StartsWith("Styles/Components/", StringComparison.Ordinal) || f.StartsWith("Views/", StringComparison.Ordinal)
            || f.StartsWith("Controls/", StringComparison.Ordinal));

    private static bool IsHighlightFill(string value) =>
        HighlightFills.Any(b => value.Contains("{ThemeResource " + b + "}", StringComparison.Ordinal));

    private static string Target(XElement setter) => (string?)setter.Attribute("Target") ?? (string?)setter.Attribute("Property") ?? "";

    private static string Value(XElement setter) => (string?)setter.Attribute("Value") ?? "";

    private static Dictionary<string, Dictionary<string, string>> SemanticThemes() =>
        AppSourceTree.LoadXaml("Styles/Tokens/Color.Semantic.xaml").Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(AppSourceTree.Xaml + "Key") is not null)
            .ToDictionary(
                e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!,
                e => e.Elements().Where(b => b.Attribute("Color") is not null)
                    .ToDictionary(b => (string)b.Attribute(AppSourceTree.Xaml + "Key")!, b => (string)b.Attribute("Color")!, StringComparer.Ordinal),
                StringComparer.Ordinal);
}
