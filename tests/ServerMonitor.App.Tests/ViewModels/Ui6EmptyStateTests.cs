using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class Ui6EmptyStateTests
{
    [Theory]
    [InlineData(ServerLoadStatus.NotFound)]
    [InlineData(ServerLoadStatus.Loaded)]
    public async Task EmptyAndHiddenStates_FollowLiveList_NotStartupSnapshot(ServerLoadStatus status)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var vm = kit.Dashboard;
        kit.Servers.LoadStatus = status;
        await vm.LoadAsync();
        Assert.True(vm.ShowFirstServerState);
        Assert.False(vm.ShowAllHiddenState);
        var server = new Ui4TestKit.Fleet().Add("hidden", ServerHealth.Healthy, hidden: true).Entries[0].Server;
        kit.Servers.Servers.Add(server);
        kit.Servers.RaiseChanged();
        Assert.False(vm.ShowFirstServerState);
        Assert.True(vm.ShowAllHiddenState);
        vm.RestoreHiddenServersCommand.Execute(null);
        Assert.Equal(NavigationDestination.SettingsData, kit.Navigation.CurrentDestination);
        kit.Servers.Servers[0] = server with { IsHidden = false };
        kit.Servers.RaiseChanged();
        Assert.False(vm.ShowFirstServerState);
        Assert.False(vm.ShowAllHiddenState);
        Assert.True(vm.HasVisibleServers);
        kit.Servers.Servers.Clear();
        kit.Servers.RaiseChanged();
        Assert.True(vm.ShowFirstServerState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableNotice_HidesOnNextChange_EvenIfDiagnosisCompletesLate(bool late)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var vm = kit.Dashboard;
        var pending = new TaskCompletionSource<ServerLoadStatus>();
        var reads = 0;
        kit.Servers.LoadStatusOverride = () => { reads++; return pending.Task; };
        var load = vm.LoadAsync();
        if (!late)
        {
            pending.SetResult(ServerLoadStatus.Unavailable);
            await load;
            Assert.True(vm.ShowConfigurationUnavailable);
            Assert.False(vm.ShowFirstServerState);
            Assert.False(vm.ShowAllHiddenState);
        }
        kit.Servers.RaiseChanged();
        if (late) pending.SetResult(ServerLoadStatus.Unavailable);
        await load;
        await vm.LoadAsync();
        Assert.False(vm.ShowConfigurationUnavailable);
        Assert.True(vm.ShowFirstServerState);
        Assert.Equal(1, reads);
    }
}
