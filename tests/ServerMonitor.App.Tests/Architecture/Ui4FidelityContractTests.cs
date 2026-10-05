using System.Xml.Linq;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.4 fidelity r1 (Prism, .boss/tmp/ui4/prism/fidelity-r1.md) + Cortex r3 NIT-1: the Figma values as XAML/token
/// contracts (no XAML runtime), and the single inclusive threshold comparison of the App held equal to the Core's.
/// </summary>
public sealed class Ui4FidelityContractTests
{
    private const string Overview = "Views/DashboardPage.xaml";
    private const string Servers = "Views/ServersPage.xaml";

    private static List<XElement> Elements(string file) => AppSourceTree.LoadXaml(file).Descendants().ToList();

    private static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    private static string? Name(XElement e) => (string?)e.Attribute(AppSourceTree.Xaml + "Name");

    private static string? Key(XElement e) => (string?)e.Attribute(AppSourceTree.Xaml + "Key");

    // ---- tokens -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SaFontSizeHealthCount", "36")]
    [InlineData("SaFontSizeHealthTotal", "22")]
    [InlineData("SaFontSizeSectionTitle", "19")]
    [InlineData("SaFontSizeListRowTitle", "15")]
    [InlineData("SaFontSizeTitle", "20")] // B3 decision: the shared title stays 20 (UI.3 Serviços e containers)
    public void TypographyTokens_HaveTheFigmaValues(string key, string value) =>
        Assert.Equal(value, Assert.Single(Elements("Styles/Tokens/Typography.xaml"), e => Key(e) == key).Value);

    [Fact]
    public void TheNewStyles_HaveTheFigmaWeights()
    {
        var typography = Elements("Styles/Tokens/Typography.xaml");
        string Weight(string style) => Attr(Assert.Single(
            Assert.Single(typography, e => Key(e) == style).Elements(),
            s => Attr(s, "Property") == "FontWeight"), "Value")!;

        Assert.Equal("Light", Weight("SaHealthCountTextStyle"));
        Assert.Equal("Normal", Weight("SaHealthTotalTextStyle"));
        Assert.Equal("SemiBold", Weight("SaSectionTitleTextStyle"));
    }

    /// <summary>B4: the "Saúde geral" dot is the neutral #A6A6A6 in Dark AND Light (HC: window text).</summary>
    [Fact]
    public void HealthLabelDot_IsNeutralInBothThemes()
    {
        Assert.Equal("#A6A6A6", Assert.Single(Elements("Styles/Tokens/Color.Primitives.xaml"), e => Key(e) == "SaColorHealthLabelDot").Value);
        var brushes = Elements("Styles/Tokens/Color.Semantic.xaml").Where(e => Key(e) == "SaHealthLabelDotBrush").Select(e => Attr(e, "Color")).ToList();
        Assert.Equal(
            ["{StaticResource SaColorHealthLabelDot}", "{StaticResource SaColorHealthLabelDot}", "{ThemeResource SystemColorWindowTextColor}"],
            brushes);
    }

    // ---- overview ---------------------------------------------------------------------------------------------

    /// <summary>B5: segments right after the text — Auto · Auto · spacer 75 · Auto(bar), the bar left-aligned in column 3.</summary>
    [Fact]
    public void HealthCountRow_PutsTheBarAfterTheTextBehindA75Spacer()
    {
        var elements = Elements(Overview);
        var row = Assert.Single(elements, e => Name(e) == "HealthCountRow");
        var columns = row.Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").Select(c => Attr(c, "Width")).ToList();
        Assert.Equal(["Auto", "Auto", "75", "Auto"], columns);

        var bar = Assert.Single(elements, e => Name(e) == "HealthSegments");
        Assert.Equal("3", Attr(bar, "Grid.Column"));
        Assert.Equal("Left", Attr(bar, "HorizontalAlignment"));
        Assert.DoesNotContain(elements, e => e.Name.LocalName == "Setter" && Attr(e, "Target") == "HealthSegments.HorizontalAlignment");
    }

