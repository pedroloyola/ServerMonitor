using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.ViewModels;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Services;

public sealed class Ui6ShellTests
{
    [Theory]
    [InlineData(NavigationDestination.Overview, ShellDestination.Overview)]
    [InlineData(NavigationDestination.Servers, ShellDestination.Servers)]
    [InlineData(NavigationDestination.Detail, ShellDestination.Servers)]
    [InlineData(NavigationDestination.Workloads, ShellDestination.Servers)]
    [InlineData(NavigationDestination.History, ShellDestination.History)]
    [InlineData(NavigationDestination.Settings, ShellDestination.Settings)]
    [InlineData(NavigationDestination.SettingsData, ShellDestination.Settings)]
    public void SelectionMapping_CoversEveryDestination(NavigationDestination destination, ShellDestination expected)
    {
        var navigation = new ServerMonitor.App.Tests.Fakes.FakeNavigationService { CurrentDestination = destination };
        using var shell = new ShellViewModel(navigation);
        Assert.Equal(expected, shell.SelectedDestination);
    }

    [Fact]
    public async Task RouterOwnsSelection_ForEveryDestination_AndReclickDoesNotRebuild()
    {
        var host = new Host();
        var pages = new List<object>();
        var services = new ServiceCollection();
        Ui4TestKit.Harness? kit = null;
        services.AddSingleton(_ => kit!.Dashboard);
        using var provider = services.BuildServiceProvider();
        var navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, type =>
        {
            object page = type == typeof(ServerDetailPage) ? new Detail() : type == typeof(HistoryPage) ? new History() : new Page();
            pages.Add(page); return page;
        });
        navigation.Initialize(host);
        kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy), navigationOverride: navigation);
        using var dashboard = kit.Dashboard;
        await dashboard.LoadAsync();
        using var shell = new ShellViewModel(navigation);
        var events = 0;
        navigation.Navigated += (_, _) => { Assert.NotNull(host.Content); events++; };
        Assert.Null(shell.SelectedDestination);
        navigation.EnsureInitialNavigation();
        Assert.Equal(ShellDestination.Overview, shell.SelectedDestination);
        shell.Navigate(ShellDestination.Servers);
        Assert.Equal(NavigationDestination.Servers, navigation.CurrentDestination);
        var shown = host.Content;
        shell.Navigate(ShellDestination.Servers);
        navigation.GoToServers();
        Assert.Same(shown, host.Content);
        foreach (var origin in new[] { ServerDetailOrigin.Overview, ServerDetailOrigin.Servers })
        {
            navigation.GoToServerDetail(dashboard.VisibleServers[0].Server.Id, origin);
            Assert.Equal(ShellDestination.Servers, shell.SelectedDestination);
            shell.Navigate(ShellDestination.Servers);
            Assert.Equal(NavigationDestination.Servers, navigation.CurrentDestination);
        }
        navigation.NavigateTo<WorkloadsPage>();
        shell.Navigate(ShellDestination.Servers);
        Assert.Equal(NavigationDestination.Servers, navigation.CurrentDestination);
        navigation.GoToHistory(dashboard.VisibleServers[0].Server.Id, "web");
        Assert.True(Assert.IsType<History>(host.Content).FromDetail);
        shell.Navigate(ShellDestination.History);
        Assert.False(Assert.IsType<History>(host.Content).FromDetail);
        shown = host.Content;
        navigation.GoToHistory();
        Assert.Same(shown, host.Content);
        Assert.Equal(ShellDestination.History, shell.SelectedDestination);
        Assert.Equal(dashboard.VisibleServers[0].Server.Id, Assert.IsType<History>(host.Content).LastDetail);
        shell.Navigate(ShellDestination.History);
        navigation.GoToSettings(SettingsSection.Data);
        Assert.Equal(ShellDestination.Settings, shell.SelectedDestination);
        shell.Navigate(ShellDestination.Settings);
        Assert.Equal(NavigationDestination.Settings, navigation.CurrentDestination);
        Assert.Equal(pages.Count, events);
        Assert.All(pages.OfType<Page>().Where(p => !ReferenceEquals(p, host.Content)), p => Assert.Equal(1, p.Disposals));
    }

    [Theory]
    [InlineData(NavigationDestination.Detail, ServerDetailOrigin.Overview, ShellDestination.Servers)]
    [InlineData(NavigationDestination.Detail, ServerDetailOrigin.Servers, ShellDestination.Servers)]
    [InlineData(NavigationDestination.Workloads, ServerDetailOrigin.Servers, ShellDestination.Servers)]
    [InlineData(NavigationDestination.SettingsData, ServerDetailOrigin.Servers, ShellDestination.Settings)]
    public async Task Sidebar_LeavesProjectedSubpageForExactRoot(NavigationDestination initial, ServerDetailOrigin origin, ShellDestination root)
    {
        var services = new ServiceCollection();
        Ui4TestKit.Harness? kit = null;
        services.AddSingleton(_ => kit!.Dashboard);
        using var provider = services.BuildServiceProvider();
        var navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance,
            type => type == typeof(ServerDetailPage) ? new Detail() : new Page());
        var host = new Host(); navigation.Initialize(host);
        kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy), navigationOverride: navigation);
        using var dashboard = kit.Dashboard; await dashboard.LoadAsync();
        using var shell = new ShellViewModel(navigation);
        if (initial == NavigationDestination.Detail) navigation.GoToServerDetail(dashboard.VisibleServers[0].Server.Id, origin);
        else if (initial == NavigationDestination.Workloads) navigation.NavigateTo<WorkloadsPage>();
        else navigation.GoToSettings(SettingsSection.Data);
        Assert.Equal(initial, navigation.CurrentDestination);
        Assert.Equal(root, shell.SelectedDestination);
        shell.Navigate(root);
        Assert.Equal(root == ShellDestination.Settings ? NavigationDestination.Settings : NavigationDestination.Servers, navigation.CurrentDestination);
    }

    [Fact]
    public void SidebarHistory_ReplacesDetailVariant_AndReclickKeepsSidebarVariant()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, _ => new History());
        var host = new Host(); navigation.Initialize(host);
        navigation.GoToHistory(Guid.NewGuid(), "test");
        var detail = Assert.IsType<History>(host.Content); Assert.True(detail.FromDetail);
        // Direct API here isolates the router's variant guard from the shell's projection guard.
        navigation.GoToHistory();
        var sidebar = Assert.IsType<History>(host.Content);
        Assert.NotSame(detail, sidebar); Assert.False(sidebar.FromDetail);
        Assert.Equal(1, detail.Disposals);
        navigation.GoToHistory(); Assert.Same(sidebar, host.Content);
    }

    [Fact]
    public void ColdStart_PreservesSettingsAndOneShotFocus()
    {
        var navigation = new NavigationService(new ServiceCollection().BuildServiceProvider(), NullLogger<NavigationService>.Instance, _ => new Page());
        var host = new Host(); navigation.Initialize(host);
        navigation.GoToSettings();
        var settings = host.Content;
        navigation.RequestBackgroundSettingsFocus();
        navigation.EnsureInitialNavigation();
        Assert.Same(settings, host.Content);
        Assert.Equal(NavigationDestination.Settings, navigation.CurrentDestination);
        Assert.True(navigation.ConsumeBackgroundSettingsFocus());
        Assert.False(navigation.ConsumeBackgroundSettingsFocus());
    }

    [Fact]
    public void ShellSelection_HasNoSetter_AndSubscriptionIsReleased()
    {
        Assert.Null(typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.SelectedDestination))!.SetMethod);
        var navigation = new NavigationService(new ServiceCollection().BuildServiceProvider(), NullLogger<NavigationService>.Instance, _ => new Page());
        navigation.Initialize(new Host());
        var shell = new ShellViewModel(navigation);
        var changes = 0; shell.PropertyChanged += (_, _) => changes++;
        navigation.GoToDashboard(); Assert.Equal(1, changes);
        shell.Dispose(); navigation.GoToServers(); Assert.Equal(1, changes);
    }

    [Fact]
    public void Show_DisposesAndSignalsDepartureBeforeNavigated_EvenWhenObserverThrows()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, _ => new Page());
        var host = new Host(); navigation.Initialize(host);
        navigation.GoToDashboard();
        var previous = Assert.IsType<Page>(host.Content);
        var departed = false;
        navigation.NavigatedAwayFromOverview += (_, _) => { Assert.Equal(1, previous.Disposals); departed = true; };
        navigation.Navigated += (_, _) =>
        {
            Assert.NotSame(previous, host.Content);
            Assert.Equal(1, previous.Disposals);
            Assert.True(departed);
            throw new InvalidOperationException("observer");
        };
        Assert.Throws<InvalidOperationException>(() => navigation.GoToServers());
        Assert.True(departed);
        Assert.Equal(1, previous.Disposals);
    }

    [Fact]
    public void RootReclicks_DoNotRebuildOrPublishNavigation()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var settings = new Page();
        var created = 0;
        var navigation = new NavigationService(provider, NullLogger<NavigationService>.Instance, type =>
        {
            if (type == typeof(SettingsPage)) return settings;
            created++;
            return type == typeof(HistoryPage) ? new History() : new Page();
        });
        var host = new Host(); navigation.Initialize(host);
        using var shell = new ShellViewModel(navigation);
        var events = 0; navigation.Navigated += (_, _) => events++;
        foreach (var destination in Enum.GetValues<ShellDestination>())
        {
            shell.Navigate(destination);
            var shown = host.Content; var count = created; var publications = events;
            shell.Navigate(destination);
            Assert.Same(shown, host.Content);
            Assert.Equal(count, created);
            Assert.Equal(publications, events);
        }
    }

    private sealed class Host : INavigationHost { public object? Content { get; set; } }
    private class Page : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
    private sealed class Detail : Page, IServerDetailView { public void Load(Guid id, ServerDetailOrigin origin) { } }
    private sealed class History : Page, IHistoryView
    {
        public Guid? LastDetail;
        public bool FromDetail;
        public void Load(Guid? id, string name, bool fromDetail) => FromDetail = fromDetail;
        public void LoadSidebar(Guid? lastDetailServer) => LastDetail = lastDetailServer;
    }
}
