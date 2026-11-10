using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Features;

namespace ServerMonitor.Features.Tests;

/// <summary>
/// M15.4a: the shell contribution contract, and the rule that user interface follows its module through the SAME
/// composition gate: an unconditional module always contributes, a denied conditional module contributes nothing.
/// </summary>
public sealed class ShellContributionTests
{
    private static ShellContribution Contribution(string id, int order = 0) =>
        new(id, order, "Test" + id.Replace(".", string.Empty, StringComparison.Ordinal) + "Title", () => new object());

    private sealed class ContributingModule(FeatureDescriptor descriptor, ShellContributions contributions) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = descriptor;

        public int ContributionReads { get; private set; }

        public ShellContributions Contributions
        {
            get
            {
                ContributionReads++;
                return contributions;
            }
        }

        public void Register(IServiceCollection services)
        {
        }
    }

    /// <summary>A module written before contributions existed: it implements only the original members.</summary>
    private sealed class OriginalModule(FeatureDescriptor descriptor) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = descriptor;

        public void Register(IServiceCollection services)
        {
        }
    }

    private sealed class DenyAll : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => false;
    }

    private sealed class GrantAll : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => true;
    }

    private sealed class ThrowingProvider : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => throw new InvalidOperationException("the provider is broken");
    }

    private static ContributingModule Unconditional(string id, ShellContributions contributions) =>
        new(FeatureDescriptor.Unconditional(id), contributions);

    private static ContributingModule Conditional(string id, ShellContributions contributions) =>
        new(new FeatureDescriptor(new FeatureId(id), RequiresEntitlement: true), contributions);

    private static ShellContributions Both(string prefix) =>
        new([Contribution(prefix + ".section")], [Contribution(prefix + ".entry")]);

    public static TheoryData<string> Providers() => ["deny-all", "grant-all", "throws", "community"];

    private static IEntitlementProvider Provider(string name) => name switch
    {
        "deny-all" => new DenyAll(),
        "grant-all" => new GrantAll(),
        "throws" => new ThrowingProvider(),
        _ => CommunityEntitlementProvider.Instance,
    };

    private static string[] Ids(IReadOnlyList<ShellContribution> contributions) => [.. contributions.Select(c => c.Id)];

    // ---------------------------------------------------------------- the gate

    [Theory]
    [MemberData(nameof(Providers))]
    public void AnUnconditionalModule_Contributes_WhateverTheProviderSays(string provider)
    {
        var module = Unconditional("always.here", Both("always"));

        var result = FeatureComposition.Compose(new ServiceCollection(), [module], Provider(provider));

        Assert.Equal(["always.section"], Ids(result.Contributions.SettingsSections));
        Assert.Equal(["always.entry"], Ids(result.Contributions.NavigationEntries));
    }

    [Theory]
    [InlineData("deny-all")]
    [InlineData("throws")]
    [InlineData("community")]
    public void ADeniedConditionalModule_ContributesNothing_AndIsNotEvenAsked(string provider)
    {
        var module = Conditional("gated.feature", Both("gated"));

        var result = FeatureComposition.Compose(new ServiceCollection(), [module], Provider(provider));

        Assert.Empty(result.Contributions.SettingsSections);
        Assert.Empty(result.Contributions.NavigationEntries);
        Assert.Equal(0, module.ContributionReads);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void AGrantedConditionalModule_Contributes()
    {
        var result = FeatureComposition.Compose(new ServiceCollection(), [Conditional("gated.feature", Both("gated"))], new GrantAll());

        Assert.Equal(["gated.section"], Ids(result.Contributions.SettingsSections));
        Assert.Equal(["gated.entry"], Ids(result.Contributions.NavigationEntries));
    }

    [Fact]
    public void ADeniedModule_DoesNotTakeAnUnconditionalModulesContributionsWithIt()
    {
        var result = FeatureComposition.Compose(
            new ServiceCollection(),
            [Unconditional("first.feature", Both("first")), Conditional("gated.feature", Both("gated")), Unconditional("last.feature", Both("last"))],
            new ThrowingProvider());

        Assert.Equal(["first.section", "last.section"], Ids(result.Contributions.SettingsSections));
        Assert.Equal(["first.entry", "last.entry"], Ids(result.Contributions.NavigationEntries));
    }

    [Fact]
    public void ContributionsKeepCompositionOrder_BeforeTheShellArrangesThem()
    {
        var result = FeatureComposition.Compose(
            new ServiceCollection(),
            [Unconditional("b.feature", new([Contribution("b.one", 9)], [])), Unconditional("a.feature", new([Contribution("a.one", 1)], []))],
            new DenyAll());

        Assert.Equal(["b.one", "a.one"], Ids(result.Contributions.SettingsSections));
    }

    [Fact]
    public void AModuleWrittenBeforeContributions_StillComposes_AndContributesNothing()
    {
        var module = new OriginalModule(FeatureDescriptor.Unconditional("original.feature"));

        var result = FeatureComposition.Compose(new ServiceCollection(), [module], new DenyAll());

        Assert.True(result.Catalog.IsComposed(new FeatureId("original.feature")));
        Assert.Same(ShellContributions.Empty, ((IFeatureModule)module).Contributions);
        Assert.Empty(result.Contributions.SettingsSections);
        Assert.Empty(result.Contributions.NavigationEntries);
    }

    [Fact]
    public void ADefaultResult_HasNoContributions_RatherThanNull()
    {
        Assert.Same(ShellContributions.Empty, default(FeatureCompositionResult).Contributions);
    }

    // ---------------------------------------------------------------- ordering and the duplicate rule

    [Fact]
    public void Arrange_OrdersByOrder_ThenById()
    {
        var arranged = ShellContributionOrdering.Arrange(
            [Contribution("c.entry", 2), Contribution("b.entry", 1), Contribution("a.entry", 2), Contribution("z.entry", -5)],
            [],
            out var rejected);

        Assert.Equal(["z.entry", "b.entry", "a.entry", "c.entry"], Ids(arranged));
        Assert.Empty(rejected);
    }

    [Fact]
    public void Arrange_IsDeterministic_WhateverOrderTheContributionsArriveIn()
    {
        ShellContribution[] all = [Contribution("a.x", 1), Contribution("b.x", 0), Contribution("c.x", 1), Contribution("d.x", 0), Contribution("e.x", 3)];
        var expected = Ids(ShellContributionOrdering.Arrange(all, [], out _));

        foreach (var permutation in Permutations(all))
        {
            Assert.Equal(expected, Ids(ShellContributionOrdering.Arrange(permutation, [], out _)));
        }

        Assert.Equal(["b.x", "d.x", "a.x", "c.x", "e.x"], expected);
    }

    [Fact]
    public void Arrange_TheFirstOfTwoEqualIdsWins_AndTheOtherIsReported()
    {
        var first = Contribution("same.id", 5);
        var second = Contribution("same.id", -1);

        var arranged = ShellContributionOrdering.Arrange([first, Contribution("other.id"), second], [], out var rejected);

        Assert.Equal(["other.id", "same.id"], Ids(arranged));
        Assert.Same(first, arranged.Single(c => c.Id == "same.id"));
        Assert.Same(second, Assert.Single(rejected).Contribution);
    }

    [Fact]
    public void Arrange_AnIdThatIsBuiltIn_IsLeftOutAndReported()
    {
        var arranged = ShellContributionOrdering.Arrange([Contribution("settings"), Contribution("new.entry")], ["overview", "settings"], out var rejected);

        Assert.Equal(["new.entry"], Ids(arranged));
        Assert.Equal("settings", Assert.Single(rejected).Contribution.Id);
    }

    // ---------------------------------------------------------------- the descriptor

    [Theory]
    [InlineData("")]
    [InlineData("Upper.Case")]
    [InlineData("with space")]
    [InlineData(".leading")]
    [InlineData("double..dot")]
    public void AContributionId_FollowsTheFeatureIdShape(string id)
    {
        Assert.Throws<ArgumentException>(() => new ShellContribution(id, 0, "TitleKey", () => new object()));
    }

    [Fact]
    public void AContribution_NeedsATitleKeyAndAFactory()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ShellContribution("ok.id", 0, " ", () => new object()));
        Assert.Throws<ArgumentNullException>(() => new ShellContribution("ok.id", 0, "TitleKey", null!));
        Assert.Throws<ArgumentException>(() => new ShellContributions([null!], []));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("M0,0 L1,1", "M0,0 L1,1")]
    public void TheIcon_IsOptional_AndBlankMeansTheShellsGenericOne(string? given, string? expected)
    {
        Assert.Equal(expected, new ShellContribution("ok.id", 0, "TitleKey", () => new object(), given).IconPathData);
        Assert.Null(new ShellContribution("ok.id", 0, "TitleKey", () => new object()).IconPathData);
    }

    // ---------------------------------------------------------------- r3 addendum: unlisted pages and links

    [Fact]
    public void ModulesDeclareTwoLists_TheOtherTwoAreEmptyByDefault()
    {
        var contributions = new ShellContributions([Contribution("a.section")], [Contribution("a.entry")]);

        Assert.Empty(contributions.UnlistedPages);
        Assert.Empty(contributions.Links);
        Assert.Empty(ShellContributions.Empty.UnlistedPages);
        Assert.Empty(ShellContributions.Empty.Links);
    }

    [Fact]
    public void NoListMayBeNullOrHoldNull()
    {
        var link = new ShellLink("a.link", 0, "Key", "a.page", ShellLinkPlacement.SettingsCard);

        Assert.Throws<ArgumentNullException>(() => new ShellContributions([], [], null!, []));
        Assert.Throws<ArgumentNullException>(() => new ShellContributions([], [], [], null!));
        Assert.Throws<ArgumentException>(() => new ShellContributions([], [], [null!], []));
        Assert.Throws<ArgumentException>(() => new ShellContributions([], [], [], [null!]));
        Assert.Equal([link], new ShellContributions([], [], [], [link]).Links);
    }

    [Theory]
    [InlineData("Bad Id", "a.page", "Key")]
    [InlineData("a.link", "Bad Target", "Key")]
    [InlineData("a.link", "a.page", " ")]
    public void ALink_NeedsWellFormedIds_AndATitleKey(string id, string target, string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new ShellLink(id, 0, key, target, ShellLinkPlacement.SidebarFooter));

    [Fact]
    public void ALink_HasAKnownPlacement_AndBlankIconMeansTheGenericOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShellLink("a.link", 0, "Key", "a.page", (ShellLinkPlacement)42));
        Assert.Null(new ShellLink("a.link", 0, "Key", "a.page", ShellLinkPlacement.SettingsCard, iconPathData: "  ").IconPathData);
    }

    [Fact]
    public void Links_AreArrangedByTheSameRule()
    {
        ShellLink Link(string id, int order) => new(id, order, "Key", "a.page", ShellLinkPlacement.SettingsCard);
        var first = Link("dup.link", 3);

        var arranged = ShellContributionOrdering.Arrange([first, Link("b.link", 1), Link("dup.link", -9), Link("settings", 0), Link("a.link", 1)],
            ["settings"], out IReadOnlyList<ShellLinkRejection> rejected);

        Assert.Equal(["a.link", "b.link", "dup.link"], arranged.Select(link => link.Id));
        Assert.Same(first, arranged[2]);
        Assert.Equal(["dup.link", "settings"], rejected.Select(r => r.Link.Id));
    }

    private static IEnumerable<ShellContribution[]> Permutations(ShellContribution[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }
}
