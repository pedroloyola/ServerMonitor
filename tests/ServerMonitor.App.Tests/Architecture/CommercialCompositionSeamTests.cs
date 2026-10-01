using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServerMonitor.App;
using ServerMonitor.App.Features;
using ServerMonitor.Core.History;
using ServerMonitor.Features;
using ServerMonitor.App.Tests.TestSupport;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// M14.2 — the public commercial composition seam, exercised through <c>App.ComposeFeatures</c> with a
/// fake conditional module and fake providers.
/// <para>
/// The claims: Community is composed first and unconditionally whatever provider is supplied; a
/// conditional module composes only on an explicit yes; a supplied module cannot shadow a Community one;
/// and the provider never reaches the container. The last group pins the partial hook itself, because a
/// hook that is handed the Community list could undo every one of those claims without a test noticing.
/// </para>
/// </summary>
public sealed class CommercialCompositionSeamTests
{
    private static readonly FeatureId ConditionalId = new("test.conditional");

    private static readonly FeatureId[] CommunityIds =
    [
        CommunityFeatures.History.Id,
        CommunityFeatures.Workloads.Id,
        CommunityFeatures.WidgetSnapshot.Id,
        CommunityFeatures.Discovery.Id
    ];

    private sealed class ConditionalMarker;

    private sealed class ConditionalModule : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = new(ConditionalId, RequiresEntitlement: true);

