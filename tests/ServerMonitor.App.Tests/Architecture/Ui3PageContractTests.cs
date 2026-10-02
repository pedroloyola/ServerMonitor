using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.3 phase 2: structural, accessibility and localization contracts of the migrated History and Workloads pages,
/// read from the XAML (no XAML runtime). The runtime counterpart is the UIA screenshot pass.
/// </summary>
public sealed partial class Ui3PageContractTests
{
    private const string History = "Views/HistoryPage.xaml";
    private const string Workloads = "Views/WorkloadsPage.xaml";
    private static readonly string[] Cultures = ["pt-PT", "pt-BR", "en-US"];

    private static IEnumerable<XElement> Elements(string file) => AppSourceTree.LoadXaml(file).Descendants();

    private static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    private static string? Uid(XElement e) => (string?)e.Attribute(AppSourceTree.Xaml + "Uid");

    private static IReadOnlyDictionary<string, string> Resw(string culture) =>
        XDocument.Load(Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.App", "Resources", culture, "Resources.resw"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    // --- History -------------------------------------------------------------------------------------------------

    [Fact]
    public void History_TitleIsTheLevel1Heading_AndEachChartCardHasALevel2Heading()
    {
        var title = Assert.Single(Elements(History), e => Uid(e) == "HistoryPageTitle");
        Assert.Equal("Level1", Attr(title, "AutomationProperties.HeadingLevel"));
        Assert.Equal(3, Elements(History).Count(e => Attr(e, "AutomationProperties.HeadingLevel") == "Level2"));
    }

    [Theory]
    [InlineData("CpuChart", "Cpu", "SaCpuBrush")]
    [InlineData("MemoryChart", "Memory", "SaMemoryBrush")]
    [InlineData("DiskChart", "Disk", "SaDiskBrush")]
    public void History_ChartsAreNamedByTheirTextSummary_AndDrawnInTheNeutralLine(string name, string metric, string metricBrush)
    {
        var chart = Assert.Single(Elements(History), e => e.Name.LocalName == "HistoryChart" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name);

        // Beacon F3: the CARD around the chart is the keyboard stop and carries the text alternative - current + peak +
        // period (HistoryViewModel.*Summary), never colour alone; the drawing itself is Raw (no duplicate announcement).
        var card = chart.Ancestors().First(e => e.Name.LocalName == "SaFocusableCard");
        Assert.Equal($"{{Binding {metric}Summary}}", Attr(card, "AutomationProperties.Name"));
        Assert.Equal("{StaticResource SaChartCardStyle}", Attr(card.Elements().Single(), "Style"));
        Assert.Equal("Raw", Attr(chart, "AutomationProperties.AccessibilityView"));
        Assert.Null(Attr(chart, "AutomationProperties.Name"));
        Assert.Equal($"{{Binding {metric}Series}}", Attr(chart, "Series"));
        // D-UI3-1 (human decision, 05 Free 112:2299 / 112:2476): neutral line, never the metric colour; the visible
        // "CPU/Memória/Disco" title names the series. Area gradient and end marker derive from LineBrush.
        Assert.Equal("{ThemeResource SaChartLineBrush}", Attr(chart, "LineBrush"));
        Assert.NotEqual($"{{ThemeResource {metricBrush}}}", Attr(chart, "LineBrush"));
        Assert.Equal("{StaticResource SaChartLineThickness}", Attr(chart, "LineThickness"));
        Assert.Equal("{Binding XAxisTicks}", Attr(chart, "XTicks"));                   // D-UI3-4 (round marks, real X)
        Assert.Equal("{Binding XAxisTicksCompact}", Attr(chart, "XTicksCompact"));
        Assert.Equal("{Binding YAxisLabels}", Attr(chart, "YLabels"));
    }

    [Fact]
    public void History_NoChartUsesAMetricColour()
    {
        var brushes = Elements(History).Where(e => e.Name.LocalName == "HistoryChart").Select(c => Attr(c, "LineBrush")).ToList();
        Assert.Equal(3, brushes.Count);
        Assert.All(brushes, b => Assert.Equal("{ThemeResource SaChartLineBrush}", b));
    }

    [Theory]
    [InlineData("Dark", "{StaticResource SaColorChartLineDark}")]
    [InlineData("Light", "{StaticResource SaColorChartLineLight}")]
    [InlineData("HighContrast", "{ThemeResource SystemColorWindowTextColor}")]
    public void ChartLineToken_HasTheFigmaValueInEveryTheme_AndTheMetricColoursAreUntouched(string theme, string expected)
    {
        var semantic = AppSourceTree.LoadXaml("Styles/Tokens/Color.Semantic.xaml").Descendants()
            .Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == theme)
            .Elements().ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, e => Attr(e, "Color"), StringComparer.Ordinal);
        Assert.Equal(expected, semantic["SaChartLineBrush"]);

