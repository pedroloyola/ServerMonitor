using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Features;

namespace ServerMonitor.Features.Tests;

/// <summary>
/// M16.1: server detail sections follow their module through the same composition gate as every other contribution,
/// keep composition order until the shell arranges them, and are arranged by the same rule.
/// </summary>
public sealed class ServerDetailSectionTests
{
    private static ServerDetailSection Section(string id, int order = 0) =>
        new(id, order, "TestTitle", _ => new object());

    private static ShellContributions WithSections(params ServerDetailSection[] sections) =>
        ShellContributions.Empty.WithServerDetailSections(sections);

    private sealed class Module(FeatureDescriptor descriptor, ShellContributions contributions) : IFeatureModule
    {
        public FeatureDescriptor Descriptor { get; } = descriptor;

        public ShellContributions Contributions { get; } = contributions;

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

    private static string[] Ids(IEnumerable<ServerDetailSection> sections) => [.. sections.Select(s => s.Id)];

    [Fact]
    public void AComposedModule_ContributesItsSections_InCompositionOrder()
    {
        var result = FeatureComposition.Compose(
            new ServiceCollection(),
            [
                new Module(FeatureDescriptor.Unconditional("b.feature"), WithSections(Section("b.section", 9))),
                new Module(FeatureDescriptor.Unconditional("a.feature"), WithSections(Section("a.section", 1)))
            ],
            new DenyAll());

        Assert.Equal(["b.section", "a.section"], Ids(result.Contributions.ServerDetailSections));
    }

    [Fact]
    public void ADeniedConditionalModule_ContributesNoSection()
    {
        var gated = new Module(
            new FeatureDescriptor(new FeatureId("gated.feature"), RequiresEntitlement: true), WithSections(Section("gated.section")));

        Assert.Empty(FeatureComposition.Compose(new ServiceCollection(), [gated], new DenyAll()).Contributions.ServerDetailSections);
        Assert.Equal(
            ["gated.section"],
            Ids(FeatureComposition.Compose(new ServiceCollection(), [gated], new GrantAll()).Contributions.ServerDetailSections));
    }

    [Fact]
    public void ThereAreNoSectionsByDefault()
    {
        Assert.Empty(ShellContributions.Empty.ServerDetailSections);
        Assert.Empty(new ShellContributions([], []).ServerDetailSections);
        Assert.Empty(new ShellContributions([], [], [], []).ServerDetailSections);
        Assert.Empty(default(FeatureCompositionResult).Contributions.ServerDetailSections);
    }

    [Fact]
    public void WithServerDetailSections_KeepsEveryOtherList()
    {
        var setting = new ShellContribution("a.section", 0, "Key", () => new object());
        var entry = new ShellContribution("a.entry", 0, "Key", () => new object());
        var page = new ShellContribution("a.page", 0, "Key", () => new object());
        var link = new ShellLink("a.link", 0, "Key", "a.page", ShellLinkPlacement.SettingsCard);
        var section = Section("a.detail");

        var contributions = new ShellContributions([setting], [entry], [page], [link]).WithServerDetailSections([section]);

        Assert.Equal([setting], contributions.SettingsSections);
        Assert.Equal([entry], contributions.NavigationEntries);
        Assert.Equal([page], contributions.UnlistedPages);
        Assert.Equal([link], contributions.Links);
        Assert.Equal([section], contributions.ServerDetailSections);
    }

    [Fact]
    public void Arrange_TheSameRule_FirstIdWins_BuiltInIdsLeftOut_ThenOrderAndId()
    {
        var first = Section("dup.section", 3);

        var arranged = ShellContributionOrdering.Arrange(
            [first, Section("b.section", 1), Section("dup.section", -9), Section("explore", 0), Section("a.section", 1)],
            ["connection", "explore"],
            out IReadOnlyList<ServerDetailSectionRejection> rejected);

        Assert.Equal(["a.section", "b.section", "dup.section"], Ids(arranged));
        Assert.Same(first, arranged[2]);
        Assert.Equal(["dup.section", "explore"], rejected.Select(r => r.Section.Id));
    }

    [Fact]
    public void TheFactory_ReceivesTheServerId()
    {
        var serverId = Guid.NewGuid();
        Guid? seen = null;
        var section = new ServerDetailSection("a.detail", 0, "Key", id =>
        {
            seen = id;
            return new object();
        });

        section.CreateView(serverId);

        Assert.Equal(serverId, seen);
    }

    [Theory]
    [InlineData("Bad Id", "Key")]
    [InlineData("", "Key")]
    [InlineData("a.detail", " ")]
    public void ASection_NeedsAWellFormedId_AndATitleKey(string id, string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new ServerDetailSection(id, 0, key, _ => new object()));

    [Fact]
    public void ASection_NeedsAFactory_AndTheListMayNotHoldNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerDetailSection("a.detail", 0, "Key", null!));
        Assert.Throws<ArgumentNullException>(() => new ShellContributions([], [], [], [], null!));
        Assert.Throws<ArgumentException>(() => new ShellContributions([], [], [], [], [null!]));
    }
}
