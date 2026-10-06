using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Windowing;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class Ui6OnboardingTests
{
    [Fact]
    public async Task NavigationAwayAndBackDuringDiagnosis_DismissesForProcess()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        var pending = new TaskCompletionSource<ServerLoadStatus>();
        kit.Servers.LoadStatusOverride = () => pending.Task;
        using var vm = new OnboardingViewModel(kit.Servers, kit.Navigation,
            new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
        var load = vm.OnMainWindowShownAsync(true);
        kit.Navigation.GoToSettings();
        kit.Navigation.GoToDashboard();
        pending.SetResult(ServerLoadStatus.NotFound);
        await load;
        Assert.False(vm.IsVisible);
    }

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
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatus = status;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
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
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatus = ServerLoadStatus.NotFound;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
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
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        var pending = new TaskCompletionSource<ServerLoadStatus>();
        kit.Servers.LoadStatusOverride = () => pending.Task;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
        var load = vm.OnMainWindowShownAsync(true);
        if (activation) vm.RecordActivation(); else vm.Dismiss();
        pending.SetResult(ServerLoadStatus.NotFound);
        await load;
        Assert.False(vm.IsVisible);
    }

    [Theory]
    [InlineData(null, LaunchMode.Foreground, true)]
    [InlineData(NavigationDestination.Settings, LaunchMode.Foreground, false)]
    [InlineData(null, LaunchMode.Background, false)]
    public void Startup_CapturesDestinationAndLaunchMode(NavigationDestination? destination, LaunchMode mode, bool expected) =>
        Assert.Equal(expected, OnboardingStartup.IsNormalStart(destination, mode));

    [Fact]
    public async Task UnexpectedDiagnosisFailure_IsObservedUnavailableAndLogged()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatusOverride = () => throw new InvalidOperationException("synthetic");
        var log = new RecordingLogger();
        using var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), log);
        await vm.OnMainWindowShownAsync(true);
        Assert.False(vm.IsVisible);
        Assert.True(vm.IsConfigurationUnavailable);
        Assert.Single(log.Messages);
        Assert.Contains(nameof(InvalidOperationException), log.Messages[0]);
    }

    [Fact]
    public async Task ExternalNavigation_DismissesForProcess_OverviewDoesNot_AndDisposeUnsubscribes()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatus = ServerLoadStatus.NotFound;
        var before = kit.Navigation.NavigatedSubscribers;
        var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
        Assert.Equal(before + 1, kit.Navigation.NavigatedSubscribers);
        await vm.OnMainWindowShownAsync(true);
        kit.Navigation.GoToDashboard(); Assert.True(vm.IsVisible);
        kit.Navigation.GoToSettings(); Assert.False(vm.IsVisible);
        await vm.OnMainWindowShownAsync(true); Assert.False(vm.IsVisible);
        vm.Dispose(); vm.Dispose();
        Assert.Equal(before, kit.Navigation.NavigatedSubscribers);
    }

    [Fact]
    public async Task ActivationRecordedBeforeGraphConstruction_SuppressesOnboarding()
    {
        var activation = new ActivationLatch(); activation.Record();
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatus = ServerLoadStatus.NotFound;
        using var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), activation, NullLogger<OnboardingViewModel>.Instance);
        await vm.OnMainWindowShownAsync(true);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task CompactFirstRun_RemainsPendingUntilStandard_WithoutAnotherRead()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatus = ServerLoadStatus.NotFound;
        using var vm = new OnboardingViewModel(kit.Servers, kit.Navigation, new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
        vm.SetWindowMode(WindowMode.Compact);
        await vm.OnMainWindowShownAsync(true);
        Assert.False(vm.IsVisible);
        kit.Servers.LoadStatusOverride = () => throw new InvalidOperationException("must not read again");
        vm.SetWindowMode(WindowMode.Standard); Assert.True(vm.IsVisible);
        vm.Dismiss(); vm.SetWindowMode(WindowMode.Compact); vm.SetWindowMode(WindowMode.Standard);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task LateDiagnosis_AfterNavigationToSettings_NeverShowsOnboarding()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        var pending = new TaskCompletionSource<ServerLoadStatus>();
        kit.Servers.LoadStatusOverride = () => pending.Task;
        using var vm = new OnboardingViewModel(kit.Servers, kit.Navigation,
            new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand),
            new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
        var load = vm.OnMainWindowShownAsync(true);
        kit.Navigation.GoToSettings();
        pending.SetResult(ServerLoadStatus.NotFound);
        await load;
        Assert.False(vm.IsVisible);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StepThree_OpensTheEditorPage_OverTheVisaoGeral(bool import)
    {
        // UI.7 B-5: the editor is a page whose origin is the Visão geral. PreparingEditor (focus the overview H1 before a
        // modal captured its return origin) is retired with its contract: the onboarding hides, the overview is shown,
        // and only then the existing command opens the editor.
        var dialog = new Dialog();
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), dialogs: dialog);
        using var dashboard = kit.Dashboard;
        kit.Navigation.CurrentDestination = NavigationDestination.Overview;
        kit.Servers.LoadStatus = ServerLoadStatus.NotFound;
        using var vm = new OnboardingViewModel(kit.Servers, kit.Navigation,
            new OnboardingActions(dashboard.AddServerCommand, dashboard.ImportFromSshCommand), new ActivationLatch(), NullLogger<OnboardingViewModel>.Instance);
        dialog.BeforeOpen = () =>
        {
            Assert.False(vm.IsVisible);
            Assert.Equal(NavigationDestination.Overview, kit.Navigation.CurrentDestination);
        };
        await vm.OnMainWindowShownAsync(true);
        vm.Next(); vm.Next();

        await (import ? vm.ImportFromSshCommand : vm.AddServerCommand).ExecuteAsync();

        Assert.Equal(import ? 0 : 1, dialog.Add);
        Assert.Equal(import ? 1 : 0, dialog.Import);
        Assert.DoesNotContain("PreparingEditor", AppSourceTree.CodeWithoutComments("ViewModels/OnboardingViewModel.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareOnboardingEditorAsync", AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger<OnboardingViewModel>
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class Dialog : IEditorScript
    {
        public int Add, Import;
        public Action? BeforeOpen;
        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) { BeforeOpen?.Invoke(); Add++; return Task.FromResult<ServerEditorResult?>(null); }
        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() { BeforeOpen?.Invoke(); Import++; return Task.FromResult<ServerEditorResult?>(null); }
        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => throw new NotSupportedException();
        public Task<bool> ConfirmRemoveAsync(Server server) => throw new NotSupportedException();
    }
}
