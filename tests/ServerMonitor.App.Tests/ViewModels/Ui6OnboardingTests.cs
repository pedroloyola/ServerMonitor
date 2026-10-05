using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class Ui6OnboardingTests
{
    [Theory]
    [InlineData(ServerLoadStatus.NotFound, true, false, true)]
    [InlineData(ServerLoadStatus.Loaded, true, false, false)]
    [InlineData(ServerLoadStatus.Unavailable, true, false, false)]
    [InlineData(ServerLoadStatus.NotFound, false, false, false)]
    [InlineData(ServerLoadStatus.NotFound, true, true, false)]
    public async Task Trigger_IsDerivedFromStatusNormalStartAndActivation(ServerLoadStatus status, bool normal, bool activation, bool expected)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Servers.LoadStatus = status;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, dashboard);
        if (activation) vm.SuppressForActivation();
        await vm.OnMainWindowShownAsync(normal);
        Assert.Equal(expected, vm.IsVisible);
        Assert.Equal(status == ServerLoadStatus.Unavailable, vm.IsConfigurationUnavailable);
        vm.Dismiss();
        await vm.OnMainWindowShownAsync(true);
        Assert.False(vm.IsVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StepThree_UsesOnlyExistingEditorCommand_AndDismissesBeforeDialog(bool import)
    {
        var dialog = new Dialog();
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), dialogs: dialog);
        using var dashboard = kit.Dashboard;
        kit.Servers.LoadStatus = ServerLoadStatus.NotFound;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, dashboard);
        dialog.BeforeOpen = () => { Assert.False(vm.IsVisible); Assert.Equal(1, kit.Navigation.DashboardCount); };
        await vm.OnMainWindowShownAsync(true);
        vm.Back(); Assert.Equal(1, vm.Step);
        await vm.AddServerCommand.ExecuteAsync(); Assert.Equal(0, dialog.Add);
        vm.Next(); vm.Next(); vm.Next(); Assert.Equal(3, vm.Step);
        vm.Back(); Assert.Equal(2, vm.Step); vm.Next();
        await (import ? vm.ImportFromSshCommand : vm.AddServerCommand).ExecuteAsync();
        Assert.Equal(import ? 0 : 1, dialog.Add);
        Assert.Equal(import ? 1 : 0, dialog.Import);
        await vm.OnMainWindowShownAsync(true); Assert.False(vm.IsVisible);
        await vm.AddServerCommand.ExecuteAsync(); Assert.Equal(import ? 0 : 1, dialog.Add);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationOrDismissal_DuringLoadPreventsLateOnboarding(bool activation)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        var pending = new TaskCompletionSource<ServerLoadStatus>();
        kit.Servers.LoadStatusOverride = () => pending.Task;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, dashboard);
        var load = vm.OnMainWindowShownAsync(true);
        if (activation) vm.RecordActivation(); else vm.Dismiss();
        pending.SetResult(ServerLoadStatus.NotFound);
        await load;
        Assert.False(vm.IsVisible);
    }

    private sealed class Dialog : IServerDialogService
    {
        public int Add, Import;
        public Action? BeforeOpen;
        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) { BeforeOpen?.Invoke(); Add++; return Task.FromResult<ServerEditorResult?>(null); }
        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() { BeforeOpen?.Invoke(); Import++; return Task.FromResult<ServerEditorResult?>(null); }
        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => throw new NotSupportedException();
        public Task<bool> ConfirmRemoveAsync(Server server) => throw new NotSupportedException();
    }
}
