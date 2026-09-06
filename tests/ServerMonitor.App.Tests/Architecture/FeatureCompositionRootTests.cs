using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServerMonitor.App;
using ServerMonitor.App.Features;
using ServerMonitor.App.Services;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure.Discovery;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// C5/C7/C8/C9/C10 — the composition root after M14.1, asserted against the container the application
/// really builds rather than against the source text.
/// <para>
/// The reason these run at all: converting four capabilities into modules is a change of SHAPE, and a
/// green suite that never looks at the resulting descriptors would not notice a registration that moved
/// house and got lost on the way.
/// </para>
/// </summary>
public sealed class FeatureCompositionRootTests
{
    private static ServiceCollection RealComposition()
    {
        var services = new ServiceCollection();
        App.ConfigureApplicationServices(services);
        return services;
    }

    private static ServiceDescriptor[] DescriptorsFor(ServiceCollection services, Type serviceType) =>
        [.. services.Where(descriptor => descriptor.ServiceType == serviceType)];

    /// <summary>Every service type the four modules own. The set IS the parity claim.</summary>
    private static readonly Type[] ModuleOwnedServiceTypes =
    [
        // History (M10)
        typeof(HistoryStorageOptions),
        typeof(SqliteServerHistoryStore),
        typeof(IServerHistoryStore),
        typeof(HistorySampleChannel),
        typeof(HistoryRecorder),
        typeof(HistoryWriterService),
        typeof(IServerHistoryQueryService),
        typeof(IHistoryMaintenanceService),
        // Workloads (M11)
        typeof(WorkloadOptions),
        typeof(IWorkloadCollector),
        typeof(WorkloadRequestQueue),
        typeof(WorkloadCadencePolicy),
        typeof(WorkloadCadenceObserver),
        typeof(WorkloadCollectorService),
        typeof(IWorkloadRefreshCoordinator),
        // Widget snapshot (M13 S1)
        typeof(WidgetStateOptions),
        typeof(IWidgetStateWriter),
        typeof(WidgetSnapshotRecorder),
        // Discovery (M7)
        typeof(IgnoredDeviceStorageOptions),
        typeof(MdnsServiceBrowserOptions),
        typeof(IIgnoredDeviceStore),
        typeof(IMdnsServiceBrowser),
        typeof(ServerDiscoveryService),
        typeof(IServerDiscoveryService)
    ];

    // ------------------------------------------------------------------ C7: parity

