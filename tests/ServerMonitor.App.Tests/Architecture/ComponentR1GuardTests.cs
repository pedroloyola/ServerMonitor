using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Controls.Primitives;
using Windows.System;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 fix round 1: guards for the Prism / Cortex / Beacon component findings (MF-2, MF-4, R-1, R-3, F-2, F-3, F-4,
/// F-5, Prism nits, Cortex low on SaRadiusPill). Static: the runtime behaviour is evidenced by the gallery probes.
/// </summary>
public sealed class ComponentR1GuardTests
{
    private static IEnumerable<XElement> Styles(string file) => AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "Style");

    private static string? Key(XElement element) => (string?)element.Attribute(AppSourceTree.Xaml + "Key");

    private static string? Setter(XElement style, string property) => style.Elements()
        .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
        .Select(e => (string?)e.Attribute("Value")).SingleOrDefault();

    private static Dictionary<string, Dictionary<string, string>> ThemeEntries(string file, string attribute) =>
        AppSourceTree.LoadXaml(file).Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && Key(e) is "Dark" or "Light" or "HighContrast")
            .ToDictionary(theme => Key(theme)!, theme => theme.Elements().Where(e => Key(e) is not null && e.Attribute(attribute) is not null)
                .ToDictionary(e => Key(e)!, e => (string)e.Attribute(attribute)!, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>MF-4: a component's REST fill is never SaSelectedBrush (Highlight in HC = "looks selected").</summary>
    [Fact]
    public void RestFillsNeverUseTheSelectedBrush()
    {
        var offenders = AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/Components/", StringComparison.Ordinal))
            .SelectMany(file => Styles(file).Where(style => Setter(style, "Background") == "{ThemeResource SaSelectedBrush}").Select(style => $"{file}: {Key(style)}"))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("Styles/Components/Sa.Buttons.xaml", "SaSecondaryButtonStyle")]
    [InlineData("Styles/Components/Sa.Buttons.xaml", "SaSecondaryRectButtonStyle")]
    [InlineData("Styles/Components/Sa.Buttons.xaml", "SaPickerButtonStyle")] // UI.7B key picker
    [InlineData("Styles/Components/Sa.Buttons.xaml", "SaIconButtonStyle")]
    [InlineData("Styles/Components/Sa.Forms.xaml", "SaPillSearchFieldStyle")]
    [InlineData("Styles/Components/Sa.Forms.xaml", "SaSelectorPillStyle")]
    public void SecondaryRestControlsUseTheRestFillAndTheHighContrastBorder(string file, string key)
    {
        var style = Styles(file).Single(s => Key(s) == key);
        Assert.Equal("{ThemeResource SaSecondaryFillBrush}", Setter(style, "Background"));
        Assert.Equal("{ThemeResource SaSecondaryBorderBrush}", Setter(style, "BorderBrush"));
        Assert.Equal("{ThemeResource SaSecondaryBorderThickness}", Setter(style, "BorderThickness"));
    }

    [Fact]
    public void RestFillIsButtonFaceWithAButtonTextBorderOnlyInHighContrast()
    {
        var brushes = ThemeEntries("Styles/Tokens/Color.Semantic.xaml", "Color");
        Assert.Equal("{ThemeResource SystemColorButtonFaceColor}", brushes["HighContrast"]["SaSecondaryFillBrush"]);
        Assert.Equal("{ThemeResource SystemColorButtonTextColor}", brushes["HighContrast"]["SaSecondaryBorderBrush"]);
        Assert.Equal("{StaticResource SaColorSelectedDark}", brushes["Dark"]["SaSecondaryFillBrush"]);
        Assert.Equal("{StaticResource SaColorSelectedLight}", brushes["Light"]["SaSecondaryFillBrush"]);

        var thickness = AppSourceTree.LoadXaml("Styles/Components/Sa.Surfaces.xaml").Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && Key(e) is "Dark" or "Light" or "HighContrast")
            .ToDictionary(t => Key(t)!, t => t.Elements().Single(e => Key(e) == "SaSecondaryBorderThickness").Value.Trim(), StringComparer.Ordinal);
        Assert.Equal("0", thickness["Dark"]);
        Assert.Equal("0", thickness["Light"]);
        Assert.Equal("1", thickness["HighContrast"]);
    }

    /// <summary>Cortex R-3: Off-active is never GrayText (disabled-only) in HC; On = Highlight in HC, the state colour otherwise.</summary>
    [Fact]
    public void ToggleTracksUseStateColoursAndNeverGrayTextWhenEnabled()
    {
        var brushes = ThemeEntries("Styles/Tokens/Color.Semantic.xaml", "Color");
        Assert.Equal("{ThemeResource SystemColorWindowTextColor}", brushes["HighContrast"]["SaToggleOffTrackBrush"]);
        Assert.Equal("{ThemeResource SystemColorHighlightColor}", brushes["HighContrast"]["SaToggleOnTrackBrush"]);
        Assert.Equal("{StaticResource SaColorHealthyDark}", brushes["Dark"]["SaToggleOnTrackBrush"]);
        Assert.Equal("{StaticResource SaColorHealthyLight}", brushes["Light"]["SaToggleOnTrackBrush"]);

        var onTrack = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "SwitchKnobBounds");
        Assert.Equal("{ThemeResource SaToggleOnTrackBrush}", (string?)onTrack.Attribute("Fill"));
    }

    /// <summary>Cortex R-1: in HC the accent scope's text selection background is Highlight, not WindowText.</summary>
    [Theory]
    [InlineData("TextControlSelectionHighlightColor")]
    [InlineData("AccentFillColorSelectedTextBackgroundBrush")]
    public void HighContrastTextSelectionIsHighlight(string key)
    {
        var aliases = ThemeEntries("Styles/Components/Sa.AccentNeutralScope.xaml", "ResourceKey");
        Assert.Equal("SaSelectedBrush", aliases["HighContrast"][key]);
    }

    /// <summary>Beacon F-5: nav and segmented Pressed = overlay only - no content opacity.</summary>
    [Fact]
    public void RadioButtonPressedStatesChangeNoOpacity()
    {
        var templates = new[] { "Styles/Components/Sa.Navigation.xaml", "Styles/Components/Sa.Forms.xaml" }
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants()
                .Where(e => e.Name.LocalName == "ControlTemplate" && (string?)e.Attribute("TargetType") == "RadioButton")
                .Select(template => (file, template)))
            .ToList();
        Assert.True(templates.Count >= 3, "expected the nav and both segmented templates");

        var offenders = templates.SelectMany(t => t.template.Descendants()
                .Where(e => e.Name.LocalName == "VisualState" && ((string?)e.Attribute(AppSourceTree.Xaml + "Name"))?.Contains("Pressed", StringComparison.Ordinal) == true)
                .SelectMany(state => state.Descendants().Where(s => s.Name.LocalName == "Setter"
                    && ((string?)s.Attribute("Target"))?.EndsWith(".Opacity", StringComparison.Ordinal) == true)
                    .Select(s => $"{t.file}: {state.Attribute(AppSourceTree.Xaml + "Name")} sets {s.Attribute("Target")}")))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Beacon F-4: the notice's Name carries title + message, so its inner text is Raw (read once).</summary>
    [Fact]
    public void InlineNoticeTextIsReadOnce()
    {
        var template = AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants()
            .Single(e => e.Name.LocalName == "ControlTemplate" && ((string?)e.Attribute("TargetType"))?.EndsWith("SaInlineNotice", StringComparison.Ordinal) == true);
        var texts = template.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
        Assert.True(texts.Count >= 4);
        Assert.All(texts, text => Assert.Equal("Raw", (string?)text.Attribute("AutomationProperties.AccessibilityView")));
    }

    /// <summary>Prism MF-2 and nit: the primary's fill runs under its border (44 visible) and Disabled keeps SaPrimaryText.</summary>
    [Fact]
    public void PrimaryKeepsItsFullShapeAndItsTextWhenDisabled()
    {
        var template = AppSourceTree.LoadXaml("Styles/Components/Sa.Buttons.xaml").Descendants()
            .Single(e => e.Name.LocalName == "ControlTemplate" && Key(e) == "SaPrimaryButtonTemplate");
        var shell = template.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "Shell");
        Assert.Equal("OuterBorderEdge", (string?)shell.Attribute("BackgroundSizing"));

        var disabled = template.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "Disabled");
        Assert.DoesNotContain(disabled.Descendants(), s => ((string?)s.Attribute("Target"))?.EndsWith(".Foreground", StringComparison.Ordinal) == true);
    }

    /// <summary>Cortex low: a CornerRadius above h/2 renders an ellipse in WinUI, so components use h/2 literals, never SaRadiusPill.</summary>
    [Fact]
    public void ComponentsNeverConsumeTheFigmaPillRadius()
    {
        var offenders = AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/Components/", StringComparison.Ordinal))
            .Where(f => AppSourceTree.LoadXaml(f).Descendants().SelectMany(e => e.Attributes()).Any(a => a.Value.Contains("SaRadiusPill", StringComparison.Ordinal)))
            .ToList();
        Assert.True(offenders.Count == 0, "SaRadiusPill consumed by: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Beacon F-2: the Light status/secondary texts stay >= 4.5:1 on every Light surface token. Glass is translucent: it
    /// is modelled over the app canvas (#E7E7E7) at -2 levels, #E5E5E5 (MF-1 measured +3 over its backdrop, so this is
    /// conservative). Over the darkest corner of the Figma reference wallpaper the glass is darker - see the R1 report.
    /// </summary>
    [Theory]
    [InlineData("SaColorAttentionTextLight")]
    [InlineData("SaColorDangerTextLight")]
    [InlineData("SaColorTextSecondaryLight")]
    public void LightTextColoursMeetAaOnEveryLightSurface(string foregroundKey)
    {
        var colours = AppSourceTree.LoadXaml("Styles/Tokens/Color.Primitives.xaml").Root!.Elements()
            .Where(e => e.Name.LocalName == "Color").ToDictionary(e => Key(e)!, e => e.Value.Trim(), StringComparer.Ordinal);
        var surfaces = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["glass over the canvas (modelled)"] = "#E5E5E5",
            ["SaColorInteriorLight"] = colours["SaColorInteriorLight"],
            ["SaColorSurfaceLight"] = colours["SaColorSurfaceLight"],
            ["SaColorCanvasLight"] = colours["SaColorCanvasLight"],
            ["SaColorModalSurfaceLight"] = colours["SaColorModalSurfaceLight"]
        };
        var failures = surfaces.Select(s => (s.Key, Ratio: Contrast(colours[foregroundKey], s.Value)))
            .Where(r => r.Ratio < 4.5).Select(r => $"{foregroundKey} on {r.Key}: {r.Ratio:0.00}").ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// R2 (Beacon N-1, WCAG 1.4.11): the toggle's Off track is >= 3:1 against every surface of its theme AND the thumb is
    /// >= 3:1 against the track. Translucent glass is modelled at its measured extremes: Light #DEDEDE (darkest corner of
    /// the reference wallpaper) .. #EDEDED, Dark #1F1F1F.
    /// </summary>
    [Theory]
    [InlineData("Dark", "#1F1F1F")]
    [InlineData("Light", "#DEDEDE", "#EDEDED")]
    public void ToggleOffTrackMeetsNonTextContrast(string theme, params string[] glass)
    {
        var colours = AppSourceTree.LoadXaml("Styles/Tokens/Color.Primitives.xaml").Root!.Elements()
            .Where(e => e.Name.LocalName == "Color").ToDictionary(e => Key(e)!, e => e.Value.Trim(), StringComparer.Ordinal);
        var track = colours[$"SaColorToggleOffTrack{theme}"];
        var surfaces = new[] { "Canvas", "Surface", "Interior", "ModalSurface" }.Select(s => colours[$"SaColor{s}{theme}"]).Concat(glass);

        var failures = surfaces.Select(s => (s, Ratio: Contrast(track, s))).Where(r => r.Ratio < 3)
            .Select(r => $"track {track} on {r.s}: {r.Ratio:0.00}").ToList();
        var thumb = Contrast(colours[$"SaColorToggleThumb{theme}"], track);
        if (thumb < 3)
        {
            failures.Add($"thumb on track {track}: {thumb:0.00}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void SegmentedSelectionFollowsTheArrowsOnly()
    {
        // UI.3 Beacon F1: selection follows an arrow move inside the group; a focus entry never selects
        // (SaGroupNavigationEntryTests holds the full matrix).
        Assert.True(SaGroupNavigation.SelectsOnFocusMove(SaGroupNavigationMode.SelectionFollowsFocus, arrowInsideGroup: true));
        Assert.False(SaGroupNavigation.SelectsOnFocusMove(SaGroupNavigationMode.SelectionFollowsFocus, arrowInsideGroup: false));
        Assert.False(SaGroupNavigation.SelectsOnFocusMove(SaGroupNavigationMode.FocusOnly, arrowInsideGroup: true));
    }

    [Theory]
    [InlineData(VirtualKey.Right, 2, 5, 3)]
    [InlineData(VirtualKey.Down, 2, 5, 3)]
    [InlineData(VirtualKey.Left, 2, 5, 1)]
    [InlineData(VirtualKey.Up, 2, 5, 1)]
    [InlineData(VirtualKey.Right, 4, 5, null)]
    [InlineData(VirtualKey.Left, 0, 5, null)]
    [InlineData(VirtualKey.Tab, 2, 5, null)]
    [InlineData(VirtualKey.Right, -1, 5, null)]
    public void ArrowsMoveOneItemWithoutWrapping(VirtualKey key, int current, int count, int? expected) =>
        Assert.Equal(expected, SaGroupNavigation.ArrowTarget(key, current, count));

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var value = hex.TrimStart('#');
        value = value.Length == 8 ? value[2..] : value;
        double Channel(int index)
        {
            var c = int.Parse(value.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }
}
