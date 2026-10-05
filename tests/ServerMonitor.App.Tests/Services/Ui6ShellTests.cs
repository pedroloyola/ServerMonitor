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
        }
        navigation.GoToHistory();
        Assert.Equal(ShellDestination.History, shell.SelectedDestination);
        Assert.Equal(dashboard.VisibleServers[0].Server.Id, Assert.IsType<History>(host.Content).LastDetail);
        shell.Navigate(ShellDestination.History);
        navigation.GoToSettings(SettingsSection.Data);
        Assert.Equal(ShellDestination.Settings, shell.SelectedDestination);
        Assert.Equal(pages.Count, events);
        Assert.All(pages.OfType<Page>().Where(p => !ReferenceEquals(p, host.Content)), p => Assert.Equal(1, p.Disposals));
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

    private sealed class Host : INavigationHost { public object? Content { get; set; } }
    private class Page : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
    private sealed class Detail : Page, IServerDetailView { public void Load(Guid id, ServerDetailOrigin origin) { } }
    private sealed class History : Page, IHistoryView
    {
        public Guid? LastDetail;
        public void Load(Guid? id, string name, bool fromDetail) { }
        public void LoadSidebar(Guid? lastDetailServer) => LastDetail = lastDetailServer;
    }
}
