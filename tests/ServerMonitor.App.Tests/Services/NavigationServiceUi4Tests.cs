using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.ViewModels;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.4 Cortex r1 (MUST-1, SHOULD-1, SHOULD-3) against the REAL <see cref="NavigationService"/> — not the fake — over a
/// recorded frame host and page factory (WinUI pages cannot be constructed without a XAML runtime, so pages are
/// sentinels; the interim page is a view that hosts the REAL <see cref="ServerDetailViewModel"/>).
/// </summary>
public sealed class NavigationServiceUi4Tests
{
    [Theory]
    [InlineData(ServerDetailOrigin.Overview, typeof(DashboardPage))]
    [InlineData(ServerDetailOrigin.Servers, typeof(ServersPage))]
    public async Task AServerNoLongerListed_LandsOnTheOrigin_AndNeverGetsTheInterimPage(ServerDetailOrigin origin, Type expected)
    {
        var world = await World.CreateAsync();

        world.Navigation.GoToServerDetail(Guid.NewGuid(), origin);

        Assert.Equal(expected, Assert.IsType<PageSentinel>(world.Host.Content).PageType);
        Assert.Empty(world.DetailViews);
    }

    [Fact]
    public async Task AListedServer_GetsTheInterimPage_WithContentAssignedBeforeLoad()
    {
        var world = await World.CreateAsync();
        var id = world.Fleet.IdOf("web");

        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Overview);

