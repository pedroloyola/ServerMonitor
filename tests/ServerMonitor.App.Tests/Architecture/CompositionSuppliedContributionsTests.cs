using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Services;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.Features;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// M15.4a: the extension point for composition-supplied contributions (<c>AddCommercialContributions</c>). It adds user
/// interface and nothing else: it cannot add a capability, a feature or a service; it runs only outside QA launches; its
/// entries come after the composed ones and follow the same arrangement; and with no implementation nothing changes.
/// </summary>
public sealed class CompositionSuppliedContributionsTests
{
    private const string HookName = "AddCommercialContributions";

    private sealed class View(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class GrantAll : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => true;
    }

    /// <summary>A composed module that contributes one Settings section.</summary>
    private sealed class SectionModule(string featureId, ShellContribution section) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = new(new FeatureId(featureId), RequiresEntitlement: true);

        public ShellContributions Contributions { get; } = new([section], []);

        public void Register(IServiceCollection services)
        {
        }
    }

    private static ShellContribution Section(string id, int order = 0, Func<object>? factory = null) =>
        new(id, order, "TitleKey." + id, factory ?? (() => new View(id)));

    private static ShellContributionHost HostOver(IServiceCollection services) =>
        new(LastContributions(services), NullLogger.Instance, key => "Title of " + key, view => view is View);

    private static ShellContributions LastContributions(IEnumerable<ServiceDescriptor> services) =>
        Assert.IsType<ShellContributions>(services.Last(d => d.ServiceType == typeof(ShellContributions)).ImplementationInstance);

    // ---------------------------------------------------------------- 1. it cannot add capabilities, features or services

    [Fact]
    public void TheHook_IsClassicOptionalPartial_AndReceivesOnlyListsOfDescriptions()
    {
        var declaration = Assert.Single(Regex.Matches(AppSource(),
            $@"^[ \t]*(?<prefix>[\w \t]*?)\bpartial\s+void\s+{HookName}\s*\((?<parameters>[^)]*)\)\s*;",
            RegexOptions.Multiline));

        Assert.Equal("static", declaration.Groups["prefix"].Value.Trim());
        Assert.Equal(
            "IList<ShellContribution> settingsSections, IList<ShellContribution> unlistedPages, IList<ShellLink> links",
            Regex.Replace(declaration.Groups["parameters"].Value.Trim(), @"\s+", " "));
    }

    [Theory]
    [InlineData(typeof(ShellContribution))]
    [InlineData(typeof(ShellContributions))]
    [InlineData(typeof(ShellLink))]
    [InlineData(typeof(RealizedShellLink))]
    public void AContribution_CarriesNoServiceCollectionCatalogProviderOrModule(Type type)
    {
        Type[] forbidden =
        [
            typeof(IServiceProvider), typeof(IServiceCollection), typeof(IEntitlementProvider), typeof(IFeatureCatalog), typeof(IFeatureModule),
            typeof(INavigationService), typeof(ShellContributionHost),
        ];
        var memberTypes = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(p => p.PropertyType)
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(f => f.FieldType))
            .Concat(type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
            .SelectMany(Flatten)
            .ToList();

        Assert.DoesNotContain(memberTypes, member => forbidden.Any(f => f.IsAssignableFrom(member)));
        Assert.Equal(typeof(Func<object>), typeof(ShellContribution).GetProperty(nameof(ShellContribution.CreateView))!.PropertyType);
    }

    [Fact]
    public void SuppliedContributions_LeaveEveryServiceAndTheCatalogAsTheyWere()
    {
        var baseline = new ServiceCollection();
        var baselineResult = App.ComposeFeatures(baseline, [], null);
        var supplied = new ServiceCollection();
        var suppliedResult = App.ComposeFeatures(supplied, [], null,
            [Section("supplied.one"), Section("supplied.broken", factory: () => throw new InvalidOperationException("view failed"))],
            [Section("supplied.page")],
            [new ShellLink("supplied.link", 0, "Key", "supplied.page", ShellLinkPlacement.SidebarFooter)]);

        Assert.Equal(Shapes(baseline), Shapes(supplied));
        Assert.Equal(
            baselineResult.Catalog.Composed.Select(d => d.Id),
            suppliedResult.Catalog.Composed.Select(d => d.Id));
        Assert.Equal(["supplied.one", "supplied.broken"], LastContributions(supplied).SettingsSections.Select(c => c.Id));
        Assert.Equal(["supplied.one"], HostOver(supplied).SettingsSections.Select(s => s.Id)); // the broken one is left out
        Assert.Equal(["supplied.page"], LastContributions(supplied).UnlistedPages.Select(c => c.Id));
        Assert.Equal(["supplied.link"], LastContributions(supplied).Links.Select(l => l.Id));
    }

    /// <summary>A composed module that declares unlisted pages and links: only the composition root may supply those.</summary>
    private sealed class OverreachingModule : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = new(new FeatureId("test.overreach"), RequiresEntitlement: true);

        public ShellContributions Contributions { get; } = new(
            [], [], [Section("module.page")], [new ShellLink("module.link", 0, "Key", "module.page", ShellLinkPlacement.SidebarFooter)]);

        public void Register(IServiceCollection services)
        {
        }
    }

    [Fact]
    public void AModule_CannotSupplyUnlistedPagesOrLinks_EvenWhenComposed()
    {
        var services = new ServiceCollection();
        App.ComposeFeatures(services, [new OverreachingModule()], new GrantAll());

        var contributions = LastContributions(services);
        Assert.Empty(contributions.UnlistedPages);
        Assert.Empty(contributions.Links);
    }

    // ---------------------------------------------------------------- 2. it runs only outside QA launches

    [Fact]
    public void TheHook_IsCalledOnce_OnlyOutsideQaLaunches_WithAFreshList_ThatTheRootNeverFills()
    {
        var source = AppSource();
        var call = Assert.Single(Regex.Matches(source, $@"\b{HookName}\s*\(\s*(?<a>\w+)\s*,\s*(?<b>\w+)\s*,\s*(?<c>\w+)\s*\)\s*;"));
        var lists = new[] { (call.Groups["a"].Value, "ShellContribution"), (call.Groups["b"].Value, "ShellContribution"), (call.Groups["c"].Value, "ShellLink") };
        Assert.Equal(3, lists.Select(l => l.Item1).Distinct().Count());

        var (blockStart, blockEnd) = Block(source, source.IndexOf("if (!qaMode)", StringComparison.Ordinal));
        Assert.InRange(call.Index, blockStart, blockEnd);
        Assert.Single(Regex.Matches(source, @"if \(!qaMode\)\s*\{[^{}]*" + "AddCommercialComposition"));
        foreach (var (list, type) in lists)
        {
            Assert.Matches($@"\bvar\s+{Regex.Escape(list)}\s*=\s*new\s+List<{type}>\s*\(\s*\)\s*;", source);
            Assert.DoesNotMatch($@"\b{Regex.Escape(list)}\s*\.\s*(Add|AddRange|Insert|InsertRange|Clear|Remove)\w*\s*\(", source);
        }

        Assert.Matches(
            $@"\bComposeFeatures\s*\(\s*services\s*,\s*\w+\s*,\s*\w+\s*,\s*{lists[0].Item1}\s*,\s*{lists[1].Item1}\s*,\s*{lists[2].Item1}\s*\)",
            source);
        Assert.True(source.IndexOf("AddCommercialComposition(", StringComparison.Ordinal) < call.Index, "it runs after the module hook");
    }

    // ---------------------------------------------------------------- 3. the same ordering and duplicate rules

    [Fact]
    public void SuppliedEntries_ComeAfterTheComposed_AndFollowTheSameArrangement()
    {
        var fromModule = Section("shared.section", order: 5);
        var services = new ServiceCollection();
        App.ComposeFeatures(services, [new SectionModule("test.sections", fromModule)], new GrantAll(),
        [
            Section("shared.section", order: -1), // same id as a composed contribution: the composed one wins
            Section("appearance"),                // a built-in id: rejected
            Section("late.section", order: -10),  // kept, and ordered by Order
        ]);

        var contributions = LastContributions(services);
        Assert.Equal(["shared.section", "shared.section", "appearance", "late.section"], contributions.SettingsSections.Select(c => c.Id));
        Assert.Same(fromModule, contributions.SettingsSections[0]);

        var host = HostOver(services);
        Assert.Equal([.. ShellContributionHostTests.BaselineSettingsSectionIds, "late.section", "shared.section"], host.SettingsSectionIds);
        Assert.Equal(ShellContributionHostTests.BaselineNavigationIds, host.NavigationIds); // supplied entries are sections only
    }

    [Fact]
    public void WithNothingSupplied_TheComposedContributionsAreRegisteredUnchanged()
    {
        var composed = new ShellContributions([Section("a.section")], [Section("a.entry")]);

        Assert.Same(composed, App.WithSupplied(composed, null));
        Assert.Same(composed, App.WithSupplied(composed, []));
        Assert.Same(composed, App.WithSupplied(composed, [], [], []));

        var withLink = App.WithSupplied(composed, null, null, [new ShellLink("only.link", 0, "Key", "a.entry", ShellLinkPlacement.SettingsCard)]);
        Assert.Equal(["a.section"], withLink.SettingsSections.Select(c => c.Id));
        Assert.Equal(["a.entry"], withLink.NavigationEntries.Select(c => c.Id));
        Assert.Equal(["only.link"], withLink.Links.Select(l => l.Id));
    }

    // ---------------------------------------------------------------- 4. with no implementation, the baseline

    [Fact]
    public void WithNoImplementation_TheHookIsGone_AndTheSequencesEqualTheBaseline()
    {
        Assert.Null(typeof(App).GetMethod(HookName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

        var root = IsolatedAppComposition.ProductionDescriptors();
        var host = new ShellContributionHost(LastContributions(root), NullLogger.Instance, key => key, _ => true);

        Assert.Equal(ShellContributionHostTests.BaselineNavigationIds, host.NavigationIds);
        Assert.Equal(ShellContributionHostTests.BaselineSettingsSectionIds, host.SettingsSectionIds);
        Assert.Empty(host.UnlistedPages);
        Assert.Empty(host.Links);
        Assert.Null(host.FooterLink);
        Assert.Empty(host.SettingsCards);
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<Type> Flatten(Type type) =>
        type.IsGenericType ? type.GetGenericArguments().SelectMany(Flatten).Prepend(type)
        : type.HasElementType ? Flatten(type.GetElementType()!).Prepend(type)
        : [type];

    private static string[] Shapes(IEnumerable<ServiceDescriptor> descriptors) =>
    [
        .. descriptors.Select(descriptor =>
            $"{descriptor.ServiceType.FullName}|{descriptor.Lifetime}|" +
            $"type={descriptor.ImplementationType?.FullName ?? "-"}|" +
            $"instance={descriptor.ImplementationInstance?.GetType().FullName ?? "-"}|" +
            $"factory={(descriptor.ImplementationFactory is null ? "-" : "yes")}")
    ];

    /// <summary>The braces of the block that starts at the first '{' after <paramref name="from"/>.</summary>
    private static (int Start, int End) Block(string source, int from)
    {
        Assert.True(from >= 0, "if (!qaMode) not found");
        var start = source.IndexOf('{', from);
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return (start, i);
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string AppSource() =>
        Regex.Replace(File.ReadAllText(AppSourceTree.Full("App.xaml.cs")), @"//.*?$|/\*.*?\*/", string.Empty,
            RegexOptions.Multiline | RegexOptions.Singleline);
}
