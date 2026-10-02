using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.4 phase 2: structural, accessibility and localization contracts of the Visão geral, Servidores and interim server
/// pages, read from the XAML (no XAML runtime). The runtime counterpart is the UIA screenshot pass via Start-QaApp.ps1.
/// </summary>
public sealed partial class Ui4PageContractTests
{
    private const string Overview = "Views/DashboardPage.xaml";
    private const string Servers = "Views/ServersPage.xaml";
    private const string Detail = "Views/ServerDetailPage.xaml";
    private static readonly string[] Cultures = ["pt-PT", "pt-BR", "en-US"];
    private const string AutomationName = "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static IEnumerable<XElement> Elements(string file) => AppSourceTree.LoadXaml(file).Descendants();

    private static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    private static string? Uid(XElement e) => (string?)e.Attribute(AppSourceTree.Xaml + "Uid");

    private static IReadOnlyDictionary<string, string> Resw(string culture) =>
        XDocument.Load(Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.App", "Resources", culture, "Resources.resw"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    [Theory]
    [InlineData(Overview)]
    [InlineData(Servers)]
    [InlineData(Detail)]
    public void EveryXUidResolvesInEveryCulture(string file)
    {
        var resources = Cultures.ToDictionary(c => c, Resw);
        var failures = new List<string>();
        foreach (var uid in Elements(file).Select(Uid).Where(u => u is not null).Distinct())
        {
            var keys = resources["pt-PT"].Keys.Where(k => k.StartsWith(uid + ".", StringComparison.Ordinal)).ToList();
            if (keys.Count == 0)
            {
                failures.Add($"{file}: x:Uid '{uid}' has no pt-PT key");
            }

            foreach (var culture in Cultures)
            {
                failures.AddRange(keys
                    .Where(k => !resources[culture].TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v))
                    .Select(k => $"{file}: {culture} lacks '{k}'"));
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData(Overview)]
    [InlineData(Servers)]
    [InlineData(Detail)]
    public void Pages_UseOnlyTokens_NoLiteralFontSizeHexOrGeometry(string file)
    {
        var offenders = Elements(file)
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName is "FontSize" or "CornerRadius" or "Padding" && !a.Value.StartsWith('{')
                        || HexColour().IsMatch(a.Value))
            .Select(a => $"{a.Parent!.Name.LocalName}.{a.Name.LocalName}=\"{a.Value}\"")
            .ToList();
        var setterOffenders = Elements(file)
            .Where(e => e.Name.LocalName == "Setter" && (Attr(e, "Target") ?? Attr(e, "Property") ?? string.Empty) is var t
                        && (t.EndsWith(".Padding", StringComparison.Ordinal) || t.EndsWith(".CornerRadius", StringComparison.Ordinal)
                            || t.EndsWith(".FontSize", StringComparison.Ordinal) || t is "Padding" or "CornerRadius" or "FontSize")
                        && !(Attr(e, "Value") ?? string.Empty).StartsWith('{'))
            .Select(e => $"Setter {Attr(e, "Target") ?? Attr(e, "Property")}={Attr(e, "Value")}");

        Assert.Empty(offenders.Concat(setterOffenders));
    }

    /// <summary>Prism r1: no horizontal scroll at any width (560×640 included).</summary>
    [Theory]
    [InlineData(Overview)]
    [InlineData(Servers)]
    [InlineData(Detail)]
    public void EveryScrollViewer_ScrollsVerticallyOnly(string file)
    {
        var viewers = Elements(file).Where(e => e.Name.LocalName == "ScrollViewer").ToList();

        Assert.NotEmpty(viewers);
        Assert.All(viewers, v => Assert.Equal("Disabled", Attr(v, "HorizontalScrollBarVisibility")));
    }

    [Theory]
    [InlineData(Overview, "OverviewTitle")]
    [InlineData(Servers, "ServersPageTitleText")]
    public void TheTitleIsTheOnlyLevel1Heading(string file, string titleUid)
    {
        var level1 = Elements(file).Where(e => Attr(e, "AutomationProperties.HeadingLevel") == "Level1").ToList();

        Assert.Equal(titleUid, Uid(Assert.Single(level1)));
    }

    /// <summary>Every icon-only button carries an accessible name (x:Uid key or binding), never just a glyph (T-17).</summary>
    [Theory]
    [InlineData(Overview)]
    [InlineData(Servers)]
    public void IconOnlyButtons_AreNamed(string file)
    {
        var ptPt = Resw("pt-PT");
        var unnamed = Elements(file)
            .Where(e => e.Name.LocalName == "Button" && Attr(e, "Content") is null && Uid(e) is var uid
                        && !(uid is not null && (ptPt.ContainsKey(uid + "." + AutomationName) || ptPt.ContainsKey(uid + ".Content")))
                        && Attr(e, "AutomationProperties.Name") is null)
            .Select(e => e.ToString().Split('\n')[0])
            .ToList();

        Assert.True(unnamed.Count == 0, string.Join(Environment.NewLine, unnamed));
    }

    /// <summary>The summary list and the table are named UIA lists, one Tab stop, arrows walk the rows.</summary>
    [Theory]
    [InlineData(Overview, "OverviewServerList")]
    [InlineData(Servers, "ServersTable")]
    public void ServerLists_AreNamedUiaLists_OneTabStop(string file, string uid)
    {
        var list = Assert.Single(Elements(file), e => e.Name.LocalName == "SaDataTableList" && Uid(e) == uid);
        Assert.True(Resw("pt-PT").ContainsKey(uid + "." + AutomationName));
        var repeater = Assert.Single(list.Elements(), e => e.Name.LocalName == "ItemsRepeater");
        Assert.Equal("Once", Attr(repeater, "TabFocusNavigation"));
        Assert.Equal("Enabled", Attr(repeater, "XYFocusKeyboardNavigation"));
    }

    /// <summary>
    /// Prism r1 responsive: Wide/Mid/Stacked share ONE virtualized ItemsRepeater — the states swap its template, never the
    /// container — and every row is one target named "Ver detalhe de &lt;servidor&gt;" with the full row as help text.
    /// </summary>
    [Fact]
    public void Servers_OneRepeater_TemplatesSwappedByState_RowsNamedByTheirAction()
    {
        var elements = Elements(Servers).ToList();
        Assert.Single(elements, e => e.Name.LocalName == "ItemsRepeater");
        var templateSetters = elements.Where(e => e.Name.LocalName == "Setter" && Attr(e, "Target") == "ServersRepeater.ItemTemplate")
            .Select(e => Attr(e, "Value")).ToList();
        Assert.Contains("{StaticResource ServerRowMidTemplate}", templateSetters);
        Assert.Contains("{StaticResource ServerRowStackedTemplate}", templateSetters);

        var rowButtons = elements.Where(e => e.Name.LocalName == "ServerTableRowButton" && Attr(e, "Style") == "{StaticResource ServerRowButtonStyle}").ToList();
        Assert.Equal(3, rowButtons.Count);
        Assert.All(rowButtons, b =>
        {
            Assert.Equal("{x:Bind DetailAutomationName}", Attr(b, "AutomationProperties.Name"));
            Assert.Equal("{x:Bind RowAutomationName, Mode=OneWay}", Attr(b, "AutomationProperties.HelpText"));
        });
    }

    /// <summary>The mDNS suggestions live in ONE template, used under the list and inside the empty state (UI.0: no inline copy).</summary>
    [Fact]
    public void Overview_DiscoveryHasOneTemplate_UsedByTheSectionAndTheEmptyState()
    {
        var elements = Elements(Overview).ToList();
        Assert.Single(elements, e => e.Name.LocalName == "DataTemplate" && Attr(e, AppSourceTree.Xaml + "Key") == "DiscoveredRowTemplate");
        Assert.Equal(2, elements.Count(e => Attr(e, "ItemTemplate") == "{StaticResource DiscoveredRowTemplate}"));
        Assert.DoesNotContain(elements, e => e.Name.LocalName == "DiscoveredServerCard");
    }

    [Fact]
    public void PtPt_PageCopy_UsesTu()
    {
        var ptPt = Resw("pt-PT");
        var uids = Elements(Overview).Concat(Elements(Servers)).Select(Uid).Where(u => u is not null).ToHashSet(StringComparer.Ordinal);
        var offenders = ptPt.Where(kv => uids.Any(u => kv.Key.StartsWith(u + ".", StringComparison.Ordinal)) && YouForm().IsMatch(kv.Value))
            .Select(kv => $"{kv.Key} = {kv.Value}").ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static string? Attr(XElement e, XName name) => (string?)e.Attribute(name);

    [GeneratedRegex(@"#[0-9A-Fa-f]{6,8}\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\b(Verifique|Experimente|Tente|Volte|Remova|Adicione|Restaure|sua|seus|suas)\b")]
    private static partial Regex YouForm();
}
