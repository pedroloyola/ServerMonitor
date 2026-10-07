using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.8 brief 8C item 6 / SPEC D-UI8-4, D-UI8-13, D-UI8-15, R-6, R-8, R-11, RC-8. Structural guards over EVERY piece of
/// Compact XAML - the Compact subtree of MainWindow.xaml, Controls/CompactShell.xaml and the SaCompact* styles in the
/// component layer: no legacy health copy (<c>HealthDisplayName</c> / <c>ServerHealth*</c>), no MDL2 glyph, no legacy
/// accent, no colour literal, no disabled text scaling, no literal font size. Each detector is counterproved on a crafted
/// snippet (<see cref="EachDetector_CatchesItsViolation"/>), so a detector that silently stops matching fails too. Plus
/// the structure the a11y and layout decisions rely on (list semantics, one tab stop, arrows, spacing in the layout,
/// overlay scroll without gutter, footer outside the scroll).
/// </summary>
public sealed partial class Ui8CompactXamlGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Every Compact XAML element, from the three places it lives.</summary>
    private static IReadOnlyList<(string Where, XElement Element)> CompactElements()
    {
        var result = new List<(string, XElement)>();
        var window = AppSourceTree.LoadXaml("MainWindow.xaml");
        var root = window.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "CompactRoot");
        result.AddRange(root.DescendantsAndSelf().Select(e => ("MainWindow.xaml#CompactRoot", e)));
        result.AddRange(AppSourceTree.LoadXaml("Controls/CompactShell.xaml").Root!.DescendantsAndSelf().Select(e => ("Controls/CompactShell.xaml", e)));
        var primitives = AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Root!.Elements()
            .Where(e => ((string?)e.Attribute(AppSourceTree.Xaml + "Key"))?.StartsWith("SaCompact", StringComparison.Ordinal) == true
                || (string?)e.Attribute("TargetType") == "primitives:SaCompactMetricBar");
        result.AddRange(primitives.SelectMany(e => e.DescendantsAndSelf()).Select(e => ("Sa.Primitives.xaml#SaCompact*", e)));
        return result;
    }

    public static TheoryData<string> Detectors => ["legacy-health", "mdl2", "legacy-accent", "colour-literal", "text-scale", "font-size-literal"];

    [Theory]
    [MemberData(nameof(Detectors))]
    public void NoCompactXaml_ViolatesTheRule(string detector)
    {
        var elements = CompactElements();
        Assert.True(elements.Count > 100, $"precondition: the Compact XAML was found ({elements.Count} elements)");

        var violations = elements.SelectMany(pair => Violations(detector, pair.Element).Select(v => $"{pair.Where}: <{pair.Element.Name.LocalName}> {v}")).ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>Counterproof of each detector: a crafted violating snippet is caught; its fixed twin is not.</summary>
    [Theory]
    [InlineData("legacy-health", "<TextBlock Text=\"{Binding HealthDisplayName}\" />", "<TextBlock Text=\"{x:Bind StatusText}\" />")]
    [InlineData("legacy-health", "<TextBlock x:Uid=\"ServerHealthOffline\" />", "<TextBlock x:Uid=\"CompactMetricCpuLabel\" />")]
    [InlineData("mdl2", "<FontIcon Glyph=\"&#xE740;\" />", "<primitives:SaIcon Data=\"{StaticResource SaIconArrowRight01Data}\" />")]
    [InlineData("mdl2", "<SymbolIcon Symbol=\"Pin\" />", "<primitives:SaBrandMark Size=\"23\" />")]
    [InlineData("legacy-accent", "<Border Background=\"{ThemeResource BrandAccentBrush}\" />", "<Border Background=\"{ThemeResource SaCompactBarBrush}\" />")]
    [InlineData("legacy-accent", "<Border Background=\"{ThemeResource AccentSoftBrush}\" />", "<Border Background=\"{ThemeResource SaCompactRowBrush}\" />")]
    [InlineData("colour-literal", "<Border Background=\"#1846E1\" />", "<Border Background=\"{ThemeResource SaCompactRowBrush}\" />")]
    [InlineData("colour-literal", "<TextBlock Foreground=\"White\" />", "<TextBlock Foreground=\"{ThemeResource SaTextBrush}\" />")]
    [InlineData("colour-literal", "<Grid Background=\"Red\" />", "<Grid Background=\"Transparent\" />")]
    [InlineData("colour-literal", "<Setter Property=\"Background\" Value=\"#20FFFFFF\" />", "<Setter Property=\"Background\" Value=\"{ThemeResource SaHoverBrush}\" />")]
    [InlineData("text-scale", "<TextBlock IsTextScaleFactorEnabled=\"False\" />", "<TextBlock />")]
    [InlineData("font-size-literal", "<TextBlock FontSize=\"15\" />", "<TextBlock FontSize=\"{StaticResource SaFontSizeListRowTitle}\" />")]
    [InlineData("font-size-literal", "<Setter Property=\"FontSize\" Value=\"9\" />", "<Setter Property=\"FontSize\" Value=\"{StaticResource SaFontSizeCompactMetricLabel}\" />")]
    public void EachDetector_CatchesItsViolation(string detector, string violating, string fixedTwin)
    {
        Assert.NotEmpty(Violations(detector, Snippet(violating)));
        Assert.Empty(Violations(detector, Snippet(fixedTwin)));
    }

    // ---- structure the a11y / layout decisions rely on ----

    [Fact]
    public void TheList_IsAListOfOneTabStop_WithArrows_AndSpacingInTheLayout()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        var repeater = Named(shell, "CompactRepeater");

        Assert.Equal("SaDataTableList", repeater.Parent!.Name.LocalName);                       // List peer, "x of N" rows
        Assert.Equal("Once", (string?)repeater.Attribute("TabFocusNavigation"));               // one Tab stop for the list
        Assert.Equal("Enabled", (string?)repeater.Attribute("XYFocusKeyboardNavigation"));     // arrow keys between rows
        var layout = repeater.Descendants().Single(e => e.Name.LocalName == "StackLayout");
        Assert.Equal("{StaticResource SaSpace8}", (string?)layout.Attribute("Spacing"));       // R-11: never a per-item margin

        var template = shell.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "CompactServerRowTemplate");
        Assert.Equal("vm:CompactServerRowViewModel", (string?)template.Attribute(AppSourceTree.Xaml + "DataType"));
        var row = template.Elements().Single();
        Assert.Equal("ServerTableRowButton", row.Name.LocalName);                              // ONE target per row (Enter/Space)
        Assert.Equal("{x:Bind AccessibleName, Mode=OneWay}", (string?)row.Attribute("AutomationProperties.Name"));
        Assert.DoesNotContain(template.Descendants(), e => e.Attributes().Any(a => a.Value.Contains("{Binding", StringComparison.Ordinal)));
        Assert.DoesNotContain(row.Descendants(), e => e.Name.LocalName is "Button" or "ToggleSwitch" or "HyperlinkButton");
    }

    [Fact]
    public void TheScrollHasNoGutter_AndTheFooterIsOutsideIt()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        var scroller = Named(shell, "ListScroller");
        var footer = Named(shell, "FooterRow");

        Assert.Null(scroller.Attribute("Padding"));                                            // overlay scrollbar, 0 gutter
        Assert.Equal("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        Assert.DoesNotContain(footer.Ancestors(), e => e.Name.LocalName == "ScrollViewer");
        Assert.Equal("2", (string?)footer.Attribute("Grid.Row"));
        Assert.DoesNotContain(shell.Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == "CompactMetricsUnavailable");
    }

    [Fact]
    public void TheTitleStrip_KeepsItsNamesAndTheNativeCaptionReserve_AndUsesTheShellMaterial()
    {
        var window = AppSourceTree.LoadXaml("MainWindow.xaml");
        var drag = Named(window, "CompactDragRegion");
        var reserve = Named(window, "CompactCaptionColumn");
        var expand = Named(window, "CompactExpandButton");

        Assert.Equal("{StaticResource SaCompactButtonStyle}", (string?)expand.Attribute("Style"));
        Assert.Equal("CompactExpandButton", (string?)expand.Attribute(AppSourceTree.Xaml + "Uid")); // accessible name + tooltip
        Assert.Equal("1", (string?)expand.Attribute("Grid.Column"));                             // left of the reserve, outside drag
        Assert.Null(drag.Attribute("Grid.Column"));
        Assert.Same(drag.Parent, reserve.Parent!.Parent);
        Assert.DoesNotContain(window.Descendants(), e => e.Name.LocalName == "ToggleButton");      // the pin left the title (D-UI8-11)

        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.DoesNotContain("SaLegacyWindowBackgroundStyle", code, StringComparison.Ordinal);
        Assert.Contains("_usesOpaqueFallback ? \"SaOpaqueWindowBackgroundStyle\" : \"SaShellWindowBackgroundStyle\"", code, StringComparison.Ordinal);
        Assert.Contains("SaThemeRefresh.Remount(CompactShellView", code, StringComparison.Ordinal);
    }

    // ---- detectors ----

    private static IEnumerable<string> Violations(string detector, XElement element)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        string? Setter(string property) =>
            element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property ? (string?)element.Attribute("Value") : null;
        switch (detector)
        {
            case "legacy-health":
                return attributes.Where(a => LegacyHealth().IsMatch(a.Value)).Select(a => $"{a.Name.LocalName}=\"{a.Value}\"");
            case "mdl2":
                return (element.Name.LocalName is "FontIcon" or "SymbolIcon" or "SymbolIconSource" or "FontIconSource"
                        ? [element.Name.LocalName] : Enumerable.Empty<string>())
                    .Concat(attributes.Where(a => a.Name.LocalName == "Glyph").Select(a => $"Glyph=\"{a.Value}\""));
            case "legacy-accent":
                return attributes.Where(a => LegacyAccent().IsMatch(a.Value)).Select(a => $"{a.Name.LocalName}=\"{a.Value}\"");
            case "colour-literal":
                return attributes.Where(a => IsBrushProperty(a.Name.LocalName) && IsColourLiteral(a.Value)).Select(a => $"{a.Name.LocalName}=\"{a.Value}\"")
                    .Concat(attributes.Where(a => HexColour().IsMatch(a.Value)).Select(a => $"{a.Name.LocalName}=\"{a.Value}\""))
                    .Concat(new[] { "Background", "Foreground", "Fill", "BorderBrush", "Stroke" }
                        .Select(Setter).Where(v => v is not null && IsColourLiteral(v)).Select(v => $"Setter Value=\"{v}\""))
                    .Distinct();
            case "text-scale":
                return attributes.Where(a => a.Name.LocalName == "IsTextScaleFactorEnabled" && a.Value.Equals("False", StringComparison.OrdinalIgnoreCase))
                    .Select(a => "IsTextScaleFactorEnabled=False");
            case "font-size-literal":
                return attributes.Where(a => a.Name.LocalName == "FontSize" && !a.Value.StartsWith('{')).Select(a => $"FontSize=\"{a.Value}\"")
                    .Concat(Setter("FontSize") is { } size && !size.StartsWith('{') ? [$"Setter FontSize=\"{size}\""] : []);
            default:
                throw new ArgumentOutOfRangeException(nameof(detector), detector, null);
        }
    }

    private static bool IsBrushProperty(string name) =>
        name is "Background" or "Foreground" or "Fill" or "BorderBrush" or "Stroke" or "Color" || name.EndsWith("Brush", StringComparison.Ordinal);

    // A literal colour: "#..." or a named colour - anything that is not a markup extension. "Transparent" is not a colour
    // decision (hit-testable empty surface, e.g. the drag region) and stays allowed.
    private static bool IsColourLiteral(string value) => value.Length > 0 && !value.StartsWith('{') && value != "Transparent";

    private static XElement Snippet(string xml) =>
        XElement.Parse($"<Root xmlns=\"{Presentation}\" xmlns:x=\"{AppSourceTree.Xaml}\" xmlns:primitives=\"using:p\">{xml}</Root>").Elements().Single();

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name);

    [GeneratedRegex(@"HealthDisplayName|\bServerHealth(Healthy|Warning|Critical|Offline|Unknown)\b")]
    private static partial Regex LegacyHealth();

    [GeneratedRegex(@"BrandAccent|AccentSoft|AccentText|AccentPill|AccentFill|SystemAccent|#1846E1", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyAccent();

    [GeneratedRegex(@"#[0-9A-Fa-f]{3,8}\b")]
    private static partial Regex HexColour();
}
