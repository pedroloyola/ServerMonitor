using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Features;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M15.4a: the shell consumes contributions. With none, the navigation and the Settings page are exactly the built-in
/// ones (the baseline below, also checked against the XAML); a factory that throws removes only its own contribution;
/// routing is by id, without a page type.
/// </summary>
public sealed class ShellContributionHostTests
{
    /// <summary>The baseline: what the shell shows today with no contributions.</summary>
    internal static readonly string[] BaselineNavigationIds = ["overview", "servers", "history", "settings"];

    internal static readonly string[] BaselineSettingsSectionIds = ["appearance", "monitoring", "window"];

    private sealed class View(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class DisposableView : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ShellContribution Contribution(string id, int order = 0, Func<object>? factory = null) =>
        new(id, order, "TitleKey." + id, factory ?? (() => new View(id)));

    private static ShellContributionHost Host(ShellContributions contributions, ILogger? logger = null) =>
        new(contributions, logger ?? NullLogger.Instance, key => "Title of " + key, view => view is View or DisposableView);

    // ---------------------------------------------------------------- (a) and (b)

    [Fact]
    public void WithNoContributions_TheSettingsAndNavigationIdSequences_MatchTheBaseline()
    {
        var host = Host(ShellContributions.Empty);

        Assert.Equal(BaselineNavigationIds, host.NavigationIds);
        Assert.Equal(BaselineSettingsSectionIds, host.SettingsSectionIds);
        Assert.Equal(BaselineNavigationIds, ShellContributionHost.BuiltInNavigationIds);
        Assert.Equal(BaselineSettingsSectionIds, ShellContributionHost.BuiltInSettingsSectionIds);
        Assert.Empty(host.NavigationEntries);
        Assert.Empty(host.SettingsSections);
    }

    [Fact]
    public void AContributionFactoryThatThrows_LeavesTheSequenceEqualToTheBaseline()
    {
        var logger = new RecordingLogger();
        var host = Host(
            new ShellContributions(
                [Contribution("broken.section", factory: () => throw new InvalidOperationException("view failed"))],
                [Contribution("broken.entry", factory: () => throw new InvalidOperationException("view failed"))]),
            logger);

        Assert.Equal(BaselineNavigationIds, host.NavigationIds);
        Assert.Equal(BaselineSettingsSectionIds, host.SettingsSectionIds);
        Assert.False(host.TryGetNavigationView("broken.entry", out _));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Message.Contains("InvalidOperationException", StringComparison.Ordinal)));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("view failed", StringComparison.Ordinal)); // type only, never the message
    }

    [Fact]
    public void AFactoryThatReturnsNothingDisplayable_IsRemovedToo()
    {
        var logger = new RecordingLogger();
        var host = Host(new ShellContributions([Contribution("null.section", factory: () => null!)], [Contribution("text.entry", factory: () => "not a view")]), logger);

        Assert.Equal(BaselineNavigationIds, host.NavigationIds);
        Assert.Equal(BaselineSettingsSectionIds, host.SettingsSectionIds);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Error));
    }

    [Fact]
    public void OneBrokenFactory_DoesNotTakeTheOthersWithIt()
    {
        var host = Host(new ShellContributions(
            [],
            [Contribution("a.entry", 1), Contribution("b.entry", 0, () => throw new InvalidOperationException()), Contribution("c.entry", 2)]));

        Assert.Equal(["overview", "servers", "history", "a.entry", "c.entry", "settings"], host.NavigationIds);
    }

    // ---------------------------------------------------------------- placement, ordering, duplicates

    [Fact]
    public void ContributedEntries_GoBeforeSettings_AndSectionsAfterTheBuiltInOnes_InOrderThenIdOrder()
    {
        var host = Host(new ShellContributions(
            [Contribution("z.section", 0), Contribution("a.section", 5), Contribution("m.section", 0)],
            [Contribution("y.entry", 3), Contribution("b.entry", 3), Contribution("q.entry", -1)]));

        Assert.Equal(["overview", "servers", "history", "q.entry", "b.entry", "y.entry", "settings"], host.NavigationIds);
        Assert.Equal(["appearance", "monitoring", "window", "m.section", "z.section", "a.section"], host.SettingsSectionIds);
        Assert.Equal("Title of TitleKey.q.entry", host.NavigationEntries[0].Title);
    }

    [Fact]
    public void DuplicateIds_TheFirstWins_AndABuiltInIdIsNeverTaken()
    {
        var logger = new RecordingLogger();
        var host = Host(new ShellContributions(
            [Contribution("window"), Contribution("extra.section")],
            [Contribution("dup.entry", 1, () => new View("first")), Contribution("dup.entry", 0, () => new View("second")), Contribution("settings")]),
            logger);

        Assert.Equal(["overview", "servers", "history", "dup.entry", "settings"], host.NavigationIds);
        Assert.Equal(["appearance", "monitoring", "window", "extra.section"], host.SettingsSectionIds);
        Assert.True(host.TryGetNavigationView("dup.entry", out var view));
        Assert.Equal("first", Assert.IsType<View>(view).Name);
        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void EachFactory_RunsOnce()
    {
        var calls = 0;
        var host = Host(new ShellContributions([], [Contribution("once.entry", factory: () => { calls++; return new View("once"); })]));

        _ = host.NavigationIds;
        _ = host.NavigationEntries;
        host.TryGetNavigationView("once.entry", out _);

        Assert.Equal(1, calls);
    }

    // ---------------------------------------------------------------- routing by id

    [Fact]
    public void NavigatingToAContribution_IsById_AndShowsItsView()
    {
        var (navigation, frame, _) = Router(new ShellContributions([], [Contribution("tools.entry")]));

        navigation.GoToContribution("tools.entry");

        Assert.Equal("tools.entry", Assert.IsType<View>(frame.Content).Name);
        Assert.Equal(NavigationDestination.Contribution, navigation.CurrentDestination);
        Assert.Equal("tools.entry", navigation.CurrentContributionId);
    }

    [Fact]
    public void AnUnknownOrRemovedId_ChangesNothing()
    {
        var (navigation, frame, _) = Router(new ShellContributions([], [Contribution("broken.entry", factory: () => throw new InvalidOperationException())]));
        navigation.GoToContribution("broken.entry");
        navigation.GoToContribution("never.contributed");

        Assert.Null(frame.Content);
        Assert.Null(navigation.CurrentDestination);
        Assert.Null(navigation.CurrentContributionId);
    }

    [Fact]
    public void LeavingAContribution_ClearsItsId_AndKeepsItsViewForTheNextVisit()
    {
        var view = new DisposableView();
        var (navigation, frame, pages) = Router(new ShellContributions([], [Contribution("tools.entry", factory: () => view)]));

        navigation.GoToContribution("tools.entry");
        navigation.GoToSettings();
        navigation.GoToContribution("tools.entry");

        Assert.False(view.Disposed);
        Assert.Same(view, frame.Content);
        Assert.Equal("tools.entry", navigation.CurrentContributionId);
        navigation.GoToSettings();
        Assert.Null(navigation.CurrentContributionId);
        Assert.Equal(NavigationDestination.Settings, navigation.CurrentDestination);
        Assert.NotEmpty(pages);
    }

    [Fact]
    public void TheShellViewModel_ProjectsTheSelectedContribution()
    {
        var (navigation, _, _) = Router(new ShellContributions([], [Contribution("tools.entry")]));
        using var shell = new ShellViewModel(navigation, Host(new ShellContributions([], [Contribution("tools.entry")])));
        var changed = new List<string?>();
        shell.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        shell.NavigateToContribution("tools.entry");

        Assert.Equal("tools.entry", shell.SelectedContributionId);
        Assert.Null(shell.SelectedDestination);
        Assert.Contains(nameof(ShellViewModel.SelectedContributionId), changed);
        Assert.Equal(["tools.entry"], shell.ContributedEntries.Select(entry => entry.Id));
    }

    [Fact]
    public void WithoutAHost_TheShellViewModel_HasNoContributedEntries()
    {
        using var shell = new ShellViewModel(new Fakes.FakeNavigationService());

        Assert.Empty(shell.ContributedEntries);
        Assert.Null(shell.SelectedContributionId);
    }

    private static (NavigationService Navigation, FrameHost Frame, List<Type> Pages) Router(ShellContributions contributions)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Host(contributions));
        var provider = services.BuildServiceProvider();
        var pages = new List<Type>();
        var settingsPage = new object();
        var navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, type =>
        {
            pages.Add(type);
            return settingsPage;
        });
        var frame = new FrameHost();
        navigation.Initialize(frame);
        return (navigation, frame, pages);
    }

    internal sealed class FrameHost : INavigationHost
    {
        public object? Content { get; set; }
    }

    // ---------------------------------------------------------------- the baseline is the real shell

    [Fact]
    public void TheNavigationBaseline_IsTheSidebarsItems_InOrder()
    {
        var sidebar = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml");
        var tags = sidebar.Descendants().Where(e => e.Name.LocalName == "RadioButton")
            .OrderBy(e => int.Parse((string)e.Attribute("Grid.Row")!, System.Globalization.CultureInfo.InvariantCulture))
            .Select(e => ((string)e.Attribute("Tag")!).ToLowerInvariant());

        Assert.Equal(BaselineNavigationIds, tags);
    }

    [Fact]
    public void TheSettingsBaseline_IsThePagesSections_AndContributionsFollowTheLastOne()
    {
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var root = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PageRoot");
        var children = root.Elements().ToList();
        var sections = children
            .Select((element, index) => (element, index))
            .Where(child => child.element.Elements().FirstOrDefault() is { } heading
                && (string?)heading.Attribute("AutomationProperties.HeadingLevel") == "Level2")
            .ToList();

        Assert.Equal(
            ["SettingsAppearanceTitle", "SettingsMonitoringTitle", "SettingsWindowTitle"],
            sections.Select(section => (string?)section.element.Elements().First().Attribute(AppSourceTree.Xaml + "Uid")));
        var host = children[sections[^1].index + 1];
        Assert.Equal("ContributedSections", (string?)host.Attribute(AppSourceTree.Xaml + "Name"));
        Assert.Equal("Collapsed", (string?)host.Attribute("Visibility")); // no visual change while there are none
        Assert.Equal(BaselineSettingsSectionIds.Length, sections.Count);
    }

    // ---------------------------------------------------------------- composition root

    [Fact]
    public void TheRoot_RegistersTheHost_AndTheContributionsComposed()
    {
        var services = IsolatedAppCompositionDescriptors();

        Assert.Contains(services, d => d.ServiceType == typeof(ShellContributionHost));
        var contributions = services.Where(d => d.ServiceType == typeof(ShellContributions)).ToList();
        Assert.Equal(2, contributions.Count); // the empty default, then what composition kept (the last one wins)
        Assert.Same(ShellContributions.Empty, contributions[0].ImplementationInstance);
        var composed = Assert.IsType<ShellContributions>(contributions[1].ImplementationInstance);
        Assert.Empty(composed.SettingsSections); // the modules in this repository add no user interface
        Assert.Empty(composed.NavigationEntries);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEntitlementProvider));
    }

    private static IReadOnlyList<ServiceDescriptor> IsolatedAppCompositionDescriptors() =>
        TestSupport.IsolatedAppComposition.ProductionDescriptors();

    // ---------------------------------------------------------------- neutral vocabulary

    [Fact]
    public void TheExtensionPoint_UsesNoCommercialVocabulary()
    {
        Type[] types =
        [
            typeof(ShellContribution), typeof(ShellContributions), typeof(ShellContributionOrdering),
            typeof(ShellContributionRejection), typeof(ShellContributionHost), typeof(RealizedShellContribution),
        ];
        var names = types.SelectMany(type => new[] { type.Name }.Concat(type.GetMembers().Select(member => member.Name)));
        var sources = new[]
        {
            Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.Features", "ShellContribution.cs"),
            AppSourceTree.Full("Services/ShellContributionHost.cs"),
        }.Select(File.ReadAllText);
        var forbidden = new Regex(@"\bPro\b|IsPro|licen[cs]e|trial|entitlement(?!Provider)|commercial|premium|paid", RegexOptions.IgnoreCase);

        Assert.Empty(names.Where(name => forbidden.IsMatch(name)));
        Assert.All(sources, source => Assert.DoesNotMatch(forbidden, source));
    }

    // ---------------------------------------------------------------- r2: what the shell can show (L-1, S-1)

    private sealed class PageView(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public void ANavigationEntry_MustBeAPage_ASettingsSectionAnyElement()
    {
        var logger = new RecordingLogger();
        var host = new ShellContributionHost(
            new ShellContributions(
                [Contribution("plain.section"), Contribution("page.section", factory: () => new PageView("page.section"))],
                [Contribution("plain.entry"), Contribution("page.entry", factory: () => new PageView("page.entry"))]),
            logger, key => "Title of " + key,
            canShowNavigationView: view => view is PageView,
            canShowSectionView: view => view is View or PageView);

        Assert.Equal(["page.entry"], host.NavigationEntries.Select(entry => entry.Id));
        Assert.Equal(["page.section", "plain.section"], host.SettingsSections.Select(section => section.Id));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("plain.entry", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShell_AcceptsOnlyAPageForANavigationEntry_TheTypeItRemountsOnAThemeChange()
    {
        Assert.Equal(typeof(Microsoft.UI.Xaml.Controls.Page), ShellContributionHost.NavigationViewType);
        Assert.Equal(typeof(Microsoft.UI.Xaml.FrameworkElement), ShellContributionHost.SectionViewType);
        Assert.False(ShellContributionHost.IsDisplayableNavigationView(new object()));
        Assert.False(ShellContributionHost.IsDisplayableSectionView("not an element"));

        // The theme re-mount re-creates what the frame shows only when it is a Page (MainWindow).
        var mainWindow = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("ContentFrame.Content is Microsoft.UI.Xaml.Controls.Page page && SaThemeRefresh.Remount(page", mainWindow, StringComparison.Ordinal);

        // Both checks also require no parent: an element can be placed in one place only.
        var host = AppSourceTree.CodeWithoutComments("Services/ShellContributionHost.cs");
        Assert.Contains("NavigationViewType.IsInstanceOfType(view) && ((FrameworkElement)view).Parent is null", host, StringComparison.Ordinal);
        Assert.Contains("SectionViewType.IsInstanceOfType(view) && ((FrameworkElement)view).Parent is null", host, StringComparison.Ordinal);
        Assert.Contains(": this(contributions, logger, localization.GetString, IsDisplayableNavigationView, IsDisplayableSectionView, ValidateIconPathData)", host, StringComparison.Ordinal);
    }

    [Fact]
    public void AViewThatCannotBePlaced_IsLeftOut_LoggedByType_AndTheNextOneIsStillPlaced()
    {
        var logger = new RecordingLogger();
        var host = Host(new ShellContributions([Contribution("a.section"), Contribution("b.section")], []), logger);
        var placed = new List<string>();

        var results = host.SettingsSections
            .Select(section => host.TryAttach(section, view =>
            {
                if (section.Id == "a.section")
                {
                    throw new InvalidOperationException("Element is already the child of another element.");
                }

                placed.Add(((View)view).Name);
            }))
            .ToList();

        Assert.Equal([false, true], results);
        Assert.Equal(["b.section"], placed);
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("a.section", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("already the child", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPage_PlacesEachSectionThroughTheHost_SoOneFailingSectionLeavesThePageBuilt()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs").Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = code.IndexOf("private void AddContributedSections(", StringComparison.Ordinal);
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("contributions.TryAttach(section, view =>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("section.View", body, StringComparison.Ordinal); // only the view TryAttach hands over
        Assert.True(
            body.IndexOf("block.Children.Add((Microsoft.UI.Xaml.UIElement)view)", StringComparison.Ordinal)
                < body.IndexOf("ContributedSections.Children.Add(block)", StringComparison.Ordinal),
            "a block reaches the page only once it is complete");
    }

    // ---------------------------------------------------------------- r2: title and icon (U-1, U-2)

    [Theory]
    [InlineData("")]
    [InlineData("TitleKey.no.title")]
    [InlineData(null)]
    public void AMissingTitle_IsLogged_AndShowsANeutralLabel_NeverTheId(string? lookupResult)
    {
        var logger = new RecordingLogger();
        var host = new ShellContributionHost(
            new ShellContributions([], [Contribution("no.title")]),
            logger,
            key => key == ShellContributionHost.UntitledResourceKey ? "Neutral label"
                : lookupResult ?? throw new InvalidOperationException("missing resource"),
            view => view is View);

        var entry = Assert.Single(host.NavigationEntries);
        Assert.Equal("Neutral label", entry.Title);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("no.title", warning.Message, StringComparison.Ordinal);
        Assert.Contains("TitleKey.no.title", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutEvenTheNeutralResource_TheLabelIsStillNotTheId()
    {
        var host = new ShellContributionHost(new ShellContributions([], [Contribution("no.title")]), NullLogger.Instance, _ => "", view => view is View);

        Assert.Equal(ShellContributionHost.UntitledFallback, Assert.Single(host.NavigationEntries).Title);
    }

    [Theory]
    [InlineData("en-US", "Untitled")]
    [InlineData("pt-PT", "Sem título")]
    [InlineData("pt-BR", "Sem título")]
    public void TheNeutralLabel_IsInEveryLanguage(string language, string expected)
    {
        var resw = XDocument.Load(AppSourceTree.Full($"Resources/{language}/Resources.resw"));
        var value = resw.Root!.Elements("data")
            .Single(data => (string?)data.Attribute("name") == ShellContributionHost.UntitledResourceKey)
            .Element("value")!.Value;

        Assert.Equal(expected, value);
    }

    [Fact]
    public void AnEntrysIcon_IsCarriedToTheShell_AndBlankMeansTheGenericOne()
    {
        var host = Host(new ShellContributions([], [
            new ShellContribution("with.icon", 0, "Key", () => new View("with.icon"), "M0,0 L20,20"),
            new ShellContribution("blank.icon", 1, "Key", () => new View("blank.icon"), "  "),
            Contribution("no.icon", order: 2),
        ]));

        Assert.Equal(["M0,0 L20,20", null, null], host.NavigationEntries.Select(entry => entry.IconPathData));
        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        Assert.Contains("entry.IconPathData ?? (string)Application.Current.Resources[\"SaIconPackageData\"]", sidebar, StringComparison.Ordinal);
    }

    [Fact]
    public void AFactoryThatReadsTheContributionsWhileTheyAreCreated_SeesNone_AndDoesNotRecurse()
    {
        ShellContributionHost? host = null;
        var seen = -1;
        host = Host(new ShellContributions([], [Contribution("curious.entry", factory: () =>
        {
            seen = host!.NavigationEntries.Count + host.SettingsSections.Count;
            return new View("curious.entry");
        })]));

        Assert.Equal(["curious.entry"], host.NavigationEntries.Select(entry => entry.Id));
        Assert.Equal(0, seen);
    }

    // ---------------------------------------------------------------- r2: the sidebar's insertion (T-1)

    [Fact]
    public void TheSidebarLayout_MatchesTheSidebarsXaml()
    {
        var grid = SidebarGrid();
        var rows = grid.Element(Presentation + "Grid.RowDefinitions")!.Elements().Select(r => (string)r.Attribute("Height")!).ToList();
        var items = grid.Elements().Where(e => e.Name.LocalName == "RadioButton").ToList();

        Assert.Equal(rows.IndexOf("*"), ServerMonitor.App.Controls.SidebarContributionLayout.FirstContributedRow);
        Assert.Single(rows, height => height == "*");
        Assert.Equal(items.Count, ServerMonitor.App.Controls.SidebarContributionLayout.BuiltInDestinations);
        Assert.Equal(ServerMonitor.App.Controls.SidebarContributionLayout.ContributedRowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), rows[1]);
        Assert.Equal(
            Enumerable.Range(1, items.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            items.Select(item => (string)item.Attribute("AutomationProperties.PositionInSet")!));
        Assert.All(items, item => Assert.Equal(
            ServerMonitor.App.Controls.SidebarContributionLayout.SizeOfSet(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string)item.Attribute("AutomationProperties.SizeOfSet")!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void InsertingEntries_KeepsSettingsLast_TheIndicatorSpanningAllRows_AndAGaplessSet(int count)
    {
        var grid = SidebarGrid();
        var rowCount = grid.Element(Presentation + "Grid.RowDefinitions")!.Elements().Count() + count;
        var children = grid.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)).Select(e => (
            Name: (string?)e.Attribute(AppSourceTree.Xaml + "Name") ?? e.Name.LocalName,
            Row: ServerMonitor.App.Controls.SidebarContributionLayout.RowAfterInsert(IntAttribute(e, "Grid.Row", 0), count),
            Span: ServerMonitor.App.Controls.SidebarContributionLayout.RowSpanAfterInsert(IntAttribute(e, "Grid.Row", 0), IntAttribute(e, "Grid.RowSpan", 1), count),
            Position: IntAttribute(e, "AutomationProperties.PositionInSet", 0))).ToList();
        var contributedRows = Enumerable.Range(0, count).Select(ServerMonitor.App.Controls.SidebarContributionLayout.RowOfContributed).ToList();

        Assert.Equal(rowCount - 1, children.Single(c => c.Name == "SettingsItem").Row);
        Assert.Equal(rowCount, children.Single(c => c.Name == "Border").Span);
        Assert.DoesNotContain(children, c => c.Span == 1 && contributedRows.Contains(c.Row)); // the new rows are free
        Assert.Equal(Enumerable.Range(ServerMonitor.App.Controls.SidebarContributionLayout.FirstContributedRow, count), contributedRows);

        var positions = children.Where(c => c.Position > 0 && c.Name != "SettingsItem").Select(c => c.Position)
            .Concat(Enumerable.Range(0, count).Select(ServerMonitor.App.Controls.SidebarContributionLayout.PositionOfContributed))
            .Append(ServerMonitor.App.Controls.SidebarContributionLayout.SizeOfSet(count)); // Settings is the last position
        Assert.Equal(Enumerable.Range(1, ServerMonitor.App.Controls.SidebarContributionLayout.SizeOfSet(count)), positions);
    }

    [Fact]
    public void TheSidebar_UsesTheLayoutForEveryRowAndPosition()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");

        Assert.Contains("SidebarContributionLayout.RowAfterInsert(row, count)", code, StringComparison.Ordinal);
        Assert.Contains("SidebarContributionLayout.RowSpanAfterInsert(row, Grid.GetRowSpan(child), count)", code, StringComparison.Ordinal);
        Assert.Contains("NavGrid.RowDefinitions.Insert(SidebarContributionLayout.RowOfContributed(i)", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetPositionInSet(item, SidebarContributionLayout.PositionOfContributed(i))", code, StringComparison.Ordinal);
        Assert.Contains("var size = SidebarContributionLayout.SizeOfSet(count);", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetPositionInSet(SettingsItem, size);", code, StringComparison.Ordinal);
        Assert.Contains("NavGrid.Children.Insert(settingsIndex + i, item);", code, StringComparison.Ordinal);
    }

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static System.Xml.Linq.XElement SidebarGrid() =>
        AppSourceTree.LoadXaml("Controls/SaSidebar.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "NavGrid");

    private static int IntAttribute(System.Xml.Linq.XElement element, string name, int fallback) =>
        element.Attribute(name) is { } attribute ? int.Parse(attribute.Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    // ---------------------------------------------------------------- r2: routing through the exit guard (T-2)

    private sealed class GuardedPage(Task<bool> answer) : INavigationExitGuard
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmLeaveAsync()
        {
            Asked++;
            return answer;
        }
    }

    [Fact]
    public void GoingToAContribution_StaysWhenTheShownPageRefusesToBeLeft()
    {
        var (navigation, frame, _) = Router(new ShellContributions([], [Contribution("tools.entry")]));
        var guard = new GuardedPage(Task.FromResult(false));
        frame.Content = guard;

        navigation.GoToContribution("tools.entry");

        Assert.Equal(1, guard.Asked);
        Assert.Same(guard, frame.Content);
        Assert.Null(navigation.CurrentContributionId);
    }

    [Fact]
    public void GoingToAContribution_WaitsForTheAnswer_AndGoesWhenThePageAgrees()
    {
        var (navigation, frame, _) = Router(new ShellContributions([], [Contribution("tools.entry")]));
        var answer = new TaskCompletionSource<bool>();
        var guard = new GuardedPage(answer.Task);
        frame.Content = guard;

        navigation.GoToContribution("tools.entry");
        Assert.Same(guard, frame.Content); // still asking
        navigation.GoToContribution("tools.entry"); // a second click while the question is open is dropped
        answer.SetResult(true);

        Assert.Equal(1, guard.Asked);
        Assert.Equal("tools.entry", Assert.IsType<View>(frame.Content).Name);
        Assert.Equal("tools.entry", navigation.CurrentContributionId);
        Assert.Equal(NavigationDestination.Contribution, navigation.CurrentDestination);
    }

    [Fact]
    public void SelectingTheContributionAlreadyShown_DoesNothing()
    {
        var (navigation, frame, _) = Router(new ShellContributions([], [Contribution("tools.entry")]));
        navigation.GoToContribution("tools.entry");
        var shown = frame.Content;
        var navigated = 0;
        navigation.Navigated += (_, _) => navigated++;

        navigation.GoToContribution("tools.entry");

        Assert.Same(shown, frame.Content);
        Assert.Equal(0, navigated);
        Assert.Equal("tools.entry", navigation.CurrentContributionId);
    }

    // ---------------------------------------------------------------- r2: the real root with a denied module (L-2)

    private sealed class DenyAll : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => false;
    }

    private sealed class GatedContributingModule : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = new(new FeatureId("test.gated"), RequiresEntitlement: true);

        public int ContributionReads { get; private set; }

        public ShellContributions Contributions
        {
            get
            {
                ContributionReads++;
                return new ShellContributions([Contribution("gated.section")], [Contribution("gated.entry")]);
            }
        }

        public void Register(IServiceCollection services)
        {
        }
    }

    [Fact]
    public void ThroughTheRealRoot_ADeniedConditionalModuleThatDeclaresContributions_ContributesNothing()
    {
        var services = new ServiceCollection();
        var module = new GatedContributingModule();

        var result = App.ComposeFeatures(services, [module], new DenyAll());

        Assert.Single(result.Skipped);
        Assert.Equal(0, module.ContributionReads);
        var registered = Assert.IsType<ShellContributions>(services.Last(d => d.ServiceType == typeof(ShellContributions)).ImplementationInstance);
        Assert.Empty(registered.SettingsSections);
        Assert.Empty(registered.NavigationEntries);
        var host = Host(registered);
        Assert.Equal(BaselineNavigationIds, host.NavigationIds);
        Assert.Equal(BaselineSettingsSectionIds, host.SettingsSectionIds);
    }

    // ---------------------------------------------------------------- r3: icon data is parsed inside the isolation (N-1 / L-3)

    private static ShellContributionHost IconHost(ShellContributions contributions, ILogger logger, List<string> validated) =>
        new(contributions, logger, key => "Title of " + key, view => view is View, view => view is View, data =>
        {
            validated.Add(data);
            if (!data.StartsWith('M') || data.EndsWith('L'))
            {
                throw new FormatException("bad path markup near the end of " + data);
            }
        });

    [Theory]
    [InlineData("M0,0 L")]
    [InlineData("not a path")]
    public void MalformedIconData_FallsBackToTheGenericIcon_LoggedByIdAndType_AndTheEntryStays(string data)
    {
        var logger = new RecordingLogger();
        var validated = new List<string>();
        var host = IconHost(new ShellContributions([], [new ShellContribution("bad.icon", 0, "Key", () => new View("bad.icon"), data)]), logger, validated);

        var entry = Assert.Single(host.NavigationEntries);

        Assert.Equal("bad.icon", entry.Id);
        Assert.Null(entry.IconPathData);
        Assert.Equal([data], validated);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("bad.icon", warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FormatException), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bad path markup", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoIconData_IsNeverParsed_AndShowsTheGenericIcon(string? data)
    {
        var logger = new RecordingLogger();
        var validated = new List<string>();
        var host = IconHost(new ShellContributions([], [new ShellContribution("no.icon", 0, "Key", () => new View("no.icon"), data)]), logger, validated);

        Assert.Null(Assert.Single(host.NavigationEntries).IconPathData);
        Assert.Empty(validated);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ValidIconData_IsParsedOnce_AndKept()
    {
        var logger = new RecordingLogger();
        var validated = new List<string>();
        var host = IconHost(new ShellContributions([], [new ShellContribution("good.icon", 0, "Key", () => new View("good.icon"), "M0,0 L20,20 Z")]), logger, validated);

        Assert.Equal("M0,0 L20,20 Z", Assert.Single(host.NavigationEntries).IconPathData);
        _ = host.NavigationEntries;
        Assert.Equal(["M0,0 L20,20 Z"], validated);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void InProduction_IconDataIsParsedExactlyAsTheSidebarsIconParsesIt()
    {
        var host = AppSourceTree.CodeWithoutComments("Services/ShellContributionHost.cs");
        var icon = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaIcon.cs");

        Assert.Contains("XamlBindingHelper.ConvertValue(typeof(Geometry), Data)", icon, StringComparison.Ordinal);
        Assert.Contains("ValidateIconPathData(string data) => _ = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);", host, StringComparison.Ordinal);
        Assert.Contains("IsDisplayableNavigationView, IsDisplayableSectionView, ValidateIconPathData)", host, StringComparison.Ordinal);
    }
}