    /// <summary>B6: both health/priority cards (and the neutral priority cards) are 156 high, so they line up.</summary>
    [Fact]
    public void TheHealthAndPriorityCards_Are156High()
    {
        var elements = Elements(Overview);
        var healthCard = Assert.Single(elements, e => e.Name.LocalName == "SaFocusableCard").Elements().Single();
        Assert.Equal("156", Attr(healthCard, "MinHeight"));

        var host = Assert.Single(elements, e => Name(e) == "PriorityHost");
        var cards = host.Elements().ToList();
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal("156", Attr(c, "MinHeight")));
    }

    /// <summary>
    /// Prism r2 R2-B1: the health card is EXACTLY 156 (it was 159): its count row is 47 and nothing in it may be taller —
    /// the 50-high count line overflows 1.5 above and below (Figma 112:1015), the "de N" line (31) and the bar fit.
    /// 24 + 20 + 8 + 47 + 8 + 23 + 24 + 2 = 156.
    /// </summary>
    [Fact]
    public void TheHealthCountRow_Is47_SoTheHealthCardIsExactly156()
    {
        var elements = Elements(Overview);
        var typography = Elements("Styles/Tokens/Typography.xaml");
        double Token(string key) => double.Parse(Assert.Single(typography, e => Key(e) == key).Value, System.Globalization.CultureInfo.InvariantCulture);

        var row = Assert.Single(elements, e => Name(e) == "HealthCountRow");
        Assert.Equal("47", Attr(row, "MinHeight"));
        Assert.Null(Attr(row, "Height"));

        var count = Assert.Single(elements, e => Name(e) == "HealthCountText");
        Assert.Equal("{StaticResource SaHealthCountTextStyle}", Attr(count, "Style"));
        var margin = Attr(count, "Margin")!.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(47, Token("SaLineHeightHealthCount") + margin[1] + margin[3]);
        Assert.True(Token("SaLineHeightHealthTotal") <= 47);
    }

    /// <summary>B7 + B9: the summary list uses the prominent row (15) and its state at 13; the table keeps 12.</summary>
    [Fact]
    public void SummaryList_RowTitle15_State13()
    {
        var template = Assert.Single(Elements(Overview), e => Key(e) == "OverviewServerRowTemplate");
        var row = Assert.Single(template.Descendants(), e => e.Name.LocalName == "SaListRow");
        Assert.Equal("{StaticResource SaListRowProminentStyle}", Attr(row, "Style"));
        var state = Assert.Single(template.Descendants(), e => e.Name.LocalName == "SaStatusIndicator");
        Assert.Equal("{StaticResource SaFontSizeControl}", Attr(state, "FontSize"));
        Assert.DoesNotContain(Elements(Servers), e => e.Name.LocalName == "SaStatusIndicator" && Attr(e, "FontSize") is not null);
    }

    /// <summary>B7/B9 primitives: the label/title follow the control's FontSize; the UI.2 defaults stay 12 / 14.</summary>
    [Fact]
    public void Primitives_TakeTheirTextSizeFromTheControl_WithTheUi2Defaults()
    {
        var primitives = Elements("Styles/Components/Sa.Primitives.xaml");
        string? Setter(string style, string property) => Attr(Assert.Single(
            Assert.Single(primitives, e => Key(e) == style).Elements(), s => Attr(s, "Property") == property), "Value");

        Assert.Equal("{StaticResource SaFontSizeCaption}", Setter("SaStatusIndicatorStyle", "FontSize"));
        Assert.Equal("{StaticResource SaFontSizeButton}", Setter("SaListRowStyle", "FontSize"));
        Assert.Equal("{StaticResource SaFontSizeListRowTitle}", Setter("SaListRowProminentStyle", "FontSize"));
        Assert.Contains(primitives, e => Name(e) == "PART_Label" && Attr(e, "FontSize") == "{TemplateBinding FontSize}");
        Assert.Contains(primitives, e => Key(e) is null && e.Name.LocalName == "Style" && Attr(e, "BasedOn") == "{StaticResource SaListRowStyle}");
    }

    /// <summary>B3 (Boss): the UI.4 section titles use the NEW 19 style; the shared 20 style is untouched.</summary>
    [Fact]
    public void SectionTitles_Use19_TheSharedStyleIsUntouched()
    {
        var titles = Elements(Overview).Where(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") is "OverviewServersTitle" or "DashboardDiscoveryTitle").ToList();
        Assert.Equal(2, titles.Count);
        Assert.All(titles, t => Assert.Equal("{StaticResource SaSectionTitleTextStyle}", Attr(t, "Style")));
        Assert.DoesNotContain(Elements(Overview), e => Attr(e, "Style") == "{StaticResource SaSectionHeaderTextStyle}");

        var shared = Assert.Single(Elements("Styles/Components/Sa.Text.xaml"), e => Key(e) == "SaSectionHeaderTextStyle");
        Assert.Equal("{StaticResource SaTitleTextStyle}", Attr(shared, "BasedOn"));
    }

    [Fact]
    public void HealthLabelDot_UsesTheNeutralToken()
    {
        var label = Assert.Single(Elements(Overview), e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == "OverviewHealthLabel");
        var dot = Assert.Single(label.Parent!.Elements(), e => e.Name.LocalName == "Ellipse");
        Assert.Equal("{ThemeResource SaHealthLabelDotBrush}", Attr(dot, "Fill"));
    }

    /// <summary>C3: from 600 to 639 the header actions stay beside the title; only below 600 they wrap.</summary>
    [Fact]
    public void HeaderActions_WrapOnlyBelow600()
    {
        var states = Elements(Overview).Where(e => e.Name.LocalName == "VisualState").ToList();
        var narrowWide = Assert.Single(states, s => Name(s) == "NarrowWide");
        Assert.Contains(narrowWide.Descendants(), e => e.Name.LocalName == "SaContentWidthTrigger" && Attr(e, "MinWidth") == "600");
        Assert.DoesNotContain(narrowWide.Descendants(), e => Attr(e, "Target")?.StartsWith("HeaderActions.", StringComparison.Ordinal) == true);
        Assert.Contains(Assert.Single(states, s => Name(s) == "Narrow").Descendants(), e => Attr(e, "Target") == "HeaderActions.(Grid.Row)");
    }

    // ---- servidores ------------------------------------------------------------------------------------------

    /// <summary>B8: "Adicionar" stays beside the title at every width (112:1420): no state moves it.</summary>
    [Fact]
    public void Servers_AddStaysBesideTheTitle_AtEveryWidth()
    {
        var elements = Elements(Servers);
        Assert.DoesNotContain(elements, e => e.Name.LocalName == "Setter" && Attr(e, "Target")?.StartsWith("AddButton.", StringComparison.Ordinal) == true);
        var add = Assert.Single(elements, e => Name(e) == "AddButton");
        Assert.Equal("1", Attr(add, "Grid.Column"));
    }

    // ---- Cortex r3 NIT-1: parity with the Core ------------------------------------------------------------------

    /// <summary>
    /// The App's single inclusive comparison equals the engine's: for each metric alone in a snapshot,
    /// <see cref="OverviewPresentation.MetricSeverity"/> == <see cref="HealthEvaluator.EvaluateFromMetrics"/> around every
    /// boundary. The one documented difference: an unknown (null/NaN) metric is "Healthy" (no colour, no candidate) for
    /// the App and "Unknown" for the engine's overall health — both mean "not above a limit".
    /// </summary>
    [Fact]
    public void MetricSeverity_HasParityWithTheCoreHealthEvaluator()
    {
        var thresholds = MonitoringThresholds.Default;
        var cases = new (string Metric, double Warning, double Critical, Func<double?, ServerMetricsSnapshot> Snapshot)[]
        {
            ("cpu", thresholds.CpuWarning, thresholds.CpuCritical, v => Snapshot(cpu: v)),
            ("memory", thresholds.MemoryWarning, thresholds.MemoryCritical, v => Snapshot(mem: v)),
            ("disk", thresholds.DiskWarning, thresholds.DiskCritical, v => Snapshot(disk: v))
        };

        foreach (var (metric, warning, critical, snapshot) in cases)
        {
            foreach (var value in new[] { 0, warning - 1e-9, warning, warning + 1e-9, critical - 1e-9, critical, critical + 1e-9, 100 })
            {
                Assert.True(
                    HealthEvaluator.EvaluateFromMetrics(snapshot(value), thresholds) == OverviewPresentation.MetricSeverity(value, warning, critical),
                    $"{metric} at {value}");
            }

            Assert.Equal(ServerHealth.Unknown, HealthEvaluator.EvaluateFromMetrics(snapshot(double.NaN), thresholds));
            Assert.Equal(ServerHealth.Healthy, OverviewPresentation.MetricSeverity(double.NaN, warning, critical));
            Assert.Equal(ServerHealth.Unknown, HealthEvaluator.EvaluateFromMetrics(snapshot(null), thresholds));
            Assert.Equal(ServerHealth.Healthy, OverviewPresentation.MetricSeverity(null, warning, critical));
        }
    }

    private static ServerMetricsSnapshot Snapshot(double? cpu = null, double? mem = null, double? disk = null) => new()
    {
        ServerId = Guid.Empty,
        CollectedAt = DateTimeOffset.UnixEpoch,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = mem,
        DiskUsagePercent = disk
    };
}
