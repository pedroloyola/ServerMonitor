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
    private static readonly string[] BaselineNavigationIds = ["overview", "servers", "history", "settings"];

    private static readonly string[] BaselineSettingsSectionIds = ["appearance", "monitoring", "window"];

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
}
