using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 UI12A-STATE-01 (Prism, P1). With the server configuration unreadable (the dashboard's startup diagnosis says
/// Unavailable - the QA scenario <c>config-unavailable</c>), the Servers page showed "Ainda não há servidores" + "Adicionar"
/// and "0 servidores", contradicting the Overview's notice. Now it shows the same notice, no empty state, and a summary
/// that is not "0". A genuinely empty, readable configuration still shows the empty state.
/// </summary>
public sealed class Ui12ServersConfigurationUnavailableTests
{
    [Fact]
    public async Task UnreadableConfiguration_ShowsTheNotice_NotTheEmptyState_NorZeroServers()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), new ResWLocalizationService("pt-PT"));
        kit.Servers.LoadStatusOverride = () => Task.FromResult(ServerLoadStatus.Unavailable);
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        var raised = new List<string?>();
        directory.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await kit.Dashboard.LoadAsync();

        Assert.True(kit.Dashboard.ShowConfigurationUnavailable); // the Overview's own flag
        Assert.True(directory.ShowConfigurationUnavailable);
        Assert.False(directory.ShowEmptyState);
        Assert.DoesNotContain("0", directory.HeaderContextDisplay, StringComparison.Ordinal);
        Assert.Equal("Configuração dos servidores indisponível", directory.HeaderContextDisplay);
        Assert.Contains(nameof(ServersViewModel.ShowConfigurationUnavailable), raised);
        Assert.Contains(nameof(ServersViewModel.ShowEmptyState), raised);
    }

    [Fact]
    public async Task AReadableEmptyConfiguration_StillShowsTheEmptyState()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), new ResWLocalizationService("pt-PT"));
        kit.Servers.LoadStatusOverride = () => Task.FromResult(ServerLoadStatus.Loaded);
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());

        await kit.Dashboard.LoadAsync();

        Assert.False(directory.ShowConfigurationUnavailable);
        Assert.True(directory.ShowEmptyState);
        Assert.Equal("0 servidores", directory.HeaderContextDisplay);
    }

    [Fact]
    public async Task ServersThatDidLoad_StayListed_WithTheNotice_AndTheirRealCount()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy), new ResWLocalizationService("pt-PT"));
        kit.Servers.LoadStatusOverride = () => Task.FromResult(ServerLoadStatus.Unavailable);
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());

        await kit.Dashboard.LoadAsync();

        Assert.True(directory.ShowConfigurationUnavailable);
        Assert.True(directory.ShowTable);
        Assert.False(directory.ShowEmptyState);
        Assert.StartsWith("1 servidor", directory.HeaderContextDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNotice_Clears_WithTheOverviews_OnTheNextConfigurationChange()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), new ResWLocalizationService("pt-PT"));
        kit.Servers.LoadStatusOverride = () => Task.FromResult(ServerLoadStatus.Unavailable);
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        await kit.Dashboard.LoadAsync();

        kit.Servers.RaiseChanged();
        await kit.Dashboard.LoadAsync();

        Assert.False(kit.Dashboard.ShowConfigurationUnavailable);
        Assert.False(directory.ShowConfigurationUnavailable);
        Assert.True(directory.ShowEmptyState);
    }

    [Fact]
    public void ThePage_ShowsTheOverviewsNotice_BoundToTheFlag()
    {
        var page = File.ReadAllText(Architecture.AppSourceTree.Full("Views/ServersPage.xaml"));
        Assert.Contains(
            "<primitives:SaInlineNotice x:Uid=\"ConfigurationUnavailableNotice\" Severity=\"Error\" AnnouncePolitely=\"True\"",
            page,
            StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.ShowConfigurationUnavailable, Mode=OneWay}\"", page, StringComparison.Ordinal);
    }
}
