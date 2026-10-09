using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.10 (GLOBAL VISUAL POLISH) guards: the rules the polish centralised must not silently regress. Each rule names its
/// finding (Prism UI.10A audit) and the Figma node that now draws it (UI.10B).
/// </summary>
public sealed partial class Ui10PolishGuardTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = AppSourceTree.Xaml;

    /// <summary>The dictionaries that define Button styles in the component layer.</summary>
    private static readonly string[] ButtonStyleDictionaries =
    [
        "Styles/Components/Sa.Buttons.xaml", "Styles/Components/Sa.Dialogs.xaml", "Styles/Components/Sa.Primitives.xaml"
    ];

    /// <summary>
    /// F06 justified exceptions (audit §1 H03 rule 4; Figma Manual A1 234:182): they keep a non-pill shape on purpose.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RadiusExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SaButtonBaseStyle"] = "the shared base: no height, no shape of its own",
        ["SaPickerButtonStyle"] = "the key file picker is a field: r12 like the inputs",
        ["SaPickerButtonErrorStyle"] = "the key file picker in error (a field)",
        ["SaDisclosureButtonStyle"] = "a settings row is a surface (r20), not an action button",
        ["SaCompactRowButtonStyle"] = "the compact server row is a surface (r15)",
        ["SaBreadcrumbParentButtonStyle"] = "a breadcrumb is a text link",
    };

    /// <summary>
    /// F06 (H03): every Button style outside the justified exceptions is a pill - its CornerRadius is exactly the
    /// SaRadiusButton&lt;h&gt; tier token of its effective Height, and that token is h/2. No literal radius on a button.
    /// </summary>
    [Fact]
    public void EveryTextButtonStyle_UsesTheRadiusTierTokenOfItsHeight()
    {
        var tiers = RadiusTiers();
        var styles = ButtonStyles();
        var failures = new List<string>();
        foreach (var (key, _) in styles)
        {
            if (RadiusExceptions.ContainsKey(key))
                continue;
            var height = Effective(styles, key, "Height");
            var radius = Effective(styles, key, "CornerRadius");
            if (height is null || radius is null)
            {
                failures.Add($"{key}: no effective Height/CornerRadius (height={height ?? "-"}, radius={radius ?? "-"})");
                continue;
            }

            var expected = $"{{StaticResource SaRadiusButton{height}}}";
            if (!string.Equals(radius, expected, StringComparison.Ordinal))
                failures.Add($"{key}: h{height} has CornerRadius \"{radius}\", expected \"{expected}\"");
            else if (!tiers.TryGetValue("SaRadiusButton" + height, out var value)
                     || value != double.Parse(height, CultureInfo.InvariantCulture) / 2)
                failures.Add($"{key}: tier SaRadiusButton{height} is missing or not h/2");
        }

        Assert.True(failures.Count == 0, "F06 pill rule broken:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>F06: each tier token is exactly h/2 (a radius above h/2 renders elliptical in WinUI).</summary>
    [Fact]
    public void EveryRadiusTier_IsHalfItsHeight()
    {
        var tiers = RadiusTiers();
        Assert.NotEmpty(tiers);
        Assert.All(tiers, tier => Assert.Equal(double.Parse(tier.Key["SaRadiusButton".Length..], CultureInfo.InvariantCulture) / 2, tier.Value));
    }

    /// <summary>
    /// F06: a page never re-shapes a tiered button locally - no Height or CornerRadius attribute on a Button whose style
    /// carries a tier (a local Height would leave the tier's radius above or below h/2).
    /// </summary>
    [Fact]
    public void NoPageOverridesTheHeightOrRadiusOfATieredButton()
    {
        var styles = ButtonStyles();
        var tiered = styles.Keys.Where(key => !RadiusExceptions.ContainsKey(key)).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml").Where(file => !AppSourceTree.IsUnderStyles(file)))
        {
            foreach (var button in AppSourceTree.LoadXaml(file).Descendants(Ns + "Button"))
            {
                var style = StyleKey((string?)button.Attribute("Style"));
                if (style is null || !tiered.Contains(style))
                    continue;
                foreach (var property in new[] { "Height", "CornerRadius" })
                    if (button.Attribute(property) is { } attribute)
                        failures.Add($"{file}: Button ({style}) sets {property}=\"{attribute.Value}\"");
            }
        }

        Assert.True(failures.Count == 0, "Local re-shape of a tiered button:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// F01/F02/F46 (H01, Prism UI.10C RC-1): the nav keyboard ring sits on the pill edge (margin 0 - never a frame larger
    /// than the selection; action buttons keep -3), every selectable
    /// sidebar item has the same 46 geometry (Definições included), and the rail item is a 46 circle.
    /// </summary>
    [Fact]
    public void NavItems_HaveAConcentricFocusRingAndOneGeometry()
    {
        var nav = AppSourceTree.LoadXaml("Styles/Components/Sa.Navigation.xaml").Root!.Elements(Ns + "Style")
            .Single(style => (string?)style.Attribute(X + "Key") == "SaNavItemStyle");
        Assert.Equal("0", (string?)Setter(nav, "FocusVisualMargin"));
        Assert.Equal("46", (string?)Setter(nav, "Height"));
        Assert.Equal("23", (string?)Setter(nav, "CornerRadius"));

        var sidebar = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml");
        var items = sidebar.Descendants(Ns + "RadioButton").ToList();
        Assert.Equal(4, items.Count);
        Assert.All(items, item => Assert.Equal("46", (string?)item.Attribute("Height")));
        var rows = sidebar.Descendants(Ns + "RowDefinition").Select(row => (string?)row.Attribute("Height")).ToList();
        foreach (var item in items)
            Assert.Equal("46", rows[int.Parse((string)item.Attribute("Grid.Row")!, CultureInfo.InvariantCulture)]);

        var code = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        Assert.Contains("item.Width = rail ? 46 : 160;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// F09/F10 (H04): Visão geral, Servidores and Definições share ONE header (Figma Page header 235:270): the same grid,
    /// title block, reserved line 2 and an actions slot centred on the title line (buttons) or the status text (Definições),
    /// so the "+ Adicionar" sits at the same y on both pages and line 2 never makes it jump; 24 to the content on both.
    /// </summary>
    [Theory]
    [InlineData("Views/DashboardPage.xaml", "SaPageHeaderActionsStyle")]
    [InlineData("Views/ServersPage.xaml", "SaPageHeaderActionsStyle")]
    [InlineData("Views/SettingsPage.xaml", "SaPageHeaderStatusTextStyle")]
    public void PageHeaders_AreTheSharedComponent(string page, string actionSlotStyle)
    {
        var document = AppSourceTree.LoadXaml(page);
        string? StyleOf(XElement element) => StyleKey((string?)element.Attribute("Style"));
        var header = Assert.Single(document.Descendants(Ns + "Grid"), grid => StyleOf(grid) == "SaPageHeaderGridStyle");
        Assert.Null(header.Attribute("ColumnSpacing"));
        var title = Assert.Single(header.Elements(Ns + "StackPanel"), panel => StyleOf(panel) == "SaPageHeaderTitleBlockStyle");
        Assert.Null(title.Attribute("Spacing"));
        Assert.Null(title.Attribute("VerticalAlignment"));
        var lineTwo = Assert.Single(title.Elements(Ns + "StackPanel"), panel => StyleOf(panel) == "SaPageHeaderLineTwoStyle");
        Assert.All(lineTwo.Elements(Ns + "TextBlock"), text => Assert.Equal("SaPageSubtitleTextStyle", StyleOf(text)));
        var slot = Assert.Single(header.Elements(), element => StyleOf(element) == actionSlotStyle);
        Assert.Equal("1", (string?)slot.Attribute("Grid.Column"));
        Assert.Null(slot.Attribute("VerticalAlignment"));
        Assert.Null(slot.Attribute("Margin"));
        if (page != "Views/SettingsPage.xaml")
        {
            var root = document.Descendants(Ns + "StackPanel").Single(panel => (string?)panel.Attribute(X + "Name") == "PageRoot");
            Assert.Equal("{StaticResource SaSpace24}", (string?)root.Attribute("Spacing"));
        }
    }

    /// <summary>F09/F10: the shared header's geometry (component layer).</summary>
    [Fact]
    public void PageHeaderStyles_CentreTheActionsOnTheTitleLine()
    {
        var styles = AppSourceTree.LoadXaml("Styles/Components/Sa.Text.xaml").Root!.Elements(Ns + "Style")
            .ToDictionary(style => (string)style.Attribute(X + "Key")!, StringComparer.Ordinal);
        Assert.Equal("16", Setter(styles["SaPageHeaderGridStyle"], "ColumnSpacing"));
        Assert.Equal("6", Setter(styles["SaPageHeaderTitleBlockStyle"], "Spacing"));
        Assert.Equal("Top", Setter(styles["SaPageHeaderTitleBlockStyle"], "VerticalAlignment"));
        Assert.Equal("{StaticResource SaLineHeightControl}", Setter(styles["SaPageHeaderLineTwoStyle"], "MinHeight"));
        Assert.Equal("Top", Setter(styles["SaPageHeaderActionsStyle"], "VerticalAlignment"));
        Assert.Equal("0,-2,0,0", Setter(styles["SaPageHeaderActionsStyle"], "Margin")); // 44 button on the 40 title line
        Assert.Equal("16", Setter(styles["SaPageHeaderActionsStyle"], "Spacing"));
        Assert.Equal("0,12,0,0", Setter(styles["SaPageHeaderStatusTextStyle"], "Margin")); // 16 line on the 40 title line
    }

    /// <summary>
    /// F11 (H05): the compact footer row is never shorter than the toggle it hosts - no fixed Height, a MinHeight at least
    /// the toggle template's switch area (52x34: hover/focus pill around the 44x26 track). A fixed 24 clipped it in 24/24
    /// captures (Beacon UI.10A).
    /// </summary>
    [Fact]
    public void CompactFooterRow_IsAtLeastTheToggleHeight()
    {
        var area = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml").Descendants(Ns + "Grid")
            .Single(grid => (string?)grid.Attribute(X + "Name") == "SwitchAreaGrid");
        var toggle = double.Parse((string)area.Attribute("Height")!, CultureInfo.InvariantCulture);

        var footer = AppSourceTree.LoadXaml("Controls/CompactShell.xaml").Descendants(Ns + "Grid")
            .Single(grid => (string?)grid.Attribute(X + "Name") == "FooterRow");
        Assert.Null(footer.Attribute("Height"));
        Assert.True(double.Parse((string?)footer.Attribute("MinHeight") ?? "0", CultureInfo.InvariantCulture) >= toggle,
            $"FooterRow MinHeight {(string?)footer.Attribute("MinHeight") ?? "-"} < the toggle's {toggle}");
        Assert.Contains(footer.Descendants(Ns + "ToggleSwitch"), element => (string?)element.Attribute("Style") == "{StaticResource SaToggleSwitchStyle}");
    }

    /// <summary>
    /// F21/F37/F39 (+F08): an irreversible action looks destructive everywhere - the restore confirmation is an Sa
    /// destructive dialog (no legacy dialog style left), "Repor histórico" is the danger pill like "Limpar histórico", and
    /// "Remover" in the Detail menu is set apart in the danger text.
    /// </summary>
    [Fact]
    public void IrreversibleActions_LookDestructive()
    {
        var restore = AppSourceTree.LoadXaml("Views/RestoreConfirmDialog.xaml").Root!;
        Assert.Equal("Destructive", (string?)restore.Attributes().Single(a => a.Name.LocalName == "SaDialog.Kind").Value);
        Assert.Contains(restore.Descendants(Ns + "StaticResource"), r => (string?)r.Attribute("ResourceKey") == "SaDialogStyle");
        foreach (var file in AppSourceTree.Files(".xaml").Concat(AppSourceTree.Files(".cs")))
        {
            var text = File.ReadAllText(AppSourceTree.Full(file));
            Assert.DoesNotContain("{StaticResource PremiumContentDialogStyle}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[\"PremiumContentDialogStyle\"]", text, StringComparison.Ordinal);
        }

        var data = AppSourceTree.LoadXaml("Views/SettingsDataPage.xaml");
        string? ButtonStyle(string name) => (string?)data.Descendants(Ns + "Button").Single(b => (string?)b.Attribute(X + "Name") == name).Attribute("Style");
        Assert.Equal("{StaticResource SaPillButtonSmallDangerStyle}", ButtonStyle("ResetHistoryButton"));
        Assert.Equal("{StaticResource SaPillButtonSmallDangerStyle}", ButtonStyle("ClearHistoryButton"));

        var menu = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml").Descendants(Ns + "MenuFlyout").Single().Elements().ToList();
        var remove = menu.Single(item => (string?)item.Attribute(X + "Uid") == "ServerCardRemoveMenuItem");
        Assert.Equal("{ThemeResource SaDangerTextBrush}", (string?)remove.Attribute("Foreground"));
        Assert.Equal("MenuFlyoutSeparator", menu[menu.IndexOf(remove) - 1].Name.LocalName);
        var hide = menu.Single(item => (string?)item.Attribute(X + "Uid") == "ServerCardHideMenuItem");
        Assert.Null(hide.Attribute("Foreground"));
    }

    /// <summary>
    /// F25 view binding + HD-1 (tests review L-1): on the Detail each big value takes its severity style through the shared
    /// converter, and the three meters stay severity-free - their colour is the metric's identity, never a state.
    /// </summary>
    [Fact]
    public void DetailValues_BindSeverity_AndTheMetersStaySeverityFree()
    {
        var page = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml");
        foreach (var metric in new[] { "Cpu", "Memory", "Disk" })
        {
            var value = page.Descendants(Ns + "TextBlock").Single(t => (string?)t.Attribute("Text") == $"{{Binding {metric}ValueText}}");
            Assert.Equal($"{{Binding {metric}Severity, Converter={{StaticResource DetailMetricValueStyleConverter}}}}", (string?)value.Attribute("Style"));
        }

        var meters = page.Descendants().Where(e => e.Name.LocalName is "SaPulseBars" or "SaSegmentMeter").ToList();
        Assert.Equal(3, meters.Count);
        Assert.All(meters, meter =>
        {
            Assert.DoesNotContain(meter.Attributes(), a => a.Value.Contains("Severity", StringComparison.Ordinal));
            Assert.Null(meter.Attribute("Foreground"));
        });
    }

    /// <summary>
    /// UI.10 H07 (Prism option D): the Compact "Expandir" ends at the content's right edge and never reaches the native
    /// caption band - its right margin = the body's right padding, no column reserves the caption width, the strip's top
    /// comes from the MEASURED caption height (no literal), and the title layout gets the whole width minus the two edges.
    /// </summary>
    [Fact]
    public void CompactExpand_IsRightAligned_BelowTheMeasuredCaption()
    {
        var window = AppSourceTree.LoadXaml("MainWindow.xaml");
        var expand = window.Descendants(Ns + "Button").Single(b => (string?)b.Attribute(X + "Name") == "CompactExpandButton");
        var strip = expand.Parent!;
        Assert.Equal(2, strip.Element(Ns + "Grid.ColumnDefinitions")!.Elements(Ns + "ColumnDefinition").Count());
        Assert.DoesNotContain(window.Descendants(Ns + "ColumnDefinition"), c => (string?)c.Attribute(X + "Name") == "CompactCaptionColumn");
        Assert.Equal("Top", (string?)expand.Attribute("VerticalAlignment"));

        var spacing = AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml").Root!.Elements(Ns + "Thickness")
            .ToDictionary(t => (string)t.Attribute(X + "Key")!, t => t.Value.Trim().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray());
        var margin = ((string)expand.Attribute("Margin")!).Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(spacing["SaCompactBodyPadding"][2], margin[2]); // right edge = the content's right edge (W - 20)
        Assert.Equal(spacing["SaCompactTitlePadding"][1] + ServerMonitor.App.Windowing.TitleBarInsetCalculator.ButtonInsetOnStripLine, margin[1]);
        Assert.Equal("{StaticResource SaCompactTitlePadding}", (string?)window.Descendants(Ns + "Grid")
            .Single(g => (string?)g.Attribute(X + "Name") == "CompactDragRegion").Attribute("Padding"));

        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("_placementAdapter.GetCaptionHeight()", code, StringComparison.Ordinal);
        Assert.Contains("TitleBarInsetCalculator.ToTitleStripTopDips(height, xamlRoot.RasterizationScale)", code, StringComparison.Ordinal);
        Assert.Contains("CompactRoot.ActualWidth - CompactDragRegion.Padding.Left - CompactExpandButton.Margin.Right", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCaptionRightInset", code, StringComparison.Ordinal);
    }

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Ns + "Setter").FirstOrDefault(setter => (string?)setter.Attribute("Property") == property)?.Attribute("Value");

    private static Dictionary<string, double> RadiusTiers() =>
        AppSourceTree.LoadXaml("Styles/Tokens/Radius.xaml").Root!.Elements(Ns + "CornerRadius")
            .Where(element => ((string?)element.Attribute(X + "Key"))?.StartsWith("SaRadiusButton", StringComparison.Ordinal) == true)
            .ToDictionary(element => (string)element.Attribute(X + "Key")!, element => double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture));

    private static Dictionary<string, XElement> ButtonStyles() =>
        ButtonStyleDictionaries
            .SelectMany(file => AppSourceTree.LoadXaml(file).Root!.Elements(Ns + "Style"))
            .Where(style => (string?)style.Attribute("TargetType") == "Button")
            .ToDictionary(style => (string)style.Attribute(X + "Key")!, style => style, StringComparer.Ordinal);

    private static string? Effective(IReadOnlyDictionary<string, XElement> styles, string key, string property)
    {
        for (var current = key; current is not null && styles.TryGetValue(current, out var style); current = StyleKey((string?)style.Attribute("BasedOn")))
        {
            var setter = style.Elements(Ns + "Setter").FirstOrDefault(element => (string?)element.Attribute("Property") == property);
            if (setter is not null)
                return (string?)setter.Attribute("Value");
        }

        return null;
    }

    private static string? StyleKey(string? reference) =>
        reference is null ? null : StaticResourceKey().Match(reference) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^\{StaticResource\s+(\w+)\}$")]
    private static partial Regex StaticResourceKey();
}
