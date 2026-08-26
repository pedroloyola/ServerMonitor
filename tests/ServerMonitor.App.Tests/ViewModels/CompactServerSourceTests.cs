using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// The compact widget renders exactly <see cref="DashboardViewModel.VisibleServers"/> — the same
/// collection the standard dashboard uses — so hidden servers and discovery-only suggestions never
/// appear in compact mode, while a restored server reappears reactively. These tests pin that data
/// source (M9 §26/§32/§56); the live-metric and per-state rendering are already covered by the
/// shared ServerCardViewModel/QA-health tests the compact card reuses unchanged.
/// </summary>
public sealed class CompactServerSourceTests
{
    [Fact]
    public async Task VisibleServers_ExcludesHidden_AndDiscoveryStaysSeparate()
    {
        var visible = ServerNamed("visible-one", hidden: false);
        var hidden = ServerNamed("hidden-one", hidden: true);
        var vm = CreateDashboard(new FakeServerService { Servers = { visible, hidden } });

        await vm.LoadAsync();

        var card = Assert.Single(vm.VisibleServers);
        Assert.Equal(visible.Id, card.Server.Id);
        Assert.True(vm.HasVisibleServers);
        // Discovery-only devices are never surfaced by the compact source.
        Assert.Empty(vm.DiscoveredServers);
    }

    [Fact]
    public async Task RestoringHiddenServer_MakesItReappearReactively()
    {
        var server = ServerNamed("toggle", hidden: true);
        var service = new FakeServerService { Servers = { server } };
        var vm = CreateDashboard(service);

        await vm.LoadAsync();
        Assert.Empty(vm.VisibleServers);

        // Simulate a restore: the server is no longer hidden and the service reloads.
        service.Servers[0] = server with { IsHidden = false };
        await vm.LoadAsync();

        Assert.Single(vm.VisibleServers);
        Assert.True(vm.HasVisibleServers);
    }

    private static DashboardViewModel CreateDashboard(IServerService serverService)
    {
        // DashboardViewModel captures a WinUI DispatcherQueue in its constructor, which requires a
        // live WinUI runtime; build it uninitialized (as the other dashboard unit tests do) and
        // populate the fields LoadAsync touches, including the two collection backing fields that
        // the skipped auto-property initializers would normally set.
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        SetField(vm, "_serverService", serverService);
        SetField(vm, "_connectionStateStore", new FakeConnectionStateStore());
        SetField(vm, "_metricsStore", new FakeServerMetricsStore());
        SetField(vm, "_monitoringStateStore", new ServerMonitoringStateStore());
        SetField(vm, "_monitoringEngine", new FakeMonitoringEngine());
        SetField(vm, "_discoveryService", new EmptyDiscoveryService());
        SetField(vm, "_localizationService", new FakeLocalizationService());
        SetField(vm, "_logger", NullLogger<DashboardViewModel>.Instance);
        SetField(vm, "_configuredEndpoints", new HashSet<string>(StringComparer.Ordinal));
        SetField(vm, "<VisibleServers>k__BackingField", new ObservableCollection<ServerCardViewModel>());
        SetField(vm, "<DiscoveredServers>k__BackingField", new ObservableCollection<DiscoveredServerViewModel>());
        return vm;
    }

    private static void SetField(object instance, string name, object value) =>
        typeof(DashboardViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private static Server ServerNamed(string name, bool hidden) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.local",
        Port = 22,
        Username = "monitor",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.SshKey,
        PrivateKeyPath = Path.Combine(Path.GetTempPath(), "id_compact_test"),
        IsHidden = hidden,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private sealed class EmptyDiscoveryService : IServerDiscoveryService
    {
        public event EventHandler DiscoveredChanged { add { } remove { } }

        public IReadOnlyList<DiscoveredService> GetDiscovered() => [];

        public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
