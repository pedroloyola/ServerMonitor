using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.ViewModels;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;
using ServerMonitor.Features;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M16: a contributed server page, against the REAL <see cref="NavigationService"/> and host over a recorded frame (pages
/// are sentinels; the detail page hosts the REAL <see cref="ServerDetailViewModel"/>). With no page contributed nothing
/// changes; a page opens for a listed server only, with a fresh view per visit; its way back is that server's detail page.
/// </summary>
public sealed class ServerPageNavigationTests
{
    private sealed class View(Guid serverId)
    {
        public Guid ServerId { get; } = serverId;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ServerPage Page(string id, int order = 0, Func<Guid, object>? factory = null, string? detail = null, string? icon = null) =>
        new(id, order, "TitleKey." + id, factory ?? (serverId => new View(serverId)), detail, icon);

    private static ShellContributionHost Host(ShellContributions contributions, ILogger? logger = null) =>
        new(contributions, logger ?? NullLogger.Instance, key => "Title of " + key, view => view is View);

    // ---------------------------------------------------------------- with none, nothing changes

    [Fact]
    public void WithNoServerPages_TheHostHasNoRows_AndNoIdIsAServerPage()
    {
        var host = Host(ShellContributions.Empty);

        Assert.Empty(host.ServerPageEntries);
        Assert.False(host.IsServerPage("any.page"));
        Assert.False(host.IsServerPage(null));
        Assert.False(host.TryCreateServerPage("any.page", Guid.NewGuid(), out _, out _));
    }

    [Fact]
    public async Task WithNoServerPages_GoingToOne_ChangesNothing_AndTheDetailPageStays()
    {
        var world = await World.CreateAsync(ShellContributions.Empty);
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);
        var detail = world.Host.Content;
        var navigated = 0;
        world.Navigation.Navigated += (_, _) => navigated++;

        world.Navigation.GoToServerPage("never.contributed", id, "web");

        Assert.Same(detail, world.Host.Content);
        Assert.Equal(NavigationDestination.Detail, world.Navigation.CurrentDestination);
        Assert.Equal(0, navigated);
        Assert.Empty(world.PageViews);
    }

    [Fact]
    public async Task WithNoServerPages_OpenServerPage_FromTheDetailPage_DoesNothing()
    {
        var world = await World.CreateAsync(ShellContributions.Empty);
        world.Navigation.GoToServerDetail(world.Fleet.IdOf("web"), ServerDetailOrigin.Servers);
        var detail = Assert.Single(world.DetailViews);

        detail.ViewModel.OpenServerPage("never.contributed");

        Assert.Same(detail, world.Host.Content);
        Assert.False(detail.Disposed);
        Assert.Equal(ServerDetailReturnTarget.None, detail.ViewModel.TakeReturnFocus(out _)); // no row memory pending
    }

    // ---------------------------------------------------------------- the host

    [Fact]
    public void Rows_AreArrangedOnce_ByTheSameRule_WithBuiltInIdsReserved_AndNoFactoryRuns()
    {
        var logger = new RecordingLogger();
        var calls = 0;
        var host = Host(ShellContributions.Empty.WithServerPages([
            Page("b.page", 1, _ => { calls++; return new View(Guid.Empty); }),
            Page("a.page", 1, detail: "DetailKey", icon: "M0,0 L20,20"),
            Page("servers"),
            Page("explore"),
            Page("a.page", -5),
        ]), logger);

        Assert.Equal(["a.page", "b.page"], host.ServerPageEntries.Select(entry => entry.Id));
        Assert.Equal("Title of TitleKey.a.page", host.ServerPageEntries[0].Title);
        Assert.Equal("Title of DetailKey", host.ServerPageEntries[0].Detail);
        Assert.Equal("M0,0 L20,20", host.ServerPageEntries[0].IconPathData);
        Assert.Null(host.ServerPageEntries[1].Detail);
        Assert.Null(host.ServerPageEntries[1].IconPathData);
        Assert.Same(host.ServerPageEntries, host.ServerPageEntries);
        Assert.Equal(0, calls);
        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
    }

    [Fact]
    public void AView_IsCreatedForTheServer_OnEveryVisit_NeverCached()
    {
        var host = Host(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(host.TryCreateServerPage("a.page", first, out var entry, out var one));
        Assert.True(host.TryCreateServerPage("a.page", second, out _, out var two));
        Assert.True(host.TryCreateServerPage("a.page", first, out _, out var three));

        Assert.Equal("a.page", entry.Id);
        Assert.Equal(first, Assert.IsType<View>(one).ServerId);
        Assert.Equal(second, Assert.IsType<View>(two).ServerId);
        Assert.NotSame(one, three);
    }

    [Fact]
    public void AFactoryThatThrows_OrGivesNoShowableView_OpensNothing_LoggedByIdAndTypeOnly()
    {
        var logger = new RecordingLogger();
        var host = Host(ShellContributions.Empty.WithServerPages([
            Page("a.page", factory: _ => throw new InvalidOperationException("secret detail")),
            Page("b.page", factory: _ => "not a view"),
            Page("c.page", factory: _ => null!),
        ]), logger);

        Assert.False(host.TryCreateServerPage("a.page", Guid.NewGuid(), out _, out _));
        Assert.False(host.TryCreateServerPage("b.page", Guid.NewGuid(), out _, out _));
        Assert.False(host.TryCreateServerPage("c.page", Guid.NewGuid(), out _, out _));

        Assert.Equal(["a.page", "b.page", "c.page"], host.ServerPageEntries.Select(entry => entry.Id)); // the rows stay
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error
            && entry.Message.Contains("a.page", StringComparison.Ordinal)
            && entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("b.page", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("c.page", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("secret detail", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- navigation

    [Fact]
    public async Task OpeningAPage_ShowsTheShellsPage_ForThatServer_WithItsViewAndTitle()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);

        Assert.Single(world.DetailViews).ViewModel.OpenServerPage("a.page");

        var page = Assert.Single(world.PageViews);
        Assert.Same(page, world.Host.Content);
        Assert.True(page.WasContentAtLoad, "the page must be the frame content before Load runs");
        Assert.Equal(NavigationDestination.ServerPage, world.Navigation.CurrentDestination);
        Assert.Equal(id, page.ServerId);
        Assert.Equal("web", page.ServerName);
        Assert.Equal("Title of TitleKey.a.page", page.Entry!.Title);
        Assert.Equal(id, Assert.IsType<View>(page.View).ServerId);
        Assert.True(world.DetailViews[0].Disposed); // the detail page it replaced is released
    }

    [Fact]
    public async Task APageThatAsks_NamesTheServerInItsHeading_AndKeepsHistoricoSelected_UntilTheUserLeaves()
    {
        var history = new ServerPage("a.page", 0, "TitleKey.a.page", serverId => new View(serverId))
        {
            TitleNamesServer = true,
            SidebarEntry = ServerPageSidebarEntry.History,
        };
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([history, Page("b.page", 1)]));
        using var shell = new ShellViewModel(world.Navigation);
        var seen = new List<ShellDestination?>();
        world.Navigation.Navigated += (_, _) => seen.Add(shell.SelectedDestination);
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);

        world.DetailViews[0].ViewModel.OpenServerPage("a.page");

        var entry = Assert.Single(world.PageViews).Entry!;
        Assert.Equal("Title of TitleKey.a.page · web", entry.HeadingFor("web"));
        Assert.Equal("Title of TitleKey.a.page", entry.Title); // the way back and the row keep the title alone
        Assert.Equal(ShellDestination.History, shell.SelectedDestination);
        Assert.Equal(ShellDestination.History, seen[^1]); // already right when the sidebar is told

        world.PageViews[0].Back();
        Assert.Equal(ShellDestination.Servers, shell.SelectedDestination);
        Assert.Equal(ServerPageSidebarEntry.Servers, world.Navigation.CurrentServerPageSidebarEntry);

        world.DetailViews[^1].ViewModel.OpenServerPage("b.page");
        Assert.Equal("Title of TitleKey.b.page", world.PageViews[^1].Entry!.HeadingFor("web"));
        Assert.Equal(ShellDestination.Servers, shell.SelectedDestination);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void ABlankServerName_LeavesTheHeadingAsTheTitle(string? name) =>
        Assert.Equal("Title", new ServerPageEntry("a.page", "Title", null, null, TitleNamesServer: true).HeadingFor(name));

    [Fact]
    public async Task TheWayBack_IsThatServersDetailPage_WithItsOrigin_AndFocusReturnsToTheRow()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Overview);
        world.DetailViews[0].ViewModel.OpenServerPage("a.page");
        var page = Assert.Single(world.PageViews);

        page.Back(); // the "‹ server" breadcrumb

        Assert.True(page.Disposed);
        var back = world.DetailViews[^1];
        Assert.Equal(2, world.DetailViews.Count);
        Assert.Same(back, world.Host.Content);
        Assert.Equal(NavigationDestination.Detail, world.Navigation.CurrentDestination);
        Assert.Equal(id, back.ViewModel.Card?.Server.Id);
        Assert.Equal(ServerDetailOrigin.Overview, back.ViewModel.Origin); // never Settings, never the overview by default
        Assert.Equal(ServerDetailReturnTarget.ServerPage, back.ViewModel.TakeReturnFocus(out var pageId));
        Assert.Equal("a.page", pageId);
        Assert.Equal(ServerDetailReturnTarget.None, back.ViewModel.TakeReturnFocus(out _)); // once
    }

    [Fact]
    public async Task TheWayBack_ForAServerThatIsGone_LandsOnItsRememberedOrigin()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);
        world.DetailViews[0].ViewModel.OpenServerPage("a.page");
        world.Dashboard.VisibleServers.Remove(world.Dashboard.VisibleServers.Single(card => card.Server.Id == id));

        Assert.Single(world.PageViews).Back();

        Assert.Equal(typeof(ServersPage), Assert.IsType<PageSentinel>(world.Host.Content).PageType); // its remembered origin
    }

    [Fact]
    public async Task AServerNoLongerListed_NeverGetsThePage_AndNoFactoryRuns()
    {
        var calls = 0;
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page", factory: id => { calls++; return new View(id); })]));

        world.Navigation.GoToServerPage("a.page", Guid.NewGuid(), "gone");

        Assert.Empty(world.PageViews);
        Assert.Equal(0, calls);
        Assert.Equal(typeof(DashboardPage), Assert.IsType<PageSentinel>(world.Host.Content).PageType);
    }

    [Fact]
    public async Task AFactoryThatFails_LeavesTheUserOnTheDetailPage()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page", factory: _ => throw new InvalidOperationException())]));
        world.Navigation.GoToServerDetail(world.Fleet.IdOf("web"), ServerDetailOrigin.Servers);
        var detail = Assert.Single(world.DetailViews);

        detail.ViewModel.OpenServerPage("a.page");

        Assert.Same(detail, world.Host.Content);
        Assert.False(detail.Disposed);
        Assert.Equal(NavigationDestination.Detail, world.Navigation.CurrentDestination);
        Assert.Empty(world.PageViews);
        Assert.Equal(ServerDetailReturnTarget.None, detail.ViewModel.TakeReturnFocus(out _)); // no row memory pending
    }

    [Fact]
    public async Task AnOpeningThatNeverLands_LeavesNoRowMemory_ForTheNextDetailPageOfThatServer()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page", factory: _ => throw new InvalidOperationException())]));
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);
        world.DetailViews[0].ViewModel.OpenServerPage("a.page");

        world.Navigation.GoToServers();
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);

        Assert.Equal(2, world.DetailViews.Count);
        Assert.Equal(ServerDetailReturnTarget.None, world.DetailViews[^1].ViewModel.TakeReturnFocus(out _));
    }

    [Fact]
    public async Task AnExitTheGuardRefuses_LeavesTheUserOnTheDetailPage_WithNoRowMemory()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        world.DetailExitAnswer = false;
        world.Navigation.GoToServerDetail(world.Fleet.IdOf("web"), ServerDetailOrigin.Servers);
        var detail = Assert.Single(world.DetailViews);

        detail.ViewModel.OpenServerPage("a.page");

        Assert.Same(detail, world.Host.Content);
        Assert.Equal(NavigationDestination.Detail, world.Navigation.CurrentDestination);
        Assert.Empty(world.PageViews);
        Assert.Equal(ServerDetailReturnTarget.None, detail.ViewModel.TakeReturnFocus(out _));
    }

    [Fact]
    public async Task AnExitTheGuardAccepts_OpensThePage_AndBackRefocusesTheRow()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        world.DetailExitAnswer = true;
        var id = world.Fleet.IdOf("web");
        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);

        Assert.Single(world.DetailViews).ViewModel.OpenServerPage("a.page");
        Assert.Single(world.PageViews).Back();

        Assert.Equal(ServerDetailReturnTarget.ServerPage, world.DetailViews[^1].ViewModel.TakeReturnFocus(out var pageId));
        Assert.Equal("a.page", pageId);
    }

    [Fact]
    public async Task OpeningThePageAlreadyShown_DoesNothing_ButAnotherServersPageOpens()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        var web = world.Fleet.IdOf("web");
        var other = world.Fleet.IdOf("later");
        world.Navigation.GoToServerPage("a.page", web, "web");
        var shown = world.Host.Content;
        var navigated = 0;
        world.Navigation.Navigated += (_, _) => navigated++;

        world.Navigation.GoToServerPage("a.page", web, "web");
        Assert.Same(shown, world.Host.Content);
        Assert.Equal(0, navigated);

        world.Navigation.GoToServerPage("a.page", other, "later");
        Assert.Equal(2, world.PageViews.Count);
        Assert.Equal(other, world.PageViews[^1].ServerId);
        Assert.True(world.PageViews[0].Disposed);
    }

    [Fact]
    public async Task LeavingThePage_ThenComingBack_BuildsAFreshPageAndView()
    {
        var world = await World.CreateAsync(ShellContributions.Empty.WithServerPages([Page("a.page")]));
        var web = world.Fleet.IdOf("web");
        world.Navigation.GoToServerPage("a.page", web, "web");
        world.Navigation.GoToSettings();
        world.Navigation.GoToServerPage("a.page", web, "web");

        Assert.Equal(2, world.PageViews.Count);
        Assert.NotSame(world.PageViews[0].View, world.PageViews[1].View);
        Assert.Same(world.PageViews[1], world.Host.Content);
    }

    // ---------------------------------------------------------------- the real page and the real detail page

    [Fact]
    public void TheShellsPage_DrawsTheWayBackToTheServer_AndTheTitle_FromExistingPieces()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServerExtensionPage.xaml");
        var code = AppSourceTree.CodeWithoutComments("Views/ServerExtensionPage.xaml.cs");

        Assert.Contains(xaml.Descendants(), e => e.Name.LocalName == "SaBreadcrumb" && (string?)e.Attribute("ParentInvoked") == "OnBreadcrumbParentInvoked");
        Assert.Contains(xaml.Descendants(), e => e.Name.LocalName == "SaHeadingHost" && (string?)e.Attribute("AutomationProperties.HeadingLevel") == "Level1");
        Assert.Contains("_navigation.ReturnToServerDetail(_serverId)", code, StringComparison.Ordinal);
        Assert.Contains("Breadcrumb.ParentText = serverName;", code, StringComparison.Ordinal);
        Assert.Contains("PageTitle.Text = page.HeadingFor(serverName);", code, StringComparison.Ordinal);
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ServerExtensionPage)));
        Assert.DoesNotContain("Unloaded", code, StringComparison.Ordinal); // a theme remount keeps the module's view
        Assert.True(typeof(IServerPageView).IsAssignableFrom(typeof(ServerExtensionPage)));
    }

    [Fact]
    public void TheDetailPage_AddsOneRowPerPage_ToTheExploreCard_AndOnlyOpensThroughTheViewModel()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/ServerDetailPage.xaml.cs");
        var start = code.IndexOf("private void AddServerPageRows(", StringComparison.Ordinal);
        var body = code[start..code.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("foreach (var page in _contributions.ServerPageEntries)", body, StringComparison.Ordinal);
        Assert.Contains("Variant = SaListRowVariant.Rich", body, StringComparison.Ordinal);
        Assert.Contains("row.Click += (_, _) => ViewModel.OpenServerPage(id);", body, StringComparison.Ordinal);
        Assert.Contains("ExploreRows.Children.Add(row);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateView", body, StringComparison.Ordinal); // no module code runs on the detail page

        // The Explore card itself is unchanged: its two built-in rows, in order.
        var xaml = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml");
        var rows = xaml.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "ExploreRows")
            .Elements().Where(e => e.Name.LocalName == "SaListRow")
            .Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name"));
        Assert.Equal(["HistoryRow", "WorkloadsRow"], rows);
    }

    [Theory]
    [InlineData(SaListRowVariant.Rich, "Detail", true)]
    [InlineData(SaListRowVariant.Rich, "", false)] // a page row with no detail: one line, never an empty second one
    [InlineData(SaListRowVariant.Rich, null, false)]
    [InlineData(SaListRowVariant.Simple, "Detail", false)]
    public void ARichRow_ShowsItsSecondLine_OnlyWithText(SaListRowVariant variant, string? detail, bool shown) =>
        Assert.Equal(shown, SaListRowRules.ShowsDetail(variant, detail));

    // ---------------------------------------------------------------- the world

    private sealed class World
    {
        private Ui4TestKit.Harness _kit = null!;

        public Ui4TestKit.Fleet Fleet { get; private init; } = null!;

        public NavigationService Navigation { get; private set; } = null!;

        public NavigationServiceUi4Tests.RecordingHost Host { get; } = new();

        public List<RecordingDetailView> DetailViews { get; } = [];

        public List<RecordingPageView> PageViews { get; } = [];

        public ServerDetailReturnFocus ReturnFocus { get; } = new();

        /// <summary>When set, detail pages created from now on are exit guards answering this at once.</summary>
        public bool? DetailExitAnswer { get; set; }

        public DashboardViewModel Dashboard => _kit.Dashboard;

        public static async Task<World> CreateAsync(ShellContributions contributions)
        {
            var fleet = new Ui4TestKit.Fleet()
                .Add("web", ServerHealth.Healthy, 10, 20, 30)
                .Add("later", ServerHealth.Healthy, 10, 20, 30);
            var world = new World { Fleet = fleet };
            DashboardViewModel? dashboard = null;
            var services = new ServiceCollection();
            services.AddSingleton(_ => dashboard!);
            services.AddSingleton(Host(contributions));
            var provider = services.BuildServiceProvider();
            world.Navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, world.CreatePage);
            world.Navigation.Initialize(world.Host);
            world._kit = Ui4TestKit.Create(fleet, navigationOverride: world.Navigation);
            dashboard = world._kit.Dashboard;
            await dashboard.LoadAsync();
            return world;
        }

        private object CreatePage(Type type)
        {
            if (type == typeof(ServerDetailPage))
            {
                var viewModel = new ServerDetailViewModel(Dashboard, Navigation, new FakeLocalizationService(), returnFocus: ReturnFocus);
                var view = DetailExitAnswer is { } answer ? new GuardedDetailView(viewModel, answer) : new RecordingDetailView(viewModel);
                DetailViews.Add(view);
                return view;
            }

            if (type == typeof(ServerExtensionPage))
            {
                var page = new RecordingPageView(this);
                PageViews.Add(page);
                return page;
            }

            return new PageSentinel(type);
        }
    }

    private sealed class PageSentinel(Type pageType) : IDisposable
    {
        public Type PageType { get; } = pageType;

        public void Dispose()
        {
        }
    }

    private class RecordingDetailView(ServerDetailViewModel viewModel) : IServerDetailView, IDisposable
    {
        public ServerDetailViewModel ViewModel { get; } = viewModel;

        public bool Disposed { get; private set; }

        public void Load(Guid serverId, ServerDetailOrigin origin) => ViewModel.Load(serverId, origin);

        public void Dispose()
        {
            Disposed = true;
            ViewModel.Dispose();
        }
    }

    private sealed class GuardedDetailView(ServerDetailViewModel viewModel, bool answer) : RecordingDetailView(viewModel), INavigationExitGuard
    {
        public Task<bool> ConfirmLeaveAsync() => Task.FromResult(answer);
    }

    /// <summary>Stands in for <see cref="ServerExtensionPage"/>: records what it was given; Back is its breadcrumb.</summary>
    private sealed class RecordingPageView(World world) : IServerPageView, IDisposable
    {
        public Guid ServerId { get; private set; }

        public string? ServerName { get; private set; }

        public ServerPageEntry? Entry { get; private set; }

        public object? View { get; private set; }

        public bool WasContentAtLoad { get; private set; }

        public bool Disposed { get; private set; }

        public void Load(Guid serverId, string serverName, ServerPageEntry page, object view)
        {
            WasContentAtLoad = ReferenceEquals(world.Host.Content, this);
            ServerId = serverId;
            ServerName = serverName;
            Entry = page;
            View = view;
        }

        public void Back() => world.Navigation.ReturnToServerDetail(ServerId);

        public void Dispose() => Disposed = true;
    }
}
