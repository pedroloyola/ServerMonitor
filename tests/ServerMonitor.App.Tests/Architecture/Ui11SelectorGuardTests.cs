using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.11 H01 static guards (Prism UI11A §2.3, Cortex §7). Read-only checks over the checked-in XAML: a segmented item
/// never paints its own selection fill again (the track's ONE sliding indicator does), and every segmented group has
/// exactly one indicator host behind its items, painting the family's measured brush.
/// </summary>
public sealed class Ui11SelectorGuardTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Primitives = "using:ServerMonitor.App.Controls.Primitives";
    private static readonly XName IndicatorBrush = Primitives + "SaSlidingSelection.IndicatorBrush";

    private static readonly string[] SegmentedItemStyles =
    [
        "SaSegmentedRectItemStyle", "SaSegmentedRectFilterItemStyle", "SaSegmentedRectCompactItemStyle",
        "SaSegmentedPillItemStyle", "SaSegmentedThemeItemStyle"
    ];

    private static XElement Style(string file, string key) =>
        AppSourceTree.LoadXaml(file).Descendants(Presentation + "Style")
            .Single(style => (string?)style.Attribute(AppSourceTree.Xaml + "Key") == key);

    [Fact]
    public void SegmentedItems_NeverPaintTheirOwnSelectionFill()
    {
        // The Checked state keeps text colour + weight only; Shell / Highlight fills are the sliding indicator's (H01 -
        // painting both would show two pills, and fading the Shell would be the forbidden fade-out/fade-in).
        foreach (var key in SegmentedItemStyles)
        {
            var style = Style("Styles/Components/Sa.Forms.xaml", key);
            foreach (var setter in style.Descendants(Presentation + "VisualState")
                         .Where(s => (string?)s.Attribute(AppSourceTree.Xaml + "Name") == "Checked")
                         .SelectMany(s => s.Descendants(Presentation + "Setter")))
            {
                var target = (string?)setter.Attribute("Target") ?? "";
                Assert.False(target.StartsWith("Shell.", StringComparison.Ordinal) || target.StartsWith("Highlight.", StringComparison.Ordinal),
                    $"{key}: Checked paints {target}");
            }

            Assert.Empty(style.Descendants().Where(e => e.Name.LocalName == "BrushTransition"
                && e.Parent?.Parent is { } owner && (string?)owner.Attribute(AppSourceTree.Xaml + "Name") == "Shell"));
        }
    }

    /// <summary>Every segmented group (every file, gallery included) has exactly ONE indicator host behind its items.</summary>
    [Fact]
    public void EverySegmentedGroup_HasExactlyOneSlidingIndicatorHostBehindItsItems()
    {
        var groups = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => !AppSourceTree.IsUnderStyles(f)))
        {
            var document = AppSourceTree.LoadXaml(file);
            var items = document.Descendants(Presentation + "RadioButton")
                .Where(item => SegmentedItemStyles.Any(key => ((string?)item.Attribute("Style") ?? "").Contains("{StaticResource " + key + "}", StringComparison.Ordinal)))
                .ToList();
            foreach (var panel in items.Select(item => item.Parent!).Distinct())
            {
                groups++;
                var cell = panel.Name.LocalName == "Grid" && IndicatorHosts(panel).Any() ? panel : panel.Parent!;
                var hosts = IndicatorHosts(cell).ToList();
                var host = Assert.Single(hosts);
                Assert.Same(cell.Elements().First(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)), host); // first: behind the items
                Assert.Equal("False", (string?)host.Attribute("IsHitTestVisible"));
                Assert.Equal("Raw", (string?)host.Attribute("AutomationProperties.AccessibilityView"));
                Assert.Empty(host.Elements()); // empty: the indicator is its Composition child visual
                if (panel != cell)
                {
                    Assert.Equal("Grid", cell.Name.LocalName);
                }
            }
        }

        Assert.True(groups >= 9, $"expected the 5 production selectors + the 4 gallery samples, found {groups}");
    }

    [Theory]
    [InlineData("Views/SettingsPage.xaml", "SaSegmentedThemeSelectedBrush", false)]
    [InlineData("Views/HistoryPage.xaml", "SaSelectedBrush", true)]
    [InlineData("Views/WorkloadsPage.xaml", "SaSelectedBrush", true)]
    [InlineData("Controls/ServerFormControl.xaml", "SaSegmentedPillSelectedBrush", false)]
    public void IndicatorHosts_PaintTheFamilysMeasuredSelectionBrush(string file, string brush, bool highlight)
    {
        var hosts = AllIndicatorHosts(AppSourceTree.LoadXaml(file)).ToList();

        Assert.NotEmpty(hosts);
        foreach (var host in hosts)
        {
            Assert.Equal("{ThemeResource " + brush + "}", (string?)host.Attribute(IndicatorBrush));
            Assert.Equal(highlight ? "{ThemeResource SaGlassHighlightBrush}" : null, (string?)host.Attribute(Primitives + "SaSlidingSelection.HighlightBrush"));
        }
    }

    private static IEnumerable<XElement> IndicatorHosts(XElement cell) => cell.Elements().Where(e => e.Attribute(IndicatorBrush) is not null);

    private static IEnumerable<XElement> AllIndicatorHosts(XDocument document) => document.Descendants().Where(e => e.Attribute(IndicatorBrush) is not null);
}