        var view = Assert.Single(world.DetailViews);
        Assert.Same(view, world.Host.Content);
        Assert.True(view.WasContentAtLoad, "the page must be the frame content before Load runs");
        Assert.Same(world.Dashboard.VisibleServers.Single(card => card.Server.Id == id), view.ViewModel.Card);
    }

    /// <summary>The MUST-1 sequence: the server vanishes after the check, so the page leaves DURING Load. The final frame
    /// content must be the origin — never the dead interim page the old "Load, then Content" order left behind.</summary>
    [Fact]
    public async Task AServerVanishingDuringLoad_EndsOnTheOrigin_NotOnADeadPage()
    {
        var world = await World.CreateAsync();
        var id = world.Fleet.IdOf("web");
        world.BeforeLoad = () => world.Dashboard.VisibleServers.Remove(world.Dashboard.VisibleServers.Single(card => card.Server.Id == id));

        world.Navigation.GoToServerDetail(id, ServerDetailOrigin.Servers);

        Assert.Equal(typeof(ServersPage), Assert.IsType<PageSentinel>(world.Host.Content).PageType);
        Assert.True(Assert.Single(world.DetailViews).Disposed);
    }

    [Fact]
    public async Task ReplacingAPerVisitPage_ReleasesIt_EvenIfItNeverLoaded()
    {
        var world = await World.CreateAsync();

        world.Navigation.GoToServers();
        var servers = Assert.IsType<PageSentinel>(world.Host.Content);
        world.Navigation.GoToServerDetail(world.Fleet.IdOf("web"), ServerDetailOrigin.Servers);
        var detail = Assert.Single(world.DetailViews);
        world.Navigation.GoToSettings();

        Assert.True(servers.Disposed);
        Assert.True(detail.Disposed);
        Assert.False(Assert.IsType<PageSentinel>(world.Host.Content).Disposed);
    }

    [Fact]
    public async Task NavigatingAway_IsAnnounced_ButReachingTheOverviewIsNot()
    {
        var world = await World.CreateAsync();
        var raised = 0;
        world.Navigation.NavigatedAwayFromOverview += (_, _) => raised++;

        world.Navigation.GoToDashboard();
        Assert.Equal(0, raised);
        world.Navigation.GoToServers();
        world.Navigation.GoToSettings();
        world.Navigation.GoToServerDetail(world.Fleet.IdOf("web"), ServerDetailOrigin.Overview);
        Assert.Equal(3, raised);
    }

    /// <summary>SHOULD-1: a deep-link waiting for a hidden server is cancelled when the user goes elsewhere; a later
    /// unhide / restore brings the server back WITHOUT yanking the user to the interim page.</summary>
    [Fact]
    public async Task APendingDeepLink_IsDroppedByAUserNavigation_AndALateUnhideDoesNotNavigate()
    {
        var world = await World.CreateAsync(hiddenTarget: true);
        var target = world.Fleet.IdOf("later");

        world.Dashboard.FocusServer(target);
        world.Navigation.GoToSettings();
        world.UnhideAndReload("later");
        await world.Dashboard.LoadAsync();

        Assert.Empty(world.DetailViews);
        Assert.Equal(typeof(SettingsPage), Assert.IsType<PageSentinel>(world.Host.Content).PageType);
    }

    [Fact]
    public async Task APendingDeepLink_StillOpensItsServer_WhenTheUserStaysOnTheOverview()
    {
        var world = await World.CreateAsync(hiddenTarget: true);
        var target = world.Fleet.IdOf("later");

        world.Navigation.GoToDashboard(); // the shell's own startup / activation navigation
        world.Dashboard.FocusServer(target);
        world.UnhideAndReload("later");
        await world.Dashboard.LoadAsync();

        Assert.Same(Assert.Single(world.DetailViews), world.Host.Content);
    }

    private sealed class World
    {
        private Ui4TestKit.Harness _kit = null!;

        public Ui4TestKit.Fleet Fleet { get; private init; } = null!;

        public NavigationService Navigation { get; private set; } = null!;

        public RecordingHost Host { get; } = new();

        public List<RecordingDetailView> DetailViews { get; } = [];

        public Action? BeforeLoad { get; set; }

        public DashboardViewModel Dashboard => _kit.Dashboard;

        public static async Task<World> CreateAsync(bool hiddenTarget = false)
        {
            var fleet = new Ui4TestKit.Fleet()
                .Add("web", ServerHealth.Healthy, 10, 20, 30)
                .Add("later", ServerHealth.Healthy, 10, 20, 30, hidden: hiddenTarget);
            var world = new World { Fleet = fleet };
            DashboardViewModel? dashboard = null;
            var services = new ServiceCollection();
            services.AddSingleton(_ => dashboard!);
            var provider = services.BuildServiceProvider();
            world.Navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, world.CreatePage);
            world.Navigation.Initialize(world.Host);
            world._kit = Ui4TestKit.Create(fleet, navigationOverride: world.Navigation);
            dashboard = world._kit.Dashboard;
            await dashboard.LoadAsync();
            return world;
        }

        public void UnhideAndReload(string name)
        {
            var index = _kit.Servers.Servers.FindIndex(server => server.Name == name);
            _kit.Servers.Servers[index] = _kit.Servers.Servers[index] with { IsHidden = false };
        }

        private object CreatePage(Type type)
        {
            if (type == typeof(ServerDetailPage))
            {
                var view = new RecordingDetailView(this, new ServerDetailViewModel(Dashboard, Navigation, new FakeLocalizationService()));
                DetailViews.Add(view);
                return view;
            }

            return new PageSentinel(type);
        }

        internal void InvokeBeforeLoad() => BeforeLoad?.Invoke();
    }

    internal sealed class RecordingHost : INavigationHost
    {
        public object? Content { get; set; }
    }

    private sealed class PageSentinel(Type pageType) : IDisposable
    {
        public Type PageType { get; } = pageType;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingDetailView(World world, ServerDetailViewModel viewModel) : IServerDetailView, IDisposable
    {
        public ServerDetailViewModel ViewModel { get; } = viewModel;

        public bool WasContentAtLoad { get; private set; }

        public bool Disposed { get; private set; }

        public void Load(Guid serverId, ServerDetailOrigin origin)
        {
            WasContentAtLoad = ReferenceEquals(world.Host.Content, this);
            world.InvokeBeforeLoad();
            ViewModel.Load(serverId, origin);
        }

        public void Dispose()
        {
            Disposed = true;
            ViewModel.Dispose();
        }
    }
}