        public void Register(IServiceCollection services) => services.AddSingleton<ConditionalMarker>();
    }

    /// <summary>A module that tries to take over a Community capability by reusing its id.</summary>
    private sealed class ShadowingModule(FeatureId id) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = new(id, RequiresEntitlement: true);

        public void Register(IServiceCollection services) => services.AddSingleton<ConditionalMarker>();
    }

    private sealed class FakeProvider(Func<FeatureId, bool> answer) : IEntitlementProvider
    {
        public List<FeatureId> Asked { get; } = [];

        public bool IsEntitled(FeatureId id)
        {
            Asked.Add(id);
            return answer(id);
        }
    }

    private static FakeProvider Allow() => new(_ => true);

    private static FakeProvider Deny() => new(_ => false);

    private static FakeProvider Throw(Exception exception) => new(_ => throw exception);

    private static (ServiceCollection Services, FeatureCompositionResult Result) Compose(
        IReadOnlyList<IFeatureModule> commercial, IEntitlementProvider? provider)
    {
        var services = new ServiceCollection();
        var result = App.ComposeFeatures(services, commercial, provider);
        return (services, result);
    }

    private static IFeatureModule[] OneConditional() => [new ConditionalModule()];

    // ----------------------------------------------------------------------------- the four states

    [Fact]
    public void Without_a_provider_the_conditional_module_is_skipped_and_community_is_composed()
    {
        var (services, result) = Compose(OneConditional(), provider: null);

        Assert.Equal(CommunityIds, result.Catalog.Composed.Select(descriptor => descriptor.Id));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(ConditionalId, skipped.Id);
        Assert.Null(skipped.Error);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ConditionalMarker));
    }

    [Fact]
    public void An_allowing_provider_composes_the_conditional_module_after_the_four_community_modules()
    {
        var provider = Allow();
        var (services, result) = Compose(OneConditional(), provider);

        Assert.Equal([.. CommunityIds, ConditionalId], result.Catalog.Composed.Select(descriptor => descriptor.Id));
        Assert.Empty(result.Skipped);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ConditionalMarker));

        // Community is never put to the provider, even one that says yes to everything.
        Assert.Equal([ConditionalId], provider.Asked);
    }

    [Fact]
    public void A_denying_provider_skips_the_conditional_module_without_an_error()
    {
        var provider = Deny();
        var (services, result) = Compose(OneConditional(), provider);

        Assert.Equal(CommunityIds, result.Catalog.Composed.Select(descriptor => descriptor.Id));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(ConditionalId, skipped.Id);
        Assert.Null(skipped.Error);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ConditionalMarker));
        Assert.Equal([ConditionalId], provider.Asked);
    }

    [Fact]
    public void A_throwing_provider_skips_the_conditional_module_and_reports_the_exception()
    {
        var thrown = new InvalidOperationException("provider failure");
        var provider = Throw(thrown);
        var (services, result) = Compose(OneConditional(), provider);

        Assert.Equal(CommunityIds, result.Catalog.Composed.Select(descriptor => descriptor.Id));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(ConditionalId, skipped.Id);
        Assert.Same(thrown, skipped.Error);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ConditionalMarker));
        Assert.Equal([ConditionalId], provider.Asked);
    }

    // ------------------------------------------------- Community descriptors do not depend on state

    public static TheoryData<string> CommercialStates => ["null", "allow", "deny", "throw"];

    [Theory]
    [MemberData(nameof(CommercialStates))]
    public void Community_owned_descriptors_are_identical_in_every_commercial_state(string state)
    {
        var (baseline, _) = Compose([], provider: null);

        IEntitlementProvider? provider = state switch
        {
            "null" => null,
            "allow" => Allow(),
            "deny" => Deny(),
            "throw" => Throw(new InvalidOperationException("provider failure")),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        var (services, _) = Compose(OneConditional(), provider);

        var communityOwned = services.Where(descriptor => descriptor.ServiceType != typeof(ConditionalMarker));

        Assert.Equal(Shapes(baseline), Shapes(communityOwned));
    }

    // --------------------------------------------------------------------- cannot shadow Community

    [Theory]
    [InlineData("history.local")]
    [InlineData("workloads.readonly")]
    [InlineData("widget.snapshot")]
    [InlineData("discovery.mdns")]
    public void A_commercial_module_reusing_a_community_feature_id_is_rejected(string communityId)
    {
        Assert.Contains(new FeatureId(communityId), CommunityIds);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Compose([new ShadowingModule(new FeatureId(communityId))], Allow()));

        Assert.Contains(communityId, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An entitled module that tries to take over the Community history store (R8).</summary>
    private sealed class HistoryStoreTakeoverModule(bool replace) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = new(new FeatureId("test.takeover"), RequiresEntitlement: true);

        public void Register(IServiceCollection services)
        {
            var takeover = ServiceDescriptor.Singleton<IServerHistoryStore>(_ => null!);
            if (replace)
            {
                services.Replace(takeover);
            }
            else
            {
                services.Add(takeover);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_entitled_commercial_module_cannot_take_over_a_community_service(bool replace)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Compose([new HistoryStoreTakeoverModule(replace)], Allow()));

        Assert.Contains("test.takeover", exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(IServerHistoryStore).FullName!, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A commercial module that declares itself unconditional to skip the entitlement question.</summary>
    private sealed class UnconditionalCommercialModule : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = FeatureDescriptor.Unconditional("test.unconditional");

        public void Register(IServiceCollection services) => services.AddSingleton<ConditionalMarker>();
    }

    [Fact]
    public void A_commercial_module_that_does_not_require_entitlement_is_rejected()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            App.ComposeFeatures(services, [new UnconditionalCommercialModule()], Allow()));

        Assert.Contains("test.unconditional", exception.Message, StringComparison.Ordinal);
        // Rejected before composition: not even the Community modules were registered.
        Assert.Empty(services);
    }

    // --------------------------------------------------------------------------- parity with root

    [Fact]
    public void Without_commercial_input_the_composition_is_the_one_the_application_root_performs()
    {
        var (composed, result) = Compose([], provider: null);
        Assert.Equal(CommunityIds, result.Catalog.Composed.Select(descriptor => descriptor.Id));
        Assert.Empty(result.Skipped);

        var root = IsolatedAppComposition.ProductionDescriptors(); // descriptors only, never built

        // The root's composed catalog is the last IFeatureCatalog registration and must list the same ids.
        var rootCatalog = Assert.IsType<FeatureCatalog>(root
            .Last(descriptor => descriptor.ServiceType == typeof(IFeatureCatalog))
            .ImplementationInstance);
        Assert.Equal(CommunityIds, rootCatalog.Composed.Select(descriptor => descriptor.Id));

        // The descriptors ComposeFeatures produces appear in the root as one contiguous run, in order.
        var expected = Shapes(composed);
        var actual = Shapes(root);
        var found = Enumerable.Range(0, actual.Length - expected.Length + 1)
            .Any(start => actual.Skip(start).Take(expected.Length).SequenceEqual(expected));

        Assert.True(found, "The root does not contain the ComposeFeatures registrations as one ordered run.");
    }

    // -------------------------------------------------------- the provider never enters the container

    [Theory]
    [MemberData(nameof(CommercialStates))]
    public void The_provider_is_absent_from_the_container_in_every_state(string state)
    {
        IEntitlementProvider? provider = state switch
        {
            "null" => null,
            "allow" => Allow(),
            "deny" => Deny(),
            "throw" => Throw(new InvalidOperationException("provider failure")),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        var (services, _) = Compose(OneConditional(), provider);

        Assert.DoesNotContain(services, descriptor =>
            typeof(IEntitlementProvider).IsAssignableFrom(descriptor.ServiceType)
            || (descriptor.ImplementationType is not null
                && typeof(IEntitlementProvider).IsAssignableFrom(descriptor.ImplementationType))
            || descriptor.ImplementationInstance is IEntitlementProvider);
    }

    // ------------------------------------------------------------------------- the hook's own shape

    [Fact]
    public void The_hook_is_declared_in_the_classic_optional_partial_form()
    {
        var source = AppSource();

        // No accessibility modifier, void, ref (never out) provider. Any other form makes the
        // implementation MANDATORY and a public clone stops compiling.
        var declarations = Regex.Matches(source,
            @"^[ \t]*(?<prefix>[\w \t]*?)\bpartial\s+void\s+AddCommercialComposition\s*\((?<parameters>[^)]*)\)\s*;",
            RegexOptions.Multiline);
        var declaration = Assert.Single(declarations);

        Assert.Equal("static", declaration.Groups["prefix"].Value.Trim());
        Assert.Equal(
            "IList<IFeatureModule> modules, ref IEntitlementProvider? entitlements",
            Regex.Replace(declaration.Groups["parameters"].Value.Trim(), @"\s+", " "));
    }

    [Fact]
    public void The_hook_is_handed_a_fresh_list_and_a_null_provider_never_the_community_modules()
    {
        var source = AppSource();

        var call = Assert.Single(Regex.Matches(source,
            @"\bAddCommercialComposition\s*\(\s*(?<list>\w+)\s*,\s*ref\s+(?<provider>\w+)\s*\)\s*;"));
        var list = call.Groups["list"].Value;
        var provider = call.Groups["provider"].Value;

        Assert.Matches(
            $@"\bvar\s+{Regex.Escape(list)}\s*=\s*new\s+List<IFeatureModule>\s*\(\s*\)\s*;",
            source);
        Assert.Matches(
            $@"\bIEntitlementProvider\?\s+{Regex.Escape(provider)}\s*=\s*null\s*;",
            source);

        // Nothing is added to that list by the root itself - only the hook may populate it.
        Assert.DoesNotMatch($@"\b{Regex.Escape(list)}\s*\.\s*(Add|AddRange|Insert|InsertRange)\s*\(", source);
    }

    [Fact]
    public void With_no_implementation_the_compiler_removes_the_hook_from_the_binary()
    {
        var method = typeof(App).GetMethod(
            "AddCommercialComposition",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.Null(method);
    }

    // ------------------------------------------------------------------------------------- helpers

    /// <summary>
    /// The comparable shape of a descriptor. Instances and factories are compared by presence (and the
    /// instance by type), because every composition constructs its own.
    /// </summary>
    private static string[] Shapes(IEnumerable<ServiceDescriptor> descriptors) =>
    [
        .. descriptors.Select(descriptor =>
            $"{descriptor.ServiceType.FullName}|{descriptor.Lifetime}|" +
            $"type={descriptor.ImplementationType?.FullName ?? "-"}|" +
            $"instance={descriptor.ImplementationInstance?.GetType().FullName ?? "-"}|" +
            $"factory={(descriptor.ImplementationFactory is null ? "-" : "yes")}")
    ];

    private static string AppSource()
    {
        var root = FindRepositoryRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "ServerMonitor.App", "App.xaml.cs"));
        return Regex.Replace(text, @"//.*?$|/\*.*?\*/", string.Empty,
            RegexOptions.Multiline | RegexOptions.Singleline);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }
}
