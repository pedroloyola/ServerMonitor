using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class DashboardDiscoveryCountTests
{
    [Fact]
    public async Task VisibleCount_TracksLoadIgnoreResetAndConfiguredSuppression()
    {
        var alpha = DiscoveredServerViewModelTests.Service("Alpha", "alpha.local", 22);
        var beta = DiscoveredServerViewModelTests.Service("Beta", "beta.local", 22);
        var discovery = new SnapshotDiscoveryService(alpha, beta);
        var servers = new FakeServerService();
        var viewModel = CreateWithoutWinUiRuntime(servers, discovery);

        await viewModel.LoadAsync();

        // Guard against a false pass: LoadAsync swallows exceptions into IsOperationErrorOpen, so a broken
        // load could leave DiscoveredCount at its default and still "match" a wrong expectation (L-2).
        Assert.False(viewModel.IsOperationErrorOpen);
        Assert.Equal(2, viewModel.DiscoveredCount);
        Assert.Equal(2, viewModel.DiscoveredServers.Count);
        Assert.True(viewModel.HasDiscoveredServers);
        Assert.Equal("Devices found: 2", viewModel.DiscoveredCountAutomationName);

        // The discovery service removes an ignored identity before raising its material-change
        // event. Invoke the exact dashboard handler without constructing a WinUI DispatcherQueue.
        discovery.Set(beta);
        RaiseDiscoveredChanged(viewModel);

        Assert.Equal(1, viewModel.DiscoveredCount);
        Assert.Equal("Devices found: 1", viewModel.DiscoveredCountAutomationName);

        // Reset makes still-present announcements visible again.
        discovery.Set(alpha, beta);
        RaiseDiscoveredChanged(viewModel);

        Assert.Equal(2, viewModel.DiscoveredCount);

        // Hidden configured servers still suppress discovery suggestions. Keeping this server
        // hidden also avoids constructing a server card in this runtime-free contract test.
        servers.Servers.Add(DashboardDiscoveryViewModelTests.ServerForTest("ALPHA.LOCAL.", 22) with
        {
            IsHidden = true
        });

        await viewModel.LoadAsync();

        Assert.False(viewModel.IsOperationErrorOpen);
        Assert.Equal(1, viewModel.DiscoveredCount);
        Assert.Equal("Beta", Assert.Single(viewModel.DiscoveredServers).DisplayName);
        Assert.Equal("Devices found: 1", viewModel.DiscoveredCountAutomationName);
    }

    private static DashboardViewModel CreateWithoutWinUiRuntime(
        IServerService serverService,
        IServerDiscoveryService discoveryService)
    {
        var viewModel = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        SetField(viewModel, "_serverService", serverService);
        SetField(viewModel, "_discoveryService", discoveryService);
        SetField(viewModel, "_localizationService", new FakeLocalizationService());
        SetField(viewModel, "_logger", NullLogger<DashboardViewModel>.Instance);
        SetField(viewModel, "_configuredEndpoints", new HashSet<string>(StringComparer.Ordinal));
        SetField(viewModel, "<VisibleServers>k__BackingField", new ObservableCollection<ServerCardViewModel>());
        SetField(viewModel, "<DiscoveredServers>k__BackingField", new ObservableCollection<DiscoveredServerViewModel>());
        return viewModel;
    }

    private static void RaiseDiscoveredChanged(DashboardViewModel viewModel)
    {
        var method = typeof(DashboardViewModel).GetMethod(
            "OnDiscoveredChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(viewModel, [null, EventArgs.Empty]);
    }

    private static void SetField(object instance, string name, object value) =>
        typeof(DashboardViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed class SnapshotDiscoveryService(params DiscoveredService[] discovered)
        : IServerDiscoveryService
    {
        private IReadOnlyList<DiscoveredService> _discovered = discovered;

        public event EventHandler DiscoveredChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<DiscoveredService> GetDiscovered() => _discovered;

        public void Set(params DiscoveredService[] items) => _discovered = items;

        public Task IgnoreAsync(
            ServiceInstanceIdentity identity,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