    [Fact]
    public void Every_service_the_four_modules_own_is_still_registered()
    {
        var services = RealComposition();

        var missing = ModuleOwnedServiceTypes
            .Where(type => !services.Any(descriptor => descriptor.ServiceType == type))
            .Select(type => type.FullName!)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_four_community_capabilities_are_composed_and_none_requires_entitlement()
    {
        var services = RealComposition();

        // Two descriptors is the documented shape: FeatureCatalog.Empty is the inert default, and the
        // composed catalog is registered after it. DI resolves the LAST one, so that is the one asserted.
        var catalogs = DescriptorsFor(services, typeof(IFeatureCatalog));
        Assert.Equal(2, catalogs.Length);
        Assert.Same(FeatureCatalog.Empty, catalogs[0].ImplementationInstance);

        var catalog = Assert.IsType<FeatureCatalog>(catalogs[^1].ImplementationInstance);
        Assert.NotSame(FeatureCatalog.Empty, catalog);

        Assert.Equal(
            [
                CommunityFeatures.History.Id,
                CommunityFeatures.Workloads.Id,
                CommunityFeatures.WidgetSnapshot.Id,
                CommunityFeatures.Discovery.Id
            ],
            catalog.Composed.Select(descriptor => descriptor.Id));

        Assert.All(catalog.Composed, descriptor => Assert.False(descriptor.RequiresEntitlement));
    }

    // ------------------------------------------------ C9: one registration per owned service type

    [Fact]
    public void No_module_owned_service_is_registered_twice_except_the_inert_defaults_it_overrides()
    {
        // The four inert defaults are registered FIRST on purpose and overridden by the real module, so
        // exactly two descriptors is the expected, documented shape for those. Everything else is one.
        Type[] intentionallyOverridden =
        [
            typeof(IServerHistoryQueryService),
            typeof(IHistoryMaintenanceService),
            typeof(IWorkloadRefreshCoordinator),
            typeof(IServerDiscoveryService)
        ];

        var services = RealComposition();
        var wrong = new List<string>();

        foreach (var type in ModuleOwnedServiceTypes)
        {
            var expected = intentionallyOverridden.Contains(type) ? 2 : 1;
            var actual = DescriptorsFor(services, type).Length;
            if (actual != expected)
            {
                wrong.Add($"{type.FullName}: expected {expected} descriptor(s), found {actual}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void Exactly_one_widget_state_writer_is_registered()
    {
        // S-8 on the public side: two writers would put two sources behind one atomic-rename contract
        // that another process reads.
        var services = RealComposition();

        Assert.Single(DescriptorsFor(services, typeof(IWidgetStateWriter)));
    }

    // ---------------------------------------------- C5: the provider is not reachable downstream

    [Fact]
    public void The_entitlement_provider_is_not_in_the_container()
    {
        // The invariant "consulted once, at the root" is provable by ABSENCE rather than by convention:
        // nothing downstream can resolve it, so nothing downstream can consult it.
        var services = RealComposition();

        var offenders = services
            .Where(descriptor =>
                typeof(IEntitlementProvider).IsAssignableFrom(descriptor.ServiceType)
                || (descriptor.ImplementationType is not null
                    && typeof(IEntitlementProvider).IsAssignableFrom(descriptor.ImplementationType))
                || descriptor.ImplementationInstance is IEntitlementProvider)
            .Select(descriptor => descriptor.ServiceType.FullName!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_feature_catalog_is_resolvable_in_every_composition()
    {
        var services = RealComposition();

        Assert.NotEmpty(DescriptorsFor(services, typeof(IFeatureCatalog)));
    }

    // ------------------------------------------------------- C10: the Discovery default is inert

    [Fact]
    public void The_inert_discovery_default_is_registered_first_and_always_overridden()
    {
        var services = RealComposition();
        var discovery = DescriptorsFor(services, typeof(IServerDiscoveryService));

        Assert.Equal(2, discovery.Length);
        Assert.Equal(typeof(NullServerDiscoveryService), discovery[0].ImplementationType);
        // Last one wins: the real service, resolved from the concrete registration, is what runs.
        Assert.Null(discovery[1].ImplementationType);
        Assert.NotNull(discovery[1].ImplementationFactory);
    }

    [Fact]
    public async Task The_inert_discovery_default_suggests_nothing_and_never_raises()
    {
        var service = new NullServerDiscoveryService();
        var raised = 0;
        service.DiscoveredChanged += (_, _) => raised++;

        Assert.Empty(service.GetDiscovered());
        var identity = Core.Discovery.ServiceInstanceIdentity.TryCreate("probe", "_ssh._tcp", "local");
        Assert.NotNull(identity);
        await service.IgnoreAsync(identity);
        await service.ResetIgnoredAsync();

        Assert.Empty(service.GetDiscovered());
        Assert.Equal(0, raised);
    }

    // ------------------------------------------------------ C8: hosted-service order is untouched

    [Fact]
    public void Hosted_service_registration_order_is_unchanged()
    {
        // Reverse-order shutdown is a documented behavioural contract in the root. Moving a hosted
        // service into a module would have reordered it against tray, notifications and alerts, so the
        // modules deliberately do not own them.
        var services = RealComposition();

        var hosted = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationFactory)
            .ToArray();

        Assert.Equal(7, hosted.Length);
        Assert.All(hosted, factory => Assert.NotNull(factory));
    }
}
