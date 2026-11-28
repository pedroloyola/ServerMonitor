using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Features;

namespace ServerMonitor.Features.Tests;

/// <summary>
/// M16: server pages follow their module through the same composition gate as every other contribution, keep
/// composition order until the shell arranges them, and are arranged by the same rule. Nothing is contributed by default.
/// </summary>
public sealed class ServerPageTests
{
    private static ServerPage Page(string id, int order = 0) =>
        new(id, order, "TestTitle", _ => new object());

    private static ShellContributions WithPages(params ServerPage[] pages) =>
        ShellContributions.Empty.WithServerPages(pages);

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

    private sealed class Throws : IEntitlementProvider
    {
        public bool IsEntitled(FeatureId id) => throw new InvalidOperationException();
    }

    private static string[] Ids(IEnumerable<ServerPage> pages) => [.. pages.Select(p => p.Id)];

    [Fact]
    public void AComposedModule_ContributesItsPages_InCompositionOrder()
    {
        var result = FeatureComposition.Compose(
            new ServiceCollection(),
            [
                new Module(FeatureDescriptor.Unconditional("b.feature"), WithPages(Page("b.page", 9))),
                new Module(FeatureDescriptor.Unconditional("a.feature"), WithPages(Page("a.page", 1)))
            ],
            new DenyAll());

        Assert.Equal(["b.page", "a.page"], Ids(result.Contributions.ServerPages));
    }

    [Fact]
    public void AnUnconditionalModulesPages_NeverDependOnTheEntitlementAnswer()
    {
        var module = new Module(FeatureDescriptor.Unconditional("open.feature"), WithPages(Page("open.page")));

        foreach (IEntitlementProvider provider in new IEntitlementProvider[] { new DenyAll(), new Throws(), new GrantAll() })
        {
            Assert.Equal(["open.page"], Ids(FeatureComposition.Compose(new ServiceCollection(), [module], provider).Contributions.ServerPages));
        }
    }

    [Fact]
    public void ADeniedOrFailingConditionalModule_ContributesNoPage()
    {
        var gated = new Module(
            new FeatureDescriptor(new FeatureId("gated.feature"), RequiresEntitlement: true), WithPages(Page("gated.page")));

        Assert.Empty(FeatureComposition.Compose(new ServiceCollection(), [gated], new DenyAll()).Contributions.ServerPages);
        Assert.Empty(FeatureComposition.Compose(new ServiceCollection(), [gated], new Throws()).Contributions.ServerPages);
        Assert.Equal(
            ["gated.page"],
            Ids(FeatureComposition.Compose(new ServiceCollection(), [gated], new GrantAll()).Contributions.ServerPages));
    }

    [Fact]
    public void ThereAreNoPagesByDefault()
    {
        Assert.Empty(ShellContributions.Empty.ServerPages);
        Assert.Empty(new ShellContributions([], []).ServerPages);
        Assert.Empty(new ShellContributions([], [], [], []).ServerPages);
        Assert.Empty(new ShellContributions([], [], [], [], []).ServerPages);
        Assert.Empty(default(FeatureCompositionResult).Contributions.ServerPages);
        Assert.Empty(FeatureComposition.Compose(new ServiceCollection(), [], new DenyAll()).Contributions.ServerPages);
    }

    [Fact]
    public void WithServerPages_KeepsEveryOtherList_AndWithServerDetailSectionsKeepsThePages()
    {
        var setting = new ShellContribution("a.section", 0, "Key", () => new object());
        var entry = new ShellContribution("a.entry", 0, "Key", () => new object());
        var unlisted = new ShellContribution("a.unlisted", 0, "Key", () => new object());
        var link = new ShellLink("a.link", 0, "Key", "a.unlisted", ShellLinkPlacement.SettingsCard);
        var section = new ServerDetailSection("a.detail", 0, "Key", _ => new object());
        var page = Page("a.page");

        var contributions = new ShellContributions([setting], [entry], [unlisted], [link], [section]).WithServerPages([page]);

        Assert.Equal([setting], contributions.SettingsSections);
        Assert.Equal([entry], contributions.NavigationEntries);
        Assert.Equal([unlisted], contributions.UnlistedPages);
        Assert.Equal([link], contributions.Links);
        Assert.Equal([section], contributions.ServerDetailSections);
        Assert.Equal([page], contributions.ServerPages);

        var other = new ServerDetailSection("b.detail", 0, "Key", _ => new object());
        Assert.Equal([page], contributions.WithServerDetailSections([other]).ServerPages);
    }

    [Fact]
    public void Arrange_TheSameRule_FirstIdWins_BuiltInIdsLeftOut_ThenOrderAndId()
    {
        var first = Page("dup.page", 3);

        var arranged = ShellContributionOrdering.Arrange(
            [first, Page("b.page", 1), Page("dup.page", -9), Page("explore", 0), Page("a.page", 1)],
            ["servers", "explore"],
            out IReadOnlyList<ServerPageRejection> rejected);

        Assert.Equal(["a.page", "b.page", "dup.page"], Ids(arranged));
        Assert.Same(first, arranged[2]);
        Assert.Equal(["dup.page", "explore"], rejected.Select(r => r.Page.Id));
    }

    [Fact]
    public void TheFactory_ReceivesTheServerId()
    {
        var serverId = Guid.NewGuid();
        Guid? seen = null;
        var page = new ServerPage("a.page", 0, "Key", id =>
        {
            seen = id;
            return new object();
        });

        page.CreateView(serverId);

        Assert.Equal(serverId, seen);
    }

    [Fact]
    public void BlankOptionalKeys_MeanNone()
    {
        var page = new ServerPage("a.page", 0, "Key", _ => new object(), " ", "");

        Assert.Null(page.DetailResourceKey);
        Assert.Null(page.IconPathData);
        Assert.Equal("Detail", new ServerPage("b.page", 0, "Key", _ => new object(), "Detail").DetailResourceKey);
    }

    [Fact]
    public void APage_KeepsItsTitleAndServidores_UnlessItAsks()
    {
        var plain = Page("a.page");
        var history = new ServerPage("b.page", 0, "Key", _ => new object())
        {
            TitleNamesServer = true,
            SidebarEntry = ServerPageSidebarEntry.History,
        };

        Assert.False(plain.TitleNamesServer);
        Assert.Equal(ServerPageSidebarEntry.Servers, plain.SidebarEntry);
        Assert.True(history.TitleNamesServer);
        Assert.Equal(ServerPageSidebarEntry.History, history.SidebarEntry);
    }

    [Theory]
    [InlineData("Bad Id", "Key")]
    [InlineData("", "Key")]
    [InlineData("a.page", " ")]
    public void APage_NeedsAWellFormedId_AndATitleKey(string id, string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new ServerPage(id, 0, key, _ => new object()));

    [Fact]
    public void APage_NeedsAFactory_AndTheListMayNotHoldNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ServerPage("a.page", 0, "Key", null!));
        Assert.Throws<ArgumentNullException>(() => new ShellContributions([], [], [], [], [], null!));
        Assert.Throws<ArgumentException>(() => new ShellContributions([], [], [], [], [], [null!]));
    }
}
