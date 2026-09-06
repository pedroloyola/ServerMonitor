using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Features;

namespace ServerMonitor.Features.Tests;

/// <summary>
/// C3/C4/C6. The composition rules that carry the binding condition on the human classification
/// <c>M14-ENT-1</c>: an unconditional capability is composed no matter what the entitlement provider says
/// or does, and an entitlement-requiring one fails CLOSED.
/// </summary>
public sealed class FeatureCompositionTests
{
    private interface IProbe;

    private sealed class Probe : IProbe;

    private sealed class RecordingModule(FeatureDescriptor descriptor) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = descriptor;

        public bool Registered { get; private set; }

        public void Register(IServiceCollection services)
        {
            Registered = true;
            services.AddSingleton<IProbe, Probe>();
        }
    }

    private sealed class DenyAll : IEntitlementProvider
    {
        public int Calls { get; private set; }

        public bool IsEntitled(FeatureId id)
        {
            Calls++;
            return false;
        }
    }

    private sealed class GrantAll : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => true;
    }

    private sealed class ThrowingProvider : IEntitlementProvider
    {
        public int Calls { get; private set; }

        public bool IsEntitled(FeatureId id)
        {
            Calls++;
            throw new InvalidOperationException("the entitlement path is broken");
        }
    }

    private static RecordingModule Unconditional(string id) =>
        new(FeatureDescriptor.Unconditional(id));

    private static RecordingModule Conditional(string id) =>
        new(new FeatureDescriptor(new FeatureId(id), RequiresEntitlement: true));

    // ---- C3: Community never passes through the entitlement provider -------------------------------

    [Fact]
    public void UnconditionalModule_IsComposed_WithoutConsultingTheProvider()
    {
        var services = new ServiceCollection();
        var module = Unconditional("history.local");
        var provider = new DenyAll();

        var result = FeatureComposition.Compose(services, [module], provider);

        Assert.True(module.Registered);
        Assert.Equal(0, provider.Calls);
        Assert.True(result.Catalog.IsComposed(module.Descriptor.Id));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void UnconditionalModule_IsComposed_EvenWhenTheProviderThrows()
    {
        var services = new ServiceCollection();
        var module = Unconditional("workloads.readonly");
        var provider = new ThrowingProvider();

        var result = FeatureComposition.Compose(services, [module], provider);

        Assert.True(module.Registered);
        Assert.Equal(0, provider.Calls);
        Assert.True(result.Catalog.IsComposed(module.Descriptor.Id));
    }

    [Fact]
    public void UnconditionalModules_ProduceIdenticalDescriptors_ForAnyProvider()
    {
        // The property that makes the M14-ENT-1 condition true rather than merely intended: whatever the
        // provider does - grant, deny, or blow up - a Community composition is the SAME composition.
        static List<string> Compose(IEntitlementProvider entitlements)
        {
            var services = new ServiceCollection();
            FeatureComposition.Compose(
                services,
                [Unconditional("history.local"), Unconditional("discovery.mdns")],
                entitlements);
            return [.. services.Select(d => $"{d.ServiceType.FullName}|{d.Lifetime}")];
        }

        var granted = Compose(new GrantAll());
        var denied = Compose(new DenyAll());
        var broken = Compose(new ThrowingProvider());

        Assert.Equal(granted, denied);
        Assert.Equal(granted, broken);
        Assert.NotEmpty(granted);
    }

    // ---- C4: entitlement-requiring modules fail closed ---------------------------------------------

    [Fact]
    public void ConditionalModule_IsComposed_WhenEntitled()
    {
        var services = new ServiceCollection();
        var module = Conditional("some.commercial.capability");

        var result = FeatureComposition.Compose(services, [module], new GrantAll());

        Assert.True(module.Registered);
        Assert.True(result.Catalog.IsComposed(module.Descriptor.Id));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void ConditionalModule_IsNotComposed_WhenDenied()
    {
        var services = new ServiceCollection();
        var module = Conditional("some.commercial.capability");
        var provider = new DenyAll();

        var result = FeatureComposition.Compose(services, [module], provider);

        Assert.False(module.Registered);
        Assert.Empty(services);
        Assert.Equal(1, provider.Calls);
        Assert.False(result.Catalog.IsComposed(module.Descriptor.Id));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(module.Descriptor.Id, skipped.Id);
        Assert.Null(skipped.Error);
    }

    [Fact]
    public void ConditionalModule_IsNotComposed_WhenTheProviderThrows()
    {
        // Fail CLOSED, and report the reason rather than swallowing it.
        var services = new ServiceCollection();
        var module = Conditional("some.commercial.capability");

        var result = FeatureComposition.Compose(services, [module], new ThrowingProvider());

        Assert.False(module.Registered);
        Assert.Empty(services);
        Assert.False(result.Catalog.IsComposed(module.Descriptor.Id));
        var skipped = Assert.Single(result.Skipped);
        Assert.IsType<InvalidOperationException>(skipped.Error);
    }

    // ---- wiring defects stay loud ------------------------------------------------------------------

    [Fact]
    public void ModuleRegistrationFailure_IsNotSwallowed()
    {
        // A module that throws while registering is OUR defect, not an entitlement outcome. Only the
        // entitlement question may fail softly, and only towards "absent".
        var services = new ServiceCollection();

        Assert.Throws<NotSupportedException>(() =>
            FeatureComposition.Compose(services, [new FaultyModule()], new GrantAll()));
    }

    private sealed class FaultyModule : IFeatureModule
    {
        public FeatureDescriptor Descriptor => FeatureDescriptor.Unconditional("faulty.module");

        public void Register(IServiceCollection services) => throw new NotSupportedException("boom");
    }

    [Fact]
    public void DuplicateFeatureId_IsRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => FeatureComposition.Compose(
            services,
            [Unconditional("history.local"), Unconditional("history.local")],
            new GrantAll()));

        Assert.Contains("history.local", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnspecifiedFeatureId_IsRejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            FeatureComposition.Compose(services, [new UnspecifiedModule()], new GrantAll()));
    }

    private sealed class UnspecifiedModule : IFeatureModule
    {
        public FeatureDescriptor Descriptor => default;

        public void Register(IServiceCollection services) => services.AddSingleton<IProbe, Probe>();
    }

    // ---- catalog -----------------------------------------------------------------------------------

    [Fact]
    public void CatalogRecordsCompositionOrder_AndOnlyWhatWasComposed()
    {
        var services = new ServiceCollection();
        var first = Unconditional("history.local");
        var second = Conditional("commercial.thing");
        var third = Unconditional("discovery.mdns");

        var result = FeatureComposition.Compose(services, [first, second, third], new DenyAll());

        Assert.Equal(
            [first.Descriptor.Id, third.Descriptor.Id],
            result.Catalog.Composed.Select(descriptor => descriptor.Id));
        Assert.False(result.Catalog.IsComposed(second.Descriptor.Id));
    }

    [Fact]
    public void EmptyCatalogComposesNothing()
    {
        Assert.Empty(FeatureCatalog.Empty.Composed);
        Assert.False(FeatureCatalog.Empty.IsComposed(new FeatureId("history.local")));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        var services = new ServiceCollection();
        var module = Unconditional("history.local");

        Assert.Throws<ArgumentNullException>(() =>
            FeatureComposition.Compose(null!, [module], new GrantAll()));
        Assert.Throws<ArgumentNullException>(() =>
            FeatureComposition.Compose(services, null!, new GrantAll()));
        Assert.Throws<ArgumentNullException>(() =>
            FeatureComposition.Compose(services, [module], null!));
    }
}

/// <summary>C6. The only entitlement provider that ships here.</summary>
public sealed class CommunityEntitlementProviderTests
{
    [Theory]
    [InlineData("history.local")]
    [InlineData("workloads.readonly")]
    [InlineData("discovery.mdns")]
    [InlineData("widget.snapshot")]
    [InlineData("anything.at.all")]
    public void GrantsNothing(string id) =>
        Assert.False(CommunityEntitlementProvider.Instance.IsEntitled(new FeatureId(id)));

    [Fact]
    public void IsDeterministic()
    {
        var id = new FeatureId("some.capability");

        // No account, no network, no Cloud, no Store API, no secret, and no state to go stale: the same
        // question gets the same answer, always.
        for (var i = 0; i < 100; i++)
        {
            Assert.False(CommunityEntitlementProvider.Instance.IsEntitled(id));
        }
    }
}