        var colours = AppSourceTree.LoadXaml("Styles/Tokens/Color.Primitives.xaml").Root!.Elements()
            .Where(e => e.Name.LocalName == "Color").ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, e => e.Value.Trim(), StringComparer.Ordinal);
        Assert.Equal("#D2D6D4", colours["SaColorChartLineDark"]);
        Assert.Equal("#565C59", colours["SaColorChartLineLight"]);

        // The metric brushes keep their Manual values and stay available for the design system / other contexts.
        foreach (var metric in new[] { "Cpu", "Memory", "Disk" })
        {
            Assert.Equal(theme == "HighContrast" ? "{ThemeResource SystemColorWindowTextColor}" : $"{{StaticResource SaColor{metric}{theme}}}", semantic[$"Sa{metric}Brush"]);
        }

        Assert.Equal(("#B69AF8", "#8668CA"), (colours["SaColorCpuDark"], colours["SaColorCpuLight"]));
        Assert.Equal(("#7DB8FF", "#427EC5"), (colours["SaColorMemoryDark"], colours["SaColorMemoryLight"]));
        Assert.Equal(("#FFC16E", "#B87B2F"), (colours["SaColorDiskDark"], colours["SaColorDiskLight"]));
    }

    [Fact]
    public void History_RangeIsOneKeyboardGroupOfFiveSegments_BoundToTheSelectedIndex()
    {
        var group = Assert.Single(Elements(History), e => Uid(e) == "HistoryRangeSelector");
        Assert.Equal("SelectionFollowsFocus", group.Attributes().First(a => a.Name.LocalName == "SaGroupNavigation.Mode").Value);
        var segments = group.Elements().Where(e => e.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(5, segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.Equal("{StaticResource SaSegmentedRectItemStyle}", Attr(segments[i], "Style"));
            Assert.Contains($"ConverterParameter={i}", Attr(segments[i], "IsChecked"), StringComparison.Ordinal);
            Assert.Contains("SelectedRangeIndex, Mode=TwoWay", Attr(segments[i], "IsChecked"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void History_ServerSelectorIsTheRichSelector_BoundToTheViewModel()
    {
        var selector = Assert.Single(Elements(History), e => Uid(e) == "HistoryServerSelector");
        Assert.Equal("{StaticResource SaSelectorRichStyle}", Attr(selector, "Style"));
        Assert.Equal("{Binding Servers}", Attr(selector, "ItemsSource"));
        Assert.Equal("{Binding SelectedServer, Mode=TwoWay}", Attr(selector, "SelectedItem"));
    }

    // --- Workloads -----------------------------------------------------------------------------------------------

    [Fact]
    public void Workloads_IsReadOnly_TheOnlyCommandsAreRefreshClearAndBack()
    {
        var commands = Elements(Workloads)
            .Select(e => Attr(e, "Command"))
            .Where(c => c is not null)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "{Binding ClearSearchCommand}", "{Binding RefreshCommand}" }, commands);
        Assert.DoesNotContain(Elements(Workloads), e => e.Name.LocalName is "MenuFlyout" or "MenuFlyoutItem" or "AppBarButton");
    }

    [Fact]
    public void Workloads_RowsAreCompactRowsWithTheDotOnlyStatus_AndNamedForUiAutomation()
    {
        foreach (var template in new[] { "ContainerRowTemplate", "ServiceRowTemplate" })
        {
            var root = Assert.Single(Elements(Workloads), e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == template);
            var row = root.Elements().Single();
            // Difference 14 (Boss): a focusable, read-only SaDataTableRow (UIA ListItem, no pattern) named by the full row.
            Assert.Equal("SaDataTableRow", row.Name.LocalName);
            Assert.Equal("{x:Bind DisplayAutomationName}", Attr(row, "AutomationProperties.Name"));
            Assert.DoesNotContain(row.DescendantsAndSelf().SelectMany(e => e.Attributes()),
                a => a.Name.LocalName is "Command" or "Tapped" or "Click" or "DoubleTapped" or "IsItemClickEnabled");
            var dot = Assert.Single(row.Descendants(), e => e.Name.LocalName == "SaStatusIndicator");
            Assert.Equal("{StaticResource SaStatusDotOnlyStyle}", Attr(dot, "Style"));
            Assert.False(string.IsNullOrWhiteSpace(Attr(dot, "Label")));
            // State is always text: the two right-hand lines exist and are styled by severity (never colour only).
            Assert.Equal(2, row.Descendants().Count(e => Attr(e, "Style")?.Contains("WorkloadSeverityToSaTextStyleConverter", StringComparison.Ordinal) == true));
        }
    }

    [Theory]
    [InlineData("WorkloadContainersList", "{Binding Containers}", "{StaticResource ContainerRowTemplate}")]
    [InlineData("WorkloadServicesList", "{Binding Services}", "{StaticResource ServiceRowTemplate}")]
    public void Workloads_ListsAreNamedUiaLists_OneTabStop_ArrowsWalkTheRows(string uid, string items, string template)
    {
        // Difference 14 (Boss) / Cortex M-2: a named UIA List (SaDataTableList, not a tab stop) around the virtualized
        // ItemsRepeater; Tab enters once, the arrows move between the focusable rows; the repeater adds no UIA level.
        var list = Assert.Single(Elements(Workloads), e => Uid(e) == uid);
        Assert.Equal("SaDataTableList", list.Name.LocalName);
        var repeater = Assert.Single(list.Elements());
        Assert.Equal("ItemsRepeater", repeater.Name.LocalName);   // virtualized: 2000 rows stay cheap
        Assert.Equal("Once", Attr(repeater, "TabFocusNavigation"));
        Assert.Equal("Enabled", Attr(repeater, "XYFocusKeyboardNavigation"));
        Assert.Equal("Raw", Attr(repeater, "AutomationProperties.AccessibilityView"));
        Assert.Equal(items, Attr(repeater, "ItemsSource"));
        Assert.Equal(template, Attr(repeater, "ItemTemplate"));
        Assert.All(Cultures, c => Assert.False(string.IsNullOrWhiteSpace(Resw(c)[uid + ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"])));
    }

    [Fact]
    public void Workloads_StackedLayoutsScrollThePage_NeverTheCards()
    {
        // Cortex M-3 (difference 11 not accepted): Medium/Narrow = one page ScrollViewer, cards Height=Auto, no inner
        // scrolling; Wide (Figma) keeps the page fixed and the lists scrolling inside their cards.
        var page = Assert.Single(Elements(Workloads), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PageScroll");
        Assert.Equal("Disabled", Attr(page, "VerticalScrollMode"));
        var states = Elements(Workloads).Where(e => e.Name.LocalName == "VisualState").ToDictionary(s => (string)s.Attribute(AppSourceTree.Xaml + "Name")!);
        Assert.Empty(states["Wide"].Descendants().Where(e => e.Name.LocalName == "Setter"));
        foreach (var state in new[] { "Medium", "Narrow" })
        {
            var setters = states[state].Descendants().Where(e => e.Name.LocalName == "Setter")
                .ToDictionary(s => (string)s.Attribute("Target")!, s => (string)s.Attribute("Value")!);
            Assert.Equal("Enabled", setters["PageScroll.VerticalScrollMode"]);
            Assert.Equal("Disabled", setters["ContainersScroll.VerticalScrollMode"]);
            Assert.Equal("Disabled", setters["ServicesScroll.VerticalScrollMode"]);
            Assert.Equal("Auto", setters["CardsRow1.Height"]);
            Assert.Equal("Auto", setters["CardsRow2.Height"]);
            Assert.Equal("0", setters["CardsGrid.ColumnSpacing"]);          // stacked: no phantom column gap
        }

        var cards = Assert.Single(Elements(Workloads), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "CardsGrid");
        Assert.Null(Attr(cards, "RowSpacing"));                                // side by side: no phantom row gap
    }

    [Fact]
    public void BeaconL1_CtasThatDisappear_MoveFocusToWhatTheyChanged()
    {
        var thirtyDays = Assert.Single(Elements(History), e => Attr(e, "Command") == "{Binding ViewLast30DaysCommand}");
        Assert.Equal("OnViewLast30DaysClick", Attr(thirtyDays, "Click"));
        Assert.Contains("FocusAfterAction.MoveTo((Control)sender, RangeLast30Days)", AppSourceTree.CodeWithoutComments("Views/HistoryPage.xaml.cs"), StringComparison.Ordinal);
        var range = Assert.Single(Elements(History), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "RangeLast30Days");
        Assert.Equal("4", Attr(range, "IsChecked")!.Split("ConverterParameter=")[1].TrimEnd('}'));

        var clear = Assert.Single(Elements(Workloads), e => Uid(e) == "WorkloadClearSearchButton");
        Assert.Equal("OnClearSearchClick", Attr(clear, "Click"));
        Assert.Contains("FocusAfterAction.MoveTo((Control)sender, SearchBox)", AppSourceTree.CodeWithoutComments("Views/WorkloadsPage.xaml.cs"), StringComparison.Ordinal);

        // keyboard activation keeps a visible ring; pointer / UIA activation moves focus without one
        Assert.Equal(Microsoft.UI.Xaml.FocusState.Keyboard, ServerMonitor.App.Views.FocusAfterAction.FocusStateFor(Microsoft.UI.Xaml.FocusState.Keyboard));
        Assert.Equal(Microsoft.UI.Xaml.FocusState.Programmatic, ServerMonitor.App.Views.FocusAfterAction.FocusStateFor(Microsoft.UI.Xaml.FocusState.Pointer));
        Assert.Equal(Microsoft.UI.Xaml.FocusState.Programmatic, ServerMonitor.App.Views.FocusAfterAction.FocusStateFor(Microsoft.UI.Xaml.FocusState.Unfocused));
    }

    [Fact]
    public void BeaconL3_StateTitlesAreHeadings_PageLevel2_SectionLevel3()
    {
        var template = Assert.Single(AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants(),
            e => e.Name.LocalName == "ControlTemplate" && Attr(e, "TargetType") == "primitives:SaEmptyState");
        Assert.Contains(template.Descendants(), e => Attr(e, "AutomationProperties.HeadingLevel") == "{TemplateBinding TitleHeadingLevel}");

        var workloads = Elements(Workloads).ToList();   // one document: Except compares element instances
        var sectionStates = workloads.Where(e => e.Name.LocalName == "SaEmptyState"
                                                            && (Uid(e)!.StartsWith("WorkloadDocker", StringComparison.Ordinal) || Uid(e)!.StartsWith("WorkloadServices", StringComparison.Ordinal))).ToList();
        Assert.Equal(9, sectionStates.Count);
        Assert.All(sectionStates, s => Assert.Equal("Level3", Attr(s, "TitleHeadingLevel")));
        var pageStates = workloads.Concat(Elements(History)).Where(e => e.Name.LocalName == "SaEmptyState").Except(sectionStates).ToList();
        Assert.Equal(5, pageStates.Count);                                         // unavailable/nothing/no-results + History unavailable/empty
        Assert.All(pageStates, s => Assert.Null(Attr(s, "TitleHeadingLevel")));   // default Level2 under the page H1
    }

    [Theory]
    [InlineData(History, 2)]
    [InlineData(Workloads, 3)]
    public void BeaconL2_PageStatePanes_AreNamedByTheStateTitle(string file, int count)
    {
        // The ScrollViewer around a page state derived its UIA name from the action inside ("Limpar pesquisa").
        var panes = Elements(file).Where(e => e.Name.LocalName == "ScrollViewer" && e.Elements().SingleOrDefault()?.Name.LocalName == "SaEmptyState").ToList();
        Assert.Equal(count, panes.Count);
        Assert.All(panes, pane =>
        {
            var state = pane.Elements().Single();
            Assert.Equal($"{{Binding Title, ElementName={(string)state.Attribute(AppSourceTree.Xaml + "Name")!}}}", Attr(pane, "AutomationProperties.Name"));
        });
    }

    [Fact]
    public void BeaconN3_ChartNameUsesTheVisibleTerm_PeakInPeriod()
    {
        foreach (var culture in Cultures)
        {
            var resw = Resw(culture);
            var peakTerm = resw["HistoryPeakFormat"].Replace("{0}", string.Empty, StringComparison.Ordinal).Trim();
            Assert.Contains($"{peakTerm} {{3}}", resw["HistoryChartSummaryFormat"], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PrismR2_ColumnHeadersSitAtTheTop_AndStackedSectionStatesGetRoomUnderTheTitle()
    {
        // R2-2: 112:2647/112:2648 put the header text at y 0 of the h28 band.
        var headers = Elements(Workloads).Where(e => Attr(e, "Style") == "{StaticResource SaTableHeaderTextStyle}").ToList();
        Assert.Equal(4, headers.Count);
        Assert.All(headers, h => Assert.Equal("Top", Attr(h, "VerticalAlignment")));

        // R2-3: every section state (5 Docker + 4 services + 2 section no-results) gets 16 above / 8 below ONLY when stacked.
        var sectionStates = Elements(Workloads)
            .Where(e => Uid(e) is { } uid && uid.StartsWith("Workload", StringComparison.Ordinal)
                        && (uid.StartsWith("WorkloadDocker", StringComparison.Ordinal) || uid.StartsWith("WorkloadServices", StringComparison.Ordinal))
                        && (uid.EndsWith("State", StringComparison.Ordinal) || uid.EndsWith("NoResults", StringComparison.Ordinal)))
            .Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name"))
            .ToList();
        Assert.Equal(11, sectionStates.Count);
        Assert.DoesNotContain(null, sectionStates);
        var states = Elements(Workloads).Where(e => e.Name.LocalName == "VisualState").ToDictionary(s => (string)s.Attribute(AppSourceTree.Xaml + "Name")!);
        foreach (var state in new[] { "Medium", "Narrow" })
        {
            var setters = states[state].Descendants().Where(e => e.Name.LocalName == "Setter")
                .ToDictionary(s => (string)s.Attribute("Target")!, s => (string)s.Attribute("Value")!);
            Assert.All(sectionStates, name => Assert.Equal("0,16,0,8", setters[$"{name}.Margin"]));
        }

        Assert.All(sectionStates, name => Assert.Null(Attr(Elements(Workloads).Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name), "Margin")));
    }

    [Fact]
    public void Workloads_SearchAndFilter_AreGlobal_Accessible_AndOneKeyboardGroup()
    {
        var search = Assert.Single(Elements(Workloads), e => Uid(e) == "WorkloadSearchAll");
        Assert.Equal("{StaticResource SaPageSearchFieldStyle}", Attr(search, "Style"));
        Assert.Contains("SearchText, Mode=TwoWay", Attr(search, "Text"), StringComparison.Ordinal);
        Assert.Equal("Left", Attr(search, "HorizontalAlignment"));   // Prism R1 F2: never centred when it spans < 900

        var group = Assert.Single(Elements(Workloads), e => Uid(e) == "WorkloadGlobalFilter");
        Assert.Equal("SelectionFollowsFocus", group.Attributes().First(a => a.Name.LocalName == "SaGroupNavigation.Mode").Value);
        var segments = group.Elements().Where(e => e.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(new[] { "{Binding FilterAllLabel}", "{Binding FilterProblemsLabel}" }, segments.Select(s => Attr(s, "Content")));
        Assert.All(segments, s => Assert.Equal("{StaticResource SaSegmentedRectFilterItemStyle}", Attr(s, "Style")));
    }

    [Fact]
    public void Workloads_TitleIsLevel1_AndSectionTitlesAreLevel2()
    {
        Assert.Equal("Level1", Attr(Assert.Single(Elements(Workloads), e => Uid(e) == "WorkloadsPageTitle"), "AutomationProperties.HeadingLevel"));
        Assert.Equal("Level2", Attr(Assert.Single(Elements(Workloads), e => Uid(e) == "WorkloadDockerSectionTitle"), "AutomationProperties.HeadingLevel"));
        Assert.Equal("Level2", Attr(Assert.Single(Elements(Workloads), e => Uid(e) == "WorkloadServicesSectionTitle"), "AutomationProperties.HeadingLevel"));
    }

    [Fact]
    public void PrismR1_VerticalRhythm_MatchesTheFigma()
    {
        // F4: a two-row control/query grid adds its RowSpacing only when the 2nd row is used (< 900).
        foreach (var (file, grid) in new[] { (History, "ControlsRow"), (Workloads, "QueryRow") })
        {
            var element = Assert.Single(Elements(file), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == grid);
            Assert.Null(Attr(element, "RowSpacing"));
            var targets = Elements(file).Where(e => e.Name.LocalName == "Setter").Select(s => Attr(s, "Target")).ToList();
            Assert.Equal(2, targets.Count(t => t == grid + ".RowSpacing"));   // Medium + Narrow
        }

        // F3: plot 98 + 16 + axis label (14) = 128; the chart's own row spacing is the 16.
        Assert.All(Elements(History).Where(e => e.Name.LocalName == "HistoryChart"), c => Assert.Equal("128", Attr(c, "Height")));
        var chartRoot = Assert.Single(AppSourceTree.LoadXaml("Controls/HistoryChart.xaml").Descendants(),
            e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "RootGrid");
        Assert.Equal("16", Attr(chartRoot, "RowSpacing"));

        // F5: workloads card = explicit spacers, compact h28 column header.
        var headers = Elements(Workloads).Where(e => Attr(e, "Style") == "{StaticResource SaDataTableCompactHeaderStyle}").ToList();
        Assert.Equal(2, headers.Count);
        Assert.All(headers, h => Assert.Equal("0,20,0,0", Attr(h, "Margin")));
        Assert.DoesNotContain(Elements(Workloads), e => e.Name.LocalName == "Grid" && Attr(e, "RowSpacing") == "{StaticResource SaSpace4}");
    }

    // --- Both pages ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(History)]
    [InlineData(Workloads)]
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
    [InlineData(History)]
    [InlineData(Workloads)]
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
                        && (t.EndsWith(".Padding", StringComparison.Ordinal) || t.EndsWith(".CornerRadius", StringComparison.Ordinal) || t.EndsWith(".FontSize", StringComparison.Ordinal))
                        && !(Attr(e, "Value") ?? string.Empty).StartsWith('{'))
            .Select(e => $"Setter {Attr(e, "Target")}={Attr(e, "Value")}");

        Assert.Empty(offenders.Concat(setterOffenders));
    }

    [Fact]
    public void PtPt_PageCopy_UsesTu()
    {
        var ptPt = Resw("pt-PT");
        var uids = Elements(History).Concat(Elements(Workloads)).Select(Uid).Where(u => u is not null).ToHashSet(StringComparer.Ordinal);
        var offenders = ptPt.Where(kv => uids.Any(u => kv.Key.StartsWith(u + ".", StringComparison.Ordinal)) && YouImperative().IsMatch(kv.Value))
            .Select(kv => $"{kv.Key} = {kv.Value}").ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [GeneratedRegex(@"#[0-9A-Fa-f]{6,8}\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\b(Verifique|Experimente|Tente|Volte|Remova|Adicione)\b")]
    private static partial Regex YouImperative();
}
