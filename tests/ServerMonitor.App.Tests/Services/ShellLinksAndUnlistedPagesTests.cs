using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Controls;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using Windows.System;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Features;
using CardLayout = ServerMonitor.App.Views.SettingsLinkCardLayout;
using SidebarLayout = ServerMonitor.App.Controls.SidebarContributionLayout;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M15.4a r3 addendum: composition-supplied unlisted pages and links (a Settings card, at most one entry at the foot of
/// the navigation), drawn by the shell's own controls. With none, nothing changes: no footer link, no card, no route.
/// </summary>
public sealed class ShellLinksAndUnlistedPagesTests
{
    private sealed class View(string name)
    {
        public string Name { get; } = name;
    }

    /// <summary>A page that is asked before it is left.</summary>
    private sealed class GuardedView(Func<Task<bool>> answer) : INavigationExitGuard
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmLeaveAsync()
        {
            Asked++;
            return answer();
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ShellContribution Page(string id, Func<object>? factory = null) =>
        new(id, 0, "TitleKey." + id, factory ?? (() => new View(id)));

    private static ShellLink Link(
        string id,
        string target,
        ShellLinkPlacement placement,
        int order = 0,
        Func<string>? label = null,
        Func<string>? hint = null,
        string? key = null) =>
        new(id, order, key ?? "LinkKey." + id, target, placement, label, hint);

    private static ShellContributions With(
        IReadOnlyList<ShellContribution>? navigation = null,
        IReadOnlyList<ShellContribution>? unlisted = null,
        IReadOnlyList<ShellLink>? links = null) =>
        new([], navigation ?? [], unlisted ?? [], links ?? []);

    private static ShellContributionHost Host(ShellContributions contributions, ILogger? logger = null, Func<string, string>? title = null) =>
        new(contributions, logger ?? NullLogger.Instance, title ?? (key => "Title of " + key), view => view is View or GuardedView);

    private static (NavigationService Navigation, ShellContributionHostTests.FrameHost Frame, ShellContributionHost Host, object SettingsPage) Router(
        ShellContributions contributions)
    {
        var host = Host(contributions);
        var services = new ServiceCollection();
        services.AddSingleton(host);
        var settingsPage = new object();
        var navigation = new NavigationService(services.BuildServiceProvider(), NullLogger<NavigationService>.Instance, _ => settingsPage);
        var frame = new ShellContributionHostTests.FrameHost();
        navigation.Initialize(frame);
        return (navigation, frame, host, settingsPage);
    }

    // ---------------------------------------------------------------- zero contributions: nothing at all

    [Fact]
    public void WithNothingSupplied_ThereIsNoFooterLinkNoCardAndNoUnlistedPage()
    {
        var host = Host(ShellContributions.Empty);
        var raised = 0;
        host.LinkTextChanged += (_, _) => raised++;
        using var shell = new ShellViewModel(new Fakes.FakeNavigationService(), host);

        Assert.Empty(host.Links);
        Assert.Null(host.FooterLink);
        Assert.Empty(host.SettingsCards);
        Assert.Empty(host.UnlistedPages);
        Assert.False(host.IsUnlistedPage("anything"));
        Assert.False(host.TryGetNavigationView("anything", out _));
        Assert.Null(shell.FooterLink);
        Assert.Null(shell.FooterLinkLabel);
        Assert.False(shell.HasUnlistedPages);
        Assert.False(shell.IsUnlistedPageShown);
        shell.RefreshLinkText();
        Assert.Equal(0, raised);
        Assert.Equal(ShellContributionHostTests.BaselineNavigationIds, host.NavigationIds);
    }

    [Fact]
    public void WithNothingSupplied_TheControlsAreNeverCreated()
    {
        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        var settings = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        // Each control returns before touching the tree when there is nothing to draw.
        AssertBefore(sidebar, "if (shell.FooterLink is not { } link) return;", "NavGrid.RowDefinitions.Insert(SidebarContributionLayout.FooterRow");
        AssertBefore(settings, "if (_linkCards.Count == 0) return;", "DisclosureGrid.ColumnDefinitions.Add(_cardColumn);");
        AssertBefore(Body(settings, "private void PlaceLinkCards()"), "if (count == 0) return;", "DisclosureGrid.RowDefinitions");
        AssertBefore(window, "if (!Shell.HasUnlistedPages) return;", "ShellSurface.Children.Add(_returnHeader);");
        // and the built-in XAML holds none of them
        Assert.DoesNotContain("SaNavFooterLinkStyle", File.ReadAllText(AppSourceTree.Full("Controls/SaSidebar.xaml")), StringComparison.Ordinal);
        Assert.DoesNotContain("ShellReturn", File.ReadAllText(AppSourceTree.Full("MainWindow.xaml")), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the foot of the navigation: one link at most

    [Fact]
    public void WithMoreThanOneFooterLink_OnlyTheFirstIsShown_AndTheOthersAreLoggedAndLeftOut()
    {
        var logger = new RecordingLogger();
        var host = Host(With(unlisted: [Page("extra.page")], links:
        [
            Link("footer.later", "extra.page", ShellLinkPlacement.SidebarFooter, order: 5),
            Link("footer.first", "extra.page", ShellLinkPlacement.SidebarFooter, order: 1),
            Link("card.one", "extra.page", ShellLinkPlacement.SettingsCard, order: 9),
        ]), logger);

        Assert.Equal("footer.first", host.FooterLink!.Id);
        Assert.Equal(["footer.first", "card.one"], host.Links.Select(link => link.Id));
        Assert.Equal(["card.one"], host.SettingsCards.Select(link => link.Id));
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("footer.later", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkWhoseTargetIsNotAShownPage_IsLeftOut_WithAWarning()
    {
        var logger = new RecordingLogger();
        var host = Host(With(
            navigation: [Page("tools.entry")],
            unlisted: [Page("broken.page", () => throw new InvalidOperationException("view failed"))],
            links:
            [
                Link("to.nowhere", "no.such.page", ShellLinkPlacement.SettingsCard),
                Link("to.broken", "broken.page", ShellLinkPlacement.SidebarFooter),
                Link("to.entry", "tools.entry", ShellLinkPlacement.SettingsCard),
            ]), logger);

        Assert.Equal(["to.entry"], host.Links.Select(link => link.Id));
        Assert.Null(host.FooterLink);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("to.nowhere", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("to.broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Links_FollowTheSameArrangement_FirstIdWins_BuiltInIdsReserved_ThenOrderAndId()
    {
        var host = Host(With(unlisted: [Page("extra.page")], links:
        [
            Link("b.card", "extra.page", ShellLinkPlacement.SettingsCard, order: 1),
            Link("a.card", "extra.page", ShellLinkPlacement.SettingsCard, order: 1),
            Link("b.card", "extra.page", ShellLinkPlacement.SettingsCard, order: -5), // a later duplicate: left out
            Link("settings", "extra.page", ShellLinkPlacement.SettingsCard),       // a built-in id: left out
            Link("first.card", "extra.page", ShellLinkPlacement.SettingsCard, order: -1),
        ]));

        Assert.Equal(["first.card", "a.card", "b.card"], host.SettingsCards.Select(link => link.Id));
    }

    // ---------------------------------------------------------------- unlisted pages: reachable by id, never listed

    [Fact]
    public void AnUnlistedPage_IsNotInTheSidebar_ButIsReachableById()
    {
        var (navigation, frame, host, _) = Router(With(unlisted: [Page("extra.page")]));
        using var shell = new ShellViewModel(navigation, host);

        Assert.Empty(shell.ContributedEntries);
        Assert.Equal(ShellContributionHostTests.BaselineNavigationIds, host.NavigationIds);
        Assert.True(shell.HasUnlistedPages);

        navigation.GoToContribution("extra.page");

        Assert.Equal("extra.page", Assert.IsType<View>(frame.Content).Name);
        Assert.Equal(NavigationDestination.Contribution, navigation.CurrentDestination);
        Assert.True(host.IsUnlistedPage("extra.page"));
        Assert.True(shell.IsUnlistedPageShown);
        Assert.Null(shell.SelectedDestination); // no sidebar destination lights up
    }

    [Fact]
    public void AnUnlistedPage_CannotTakeTheIdOfANavigationEntryOrABuiltInDestination_NorBeAnythingButAPage()
    {
        var logger = new RecordingLogger();
        var host = Host(With(
            navigation: [Page("tools.entry")],
            unlisted:
            [
                Page("tools.entry"),                       // a navigation entry has it
                Page("settings"),                          // a built-in destination
                Page("not.a.page", () => new object()),    // not a page the shell can show
                Page("extra.page"),
            ]), logger);

        Assert.Equal(["extra.page"], host.UnlistedPages.Select(page => page.Id));
        Assert.True(host.TryGetNavigationView("tools.entry", out var entry));
        Assert.Same(host.NavigationEntries.Single().View, entry); // the navigation entry, not the unlisted duplicate
        Assert.Equal(3, logger.Entries.Count(e => e.Level is LogLevel.Warning or LogLevel.Error));
    }

    // ---------------------------------------------------------------- (A) the way back from an unlisted page

    [Fact]
    public void TheWayBack_GoesToSettings_ThroughTheExitGuard_OfTheUnlistedPage()
    {
        var answer = Task.FromResult(false);
        var page = new GuardedView(() => answer);
        var (navigation, frame, host, settingsPage) = Router(With(unlisted: [Page("extra.page", () => page)]));
        using var shell = new ShellViewModel(navigation, host);
        navigation.GoToContribution("extra.page");
        Assert.True(shell.IsUnlistedPageShown);

        shell.ReturnToSettings(); // refused: stays
        Assert.Equal(1, page.Asked);
        Assert.Same(page, frame.Content);
        Assert.True(shell.IsUnlistedPageShown);

        answer = Task.FromResult(true);
        shell.ReturnToSettings(); // agreed: Settings
        Assert.Equal(2, page.Asked);
        Assert.Same(settingsPage, frame.Content);
        Assert.Equal(NavigationDestination.Settings, navigation.CurrentDestination);
        Assert.False(shell.IsUnlistedPageShown);
    }

    [Fact]
    public void TheWayBack_IsAHostControl_DrawnByTheWindow_NamedForTheNarrator_InEveryLanguage()
    {
        var header = AppSourceTree.CodeWithoutComments("Controls/ShellReturnHeader.cs");
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        Assert.Contains("AutomationProperties.SetName(button, name);", header, StringComparison.Ordinal);
        Assert.Contains("button.Click += (_, _) => activate();", header, StringComparison.Ordinal);
        Assert.Contains("SaBreadcrumbParentButtonStyle", header, StringComparison.Ordinal);
        Assert.Contains("Shell.ReturnToSettings);", window, StringComparison.Ordinal);
        Assert.Contains("_returnHeader.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;", window, StringComparison.Ordinal);
        Assert.Contains("public void ReturnToSettings() => _navigation.GoToSettings();",
            AppSourceTree.CodeWithoutComments("ViewModels/ShellViewModel.cs"), StringComparison.Ordinal);
        foreach (var language in new[] { "en-US", "pt-PT", "pt-BR" })
        {
            var resources = XDocument.Load(AppSourceTree.Full($"Resources/{language}/Resources.resw"));
            foreach (var key in new[] { "ShellReturnToSettingsText", "ShellReturnToSettingsName" })
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    resources.Root!.Elements("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value));
            }
        }
    }

    // ---------------------------------------------------------------- the footer link goes through the exit guard

    [Fact]
    public void TheFooterLink_OpensItsPage_ThroughTheExitGuardOfTheShownPage()
    {
        var (navigation, frame, host, _) = Router(With(
            unlisted: [Page("extra.page")],
            links: [Link("footer.link", "extra.page", ShellLinkPlacement.SidebarFooter)]));
        using var shell = new ShellViewModel(navigation, host);
        var answer = Task.FromResult(false);
        var shown = new GuardedView(() => answer);
        frame.Content = shown;

        shell.OpenFooterLink(); // refused: stays
        Assert.Equal(1, shown.Asked);
        Assert.Same(shown, frame.Content);

        answer = Task.FromResult(true);
        shell.OpenFooterLink();
        Assert.Equal(2, shown.Asked);
        Assert.Equal("extra.page", Assert.IsType<View>(frame.Content).Name);
        Assert.True(shell.IsUnlistedPageShown);
    }

    [Fact]
    public void TheSettingsCard_OpensItsPageThroughTheNavigation()
    {
        var settings = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");

        Assert.Contains("card.Click += (_, _) => _navigation.GoToContribution(target);", settings, StringComparison.Ordinal);
        // FIX-OVERVIEW-FIT D-4: each card goes in before "Sobre", never appended after it.
        Assert.Contains("SettingsLinkCardLayout.AddBefore(DisclosureGrid.Children, AboutDisclosure, card);", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("DisclosureGrid.Children.Add(", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("TabIndex", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ContributedSections.Children.Add(card)", settings, StringComparison.Ordinal);
        Assert.Contains("SaDisclosureButtonStyle", settings, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- dynamic label and hint

    [Fact]
    public void ALabelProviderThatThrows_ShowsTheTitleResource_LoggedOnce_AndNeverTheId()
    {
        var logger = new RecordingLogger();
        var host = Host(With(unlisted: [Page("extra.page")], links:
        [
            Link("card.throws", "extra.page", ShellLinkPlacement.SettingsCard,
                label: () => throw new InvalidOperationException("secret detail"), hint: () => throw new TimeoutException()),
            Link("card.blank", "extra.page", ShellLinkPlacement.SettingsCard, order: 1, label: () => "  ", hint: () => ""),
            Link("card.untitled", "extra.page", ShellLinkPlacement.SettingsCard, order: 2, key: "Missing.Key"),
            Link("card.live", "extra.page", ShellLinkPlacement.SettingsCard, order: 3, label: () => "Live label", hint: () => "Live hint"),
        ]), logger, key => key == "Missing.Key" ? key : "Title of " + key);
        var throwing = host.SettingsCards[0];

        Assert.Equal("Title of LinkKey.card.throws", host.LabelOf(throwing));
        Assert.Equal("Title of LinkKey.card.throws", host.LabelOf(throwing));
        Assert.Null(host.HintOf(throwing));
        Assert.Equal("Title of LinkKey.card.blank", host.LabelOf(host.SettingsCards[1]));
        Assert.Null(host.HintOf(host.SettingsCards[1]));
        Assert.Equal("Title of " + ShellContributionHost.UntitledResourceKey, host.LabelOf(host.SettingsCards[2])); // the neutral label
        Assert.Equal("Live label", host.LabelOf(host.SettingsCards[3]));
        Assert.Equal("Live hint", host.HintOf(host.SettingsCards[3]));

        var failures = logger.Entries.Where(entry => entry.Message.Contains("card.throws", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, failures.Count); // once for the label, once for the hint, however often they are read
        Assert.Contains(failures, entry => entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("secret detail", StringComparison.Ordinal));
        Assert.DoesNotContain(host.SettingsCards, card => host.LabelOf(card) == card.Id);
    }

    // ---------------------------------------------------------------- (C) when the label is read again

    [Fact]
    public void EveryNavigation_AndEachRefresh_ReadTheLabelAgain_AndNothingIsReadWhileNoLinkIsShown()
    {
        var reads = 0;
        var (navigation, _, host, _) = Router(With(
            unlisted: [Page("extra.page")],
            links: [Link("footer.link", "extra.page", ShellLinkPlacement.SidebarFooter, label: () => $"Read {++reads}")]));
        using var shell = new ShellViewModel(navigation, host);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        shell.RefreshLinkText(); // links not realized yet: nothing is read, nothing raised
        Assert.Equal(0, reads);
        Assert.DoesNotContain(nameof(ShellViewModel.FooterLinkLabel), changed);

        Assert.Equal("Read 1", shell.FooterLinkLabel); // realized and read when the sidebar builds it
        navigation.GoToContribution("extra.page");
        Assert.Contains(nameof(ShellViewModel.FooterLinkLabel), changed);
        Assert.Equal("Read 2", shell.FooterLinkLabel);

        changed.Clear();
        shell.RefreshLinkText(); // the window became active again, or the theme changed
        Assert.Contains(nameof(ShellViewModel.FooterLinkLabel), changed);
        Assert.Contains(nameof(ShellViewModel.FooterLinkHint), changed);
    }

    [Fact]
    public void TheWindow_RefreshesTheLinks_WhenItBecomesActive_AndAfterAThemeRemount()
    {
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        var activated = Body(window, "private void OnWindowActivated(");
        var theme = Body(window, "private void OnActualThemeChanged(");

        Assert.Contains("if (_isWindowActive) Shell.RefreshLinkText();", activated, StringComparison.Ordinal);
        AssertBefore(theme, "_themeRemountFocus.RemountAll(", "Shell.RefreshLinkText();");
        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        Assert.Contains("nameof(ShellViewModel.FooterLinkLabel) or nameof(ShellViewModel.FooterLinkHint)) ApplyFooterText();", sidebar, StringComparison.Ordinal);
        var settings = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");
        Assert.Contains("_contributions.LinkTextChanged += OnLinkTextChanged;", settings, StringComparison.Ordinal);
        Assert.Contains("_contributions.LinkTextChanged -= OnLinkTextChanged;", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostControls_ReadLabelAndHintOnlyThroughTheShell()
    {
        // The providers are called only by the host (LabelOf/HintOf), inside its try/catch.
        foreach (var file in new[] { "Controls/SaSidebar.xaml.cs", "Views/SettingsPage.xaml.cs", "MainWindow.xaml.cs", "ViewModels/ShellViewModel.cs" })
        {
            var code = AppSourceTree.CodeWithoutComments(file);
            Assert.DoesNotContain(".Label(", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".Label?.Invoke", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".Hint(", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".Hint?.Invoke", code, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- (B) the Settings cards join the row of link cards

    [Fact]
    public void TheCardLayout_PlacesTheBuiltInCardsExactlyAsTheXaml()
    {
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var grid = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "DisclosureGrid");
        var cards = grid.Elements().Where(e => e.Name.LocalName == "Button").ToList();

        Assert.Equal(CardLayout.BuiltInCards, cards.Count);
        int Declared(string definitions) => grid.Elements().Single(e => e.Name.LocalName == definitions).Elements().Count();
        Assert.Equal(CardLayout.DeclaredRows, Declared("Grid.RowDefinitions"));
        Assert.Equal(CardLayout.DeclaredColumns, Declared("Grid.ColumnDefinitions"));
        Assert.Equal(["SettingsOpenDataButton", "SettingsOpenAboutButton"], cards.Select(c => (string?)c.Attribute(AppSourceTree.Xaml + "Uid")));
        Assert.Equal(CardLayout.Place(CardLayout.DataSequence, CardLayout.DeclaredColumns), (Int(cards[0], "Grid.Row"), Int(cards[0], "Grid.Column")));
        Assert.Equal(CardLayout.Place(CardLayout.AboutSequence(0), CardLayout.DeclaredColumns), (Int(cards[1], "Grid.Row"), Int(cards[1], "Grid.Column")));
        Assert.Equal("AboutDisclosure", (string?)cards[1].Attribute(AppSourceTree.Xaml + "Name"));

        // The Narrow state no longer moves "Sobre" (the page places it, see below) and still zeroes the second column.
        var setters = page.Descendants().Where(e => e.Name.LocalName == "Setter" && e.Attribute("Target") is not null)
            .Select(e => ((string)e.Attribute("Target")!, (string?)e.Attribute("Value"))).ToList();
        Assert.DoesNotContain(setters, s => s.Item1.StartsWith("AboutDisclosure.", StringComparison.Ordinal));
        Assert.Contains(("DisclosureColumn2.Width", "0"), setters);
        // ... and the page puts "Sobre" where those setters did, under "Dados e servidores".
        Assert.Equal((1, 0), CardLayout.Place(CardLayout.AboutSequence(0), 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoCard_TheRowOfCardsIsTheXamls(bool narrow)
    {
        // Today's built-in texts (about 121 at most) fit two columns from the Narrow trigger up.
        var columns = CardLayout.Columns(0, narrow ? 699 : 700, 121);
        Assert.Equal(narrow ? 1 : CardLayout.DeclaredColumns, columns);
        Assert.Equal(CardLayout.DeclaredRows, CardLayout.Rows(0, columns));
        Assert.Equal((0, 0), CardLayout.Place(CardLayout.DataSequence, columns));
        Assert.Equal(narrow ? (1, 0) : (0, 1), CardLayout.Place(CardLayout.AboutSequence(0), columns));
    }

    [Fact]
    public void WithOneCard_ItSitsInTheMiddle_ThreePerRow_AndOnePerRowWhenNarrow()
    {
        // Wide (Figma 303:486): Dados | the card | Sobre, on one row.
        var wide = CardLayout.Columns(1, 1000, 121);
        Assert.Equal(3, wide);
        Assert.Equal((0, 0), CardLayout.Place(CardLayout.DataSequence, wide));
        Assert.Equal((0, 1), CardLayout.Place(CardLayout.ContributedSequence(0), wide));
        Assert.Equal((0, 2), CardLayout.Place(CardLayout.AboutSequence(1), wide));
        Assert.Equal(1, CardLayout.UsedRows(1, wide));
        Assert.Equal(CardLayout.DeclaredRows, CardLayout.Rows(1, wide));
        // Narrow (or a text that does not fit, FIX-OVERVIEW-FIT D-3): one per row, same order.
        var stacked = CardLayout.Columns(1, 699, 121);
        Assert.Equal(1, stacked);
        Assert.Equal((0, 0), CardLayout.Place(CardLayout.DataSequence, stacked));
        Assert.Equal((1, 0), CardLayout.Place(CardLayout.ContributedSequence(0), stacked));
        Assert.Equal((2, 0), CardLayout.Place(CardLayout.AboutSequence(1), stacked));
        Assert.Equal(3, CardLayout.Rows(1, stacked));
        // Two cards: the row fills, "Sobre" starts the next one.
        Assert.Equal(3, CardLayout.Columns(2, 1000, 121));
        Assert.Equal((0, 2), CardLayout.Place(CardLayout.ContributedSequence(1), wide));
        Assert.Equal((1, 0), CardLayout.Place(CardLayout.AboutSequence(2), wide));
        Assert.Equal(2, CardLayout.UsedRows(2, wide));
    }

    /// <summary>
    /// FIX-OVERVIEW-FIT D-4: the grid's children (the tree order that Tab, Shift+Tab, Narrator and UI Automation follow)
    /// are the visual order: "Dados e servidores", the contributed cards in the shell's order, then "Sobre". Without a
    /// contributed card the children are the XAML's, unchanged. The cards come from a host over test contributions.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void TheTreeOrder_IsTheVisualOrder_WithAndWithoutContributedCards(int contributed)
    {
        var host = Host(With(unlisted: [Page("extra.page")], links:
            [.. Enumerable.Range(0, contributed).Select(i => Link($"card.{i}", "extra.page", ShellLinkPlacement.SettingsCard, order: i))]));
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var grid = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "DisclosureGrid");
        var children = grid.Elements().Where(e => e.Name.LocalName == "Button").Select(e => (string)e.Attribute(AppSourceTree.Xaml + "Uid")!).ToList();
        const string Data = "SettingsOpenDataButton";
        const string About = "SettingsOpenAboutButton";
        Assert.Equal([Data, About], children);

        foreach (var card in host.SettingsCards)
        {
            CardLayout.AddBefore(children, About, card.Id);
        }

        Assert.Equal(contributed, host.SettingsCards.Count);
        Assert.Equal([Data, .. Enumerable.Range(0, contributed).Select(i => $"card.{i}"), About], children);
        // The tree index of every card is its place in the layout, so focus moves cell by cell in reading order.
        Assert.Equal(CardLayout.DataSequence, children.IndexOf(Data));
        for (var i = 0; i < contributed; i++)
        {
            Assert.Equal(CardLayout.ContributedSequence(i), children.IndexOf($"card.{i}"));
        }

        Assert.Equal(CardLayout.AboutSequence(contributed), children.IndexOf(About));
    }

    [Fact]
    public void WithoutSobreAmongTheChildren_ACardIsAddedLast()
    {
        List<string> children = ["data"];
        CardLayout.AddBefore(children, "about", "card");
        Assert.Equal(["data", "card"], children);
    }

    [Fact]
    public void TheSettingsPage_PlacesSobreAlways_AndTheCardsBetween_ForTheCurrentWidthState()
    {
        var settings = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");
        var place = Body(settings, "private void PlaceLinkCards()");

        Assert.Contains("foreach (var link in contributions.SettingsCards)", settings, StringComparison.Ordinal);
        Assert.Contains("WidthStates.CurrentStateChanged += (_, _) => PlaceLinkCards();", settings, StringComparison.Ordinal);
        Assert.Contains("Loaded += (_, _) => PlaceLinkCards();", settings, StringComparison.Ordinal);
        // FIX-OVERVIEW-FIT D-3: the columns come from the viewport and the widest card text, first of all.
        AssertBefore(place, "var columns = SettingsLinkCardLayout.Columns(count, ActualWidth, WidestCardText());", "SettingsLinkCardLayout.Place(");
        // "Sobre" is placed with or without cards (the XAML's Narrow state no longer does it), then the gaps, then the cards' own work.
        AssertBefore(place, "SettingsLinkCardLayout.Place(SettingsLinkCardLayout.AboutSequence(count), columns);", "Grid.SetColumn(AboutDisclosure, aboutColumn);");
        AssertBefore(place, "Grid.SetColumn(AboutDisclosure, aboutColumn);", "if (count == 0) return;");
        AssertBefore(place, "DisclosureGrid.RowSpacing =", "if (count == 0) return;");
        AssertBefore(place, "if (count == 0) return;", "_cardColumn!.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);");
        Assert.Contains("SettingsLinkCardLayout.Place(SettingsLinkCardLayout.ContributedSequence(i), columns);", place, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsCard_IsTheFigmasCard_DrawnFromATemplate_ThatFollowsTheTheme()
    {
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var template = page.Descendants().Single(e => e.Name.LocalName == "DataTemplate"
            && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SettingsLinkCardTemplate");
        Assert.Equal("views:SettingsLinkCardContent", (string?)template.Attribute(AppSourceTree.Xaml + "DataType"));

        // P-4: both icons take the theme resource, re-evaluated on theme and high-contrast changes, like the built-in cards.
        var icons = template.Descendants().Where(e => e.Name.LocalName == "SaIcon").ToList();
        Assert.Equal(2, icons.Count);
        Assert.All(icons, icon => Assert.Equal("{ThemeResource SaIconSecondaryStyle}", (string?)icon.Attribute("Style")));
        Assert.Equal("{x:Bind IconPathData}", (string?)icons[0].Attribute("Data"));
        Assert.Equal("18", (string?)icons[0].Attribute("Size"));
        // P-3: the label, then the hint visible after it (12 Medium, secondary), trimmed together.
        var text = template.Descendants().Single(e => e.Name.LocalName == "TextBlock");
        Assert.Equal("CharacterEllipsis", (string?)text.Attribute("TextTrimming"));
        var runs = text.Elements().Where(e => e.Name.LocalName == "Run").ToList();
        Assert.Equal("{x:Bind Label, Mode=OneWay}", (string?)runs[0].Attribute("Text"));
        Assert.Equal("{x:Bind HintText, Mode=OneWay}", (string?)runs[1].Attribute("Text"));
        Assert.Equal("{StaticResource SaFontSizeCaption}", (string?)runs[1].Attribute("FontSize"));
        Assert.Equal("Medium", (string?)runs[1].Attribute("FontWeight"));
        Assert.Equal("{ThemeResource SaTextSecondaryBrush}", (string?)runs[1].Attribute("Foreground"));
        Assert.Equal(string.Empty, ServerMonitor.App.Views.SettingsLinkCardContent.HintTextOf(null));
        Assert.Equal(string.Empty, ServerMonitor.App.Views.SettingsLinkCardContent.HintTextOf("  "));
        // r5 (Prism INFO-2): 12 px between label and hint: an em space in the 12 px hint run.
        Assert.Equal("\u2003Hint", ServerMonitor.App.Views.SettingsLinkCardContent.HintTextOf("Hint"));
        Assert.Equal("\u2003", ServerMonitor.App.Views.SettingsLinkCardContent.EmSpace);
        Assert.Contains("<x:Double x:Key=\"SaFontSizeCaption\">12</x:Double>",
            File.ReadAllText(AppSourceTree.Full("Styles/Tokens/Typography.xaml")), StringComparison.Ordinal);
        Assert.Single(text.Elements(), e => e.Name.LocalName == "Run" && (string?)e.Attribute("Text") == "{x:Bind HintText, Mode=OneWay}");

        // The page builds the card from that template and never resolves a theme style once in code.
        var settings = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");
        Assert.Contains("var template = (DataTemplate)Resources[\"SettingsLinkCardTemplate\"];", settings, StringComparison.Ordinal);
        Assert.Contains("ContentTemplate = template,", settings, StringComparison.Ordinal);
        Assert.Contains("content.SetText(text, hint);", settings, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(card, text);", settings, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(card, hint ?? string.Empty);", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SaIconSecondaryStyle", settings, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the footer row in the sidebar

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TheFooterLink_SitsJustAboveSettings_WithAnyNumberOfEntries(int entries)
    {
        var grid = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "NavGrid");
        var rows = grid.Descendants().Count(e => e.Name.LocalName == "RowDefinition");
        var settings = grid.Elements().Single(e => (string?)e.Attribute("Tag") == "Settings");
        var indicator = grid.Elements().Single(e => e.Attribute("Grid.RowSpan") is not null);
        Assert.Equal(SidebarLayout.SettingsRow, Int(settings, "Grid.Row"));

        // As the sidebar does it: the footer row first, then the contributed entries.
        var settingsRow = SidebarLayout.RowAfterFooter(Int(settings, "Grid.Row"));
        var span = SidebarLayout.RowSpanAfterFooter(Int(indicator, "Grid.Row"), Int(indicator, "Grid.RowSpan"));
        var footerRow = SidebarLayout.FooterRow;
        rows += 1;
        span = SidebarLayout.RowSpanAfterInsert(0, span, entries);
        settingsRow = SidebarLayout.RowAfterInsert(settingsRow, entries);
        footerRow = SidebarLayout.RowAfterInsert(footerRow, entries);
        rows += entries;

        Assert.Equal(rows - 1, settingsRow);
        Assert.Equal(rows - 2, footerRow);
        Assert.Equal(rows, span); // the selection indicator still spans every row
    }

    // ---------------------------------------------------------------- widths: owner decision, sidebar 224, items 176

    [Fact]
    public void EveryExpandedItem_IsAsWideAsTheFooterLink_AndTheSidebarFitsThemWithItsPadding()
    {
        var navigation = AppSourceTree.LoadXaml("Styles/Components/Sa.Navigation.xaml");
        string Width(string key) => (string)navigation.Root!.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key)
            .Descendants().Single(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Width").Attribute("Value")!;
        var spacing = File.ReadAllText(AppSourceTree.Full("Styles/Tokens/Spacing.xaml"));

        Assert.Equal(176, ShellLayout.ItemWidth);
        Assert.Equal(ShellLayout.ItemWidth.ToString(CultureInfo.InvariantCulture), Width("SaNavItemStyle"));
        Assert.Equal(ShellLayout.ItemWidth.ToString(CultureInfo.InvariantCulture), Width("SaNavFooterLinkStyle"));
        Assert.Contains("<Thickness x:Key=\"SaSidebarPadding\">24,24,24,40</Thickness>", spacing, StringComparison.Ordinal);
        Assert.Equal(24 + ShellLayout.ItemWidth + 24, ShellLayout.ExpandedWidth);
        Assert.Equal(ShellLayout.ItemWidth - 12 - 12 - 20 - 10, ShellLayout.ItemLabelMaxWidth);
        // The rail is unchanged: the 46 circle inside 16 + 16, and the same breakpoint.
        Assert.Contains("<Thickness x:Key=\"SaRailPadding\">16,24,16,40</Thickness>", spacing, StringComparison.Ordinal);
        Assert.True(16 + ShellLayout.RailItemWidth + 16 <= ShellLayout.RailWidth);
        Assert.Equal(1040, ShellLayout.RailBreakpoint);
        // The footer link uses the common width, never a width of its own.
        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        Assert.Contains("_footerLink.Width = rail ? ShellLayout.RailItemWidth : ShellLayout.ItemWidth;", sidebar, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- r4: the pill follows the Figma (owner decision)

    [Fact]
    public void TheFooterLink_IsTheFigmaPill_FromTheThemesTokens()
    {
        var navigation = AppSourceTree.LoadXaml("Styles/Components/Sa.Navigation.xaml");
        var setters = navigation.Root!.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaNavFooterLinkStyle")
            .Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(e => (string)e.Attribute("Property")!, e => (string?)e.Attribute("Value"));

        Assert.Equal(SidebarLayout.FooterLinkHeight.ToString(CultureInfo.InvariantCulture), setters["Height"]);
        Assert.Equal(36, SidebarLayout.FooterLinkHeight);
        Assert.Equal(SidebarLayout.FooterLinkCornerRadius.ToString(CultureInfo.InvariantCulture), setters["CornerRadius"]);
        Assert.Equal(12, SidebarLayout.FooterLinkCornerRadius);
        Assert.Equal("176", setters["Width"]);
        // Solid fill, no outline (transparent, ButtonText in high contrast), primary text, 12 Regular; tokens, no literal colour.
        Assert.Equal("{ThemeResource SaInteriorBrush}", setters["Background"]);
        Assert.Equal("{ThemeResource SaSecondaryBorderBrush}", setters["BorderBrush"]);
        Assert.Equal("{ThemeResource SaTextBrush}", setters["Foreground"]);
        Assert.Equal("{StaticResource SaFontSizeCaption}", setters["FontSize"]);
        Assert.Equal("Normal", setters["FontWeight"]);
        Assert.Equal("True", setters["UseSystemFocusVisuals"]);
        Assert.DoesNotContain(setters.Values, value => value is not null && value.StartsWith('#'));
        var colors = File.ReadAllText(AppSourceTree.Full("Styles/Tokens/Color.Semantic.xaml"));
        Assert.Contains("<SolidColorBrush x:Key=\"SaInteriorBrush\" Color=\"{ThemeResource SystemColorWindowColor}\" />", colors, StringComparison.Ordinal);
        Assert.Contains("<SolidColorBrush x:Key=\"SaSecondaryBorderBrush\" Color=\"{ThemeResource SystemColorButtonTextColor}\" />", colors, StringComparison.Ordinal);
        Assert.Equal(2, colors.Split("<SolidColorBrush x:Key=\"SaSecondaryBorderBrush\" Color=\"Transparent\" />").Length - 1);
    }

    [Fact]
    public void TheFooterLink_ShowsOneLine_LabelDotHint_Expanded_AndTheIconCircle_InTheRail()
    {
        Assert.Equal("Label · Hint", SidebarLayout.FooterLinkText("Label", "Hint"));
        Assert.Equal("Label", SidebarLayout.FooterLinkText("Label", null));
        Assert.Equal("Label", SidebarLayout.FooterLinkText("Label", " "));
        Assert.Equal(ShellLayout.RailItemWidth, SidebarLayout.RailFooterLinkHeight);
        Assert.Equal(ShellLayout.RailItemWidth / 2, SidebarLayout.RailFooterLinkCornerRadius);

        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        var build = Body(sidebar, "private void AddFooterLink(ShellViewModel shell)");
        Assert.Contains("Style = (Style)Application.Current.Resources[\"SaCaptionTextStyle\"],", build, StringComparison.Ordinal);
        Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis,", build, StringComparison.Ordinal);
        Assert.Contains("new GridLength(SidebarContributionLayout.FooterLinkHeight)", build, StringComparison.Ordinal);
        Assert.Contains("_footerLabel.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;", sidebar, StringComparison.Ordinal);
        Assert.Contains("_footerIcon.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;", sidebar, StringComparison.Ordinal);
        Assert.Contains("_footerLink.Height = rail ? SidebarContributionLayout.RailFooterLinkHeight : SidebarContributionLayout.FooterLinkHeight;", sidebar, StringComparison.Ordinal);
        Assert.Contains("NavGrid.RowDefinitions[Grid.GetRow(_footerLink)].Height = new GridLength(_footerLink.Height);", sidebar, StringComparison.Ordinal);
        var text = Body(sidebar, "private void ApplyFooterText()");
        Assert.Contains("_footerLabel.Text = text;", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(_footerLink, label);", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(_footerLink, hint ?? string.Empty);", text, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(_footerLink, SidebarContributionLayout.FooterLinkToolTip(_rail, _footerLabel.IsTextTrimmed, text));", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFooterLinksTooltip_IsTheWholeLine_InTheRail_AndExpandedOnlyWhenTrimmed()
    {
        // r5 (Prism INFO-1)
        Assert.Equal("Label · Hint", SidebarLayout.FooterLinkToolTip(rail: true, trimmed: false, "Label · Hint"));
        Assert.Equal("Label · Hint", SidebarLayout.FooterLinkToolTip(rail: true, trimmed: true, "Label · Hint"));
        Assert.Equal("Label · Hint", SidebarLayout.FooterLinkToolTip(rail: false, trimmed: true, "Label · Hint"));
        Assert.Null(SidebarLayout.FooterLinkToolTip(rail: false, trimmed: false, "Label · Hint"));

        // The tooltip is set again whenever the trimming changes (a resize, another language, a new label).
        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        Assert.Contains("_footerLabel.IsTextTrimmedChanged += (_, _) => ApplyFooterText();", Body(sidebar, "private void AddFooterLink(ShellViewModel shell)"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- r4 (Prism P-1): the pill is reached by keyboard

    [Fact]
    public void TheFooterLink_IsInTheSidebarsArrowOrder_BetweenTheLastEntryAndSettings_ForFocusOnly()
    {
        // The rule: radio items, and buttons marked as members; nothing else.
        Assert.True(SaGroupNavigation.IsMember(isRadioItem: true, isButton: true, markedMember: false));
        Assert.True(SaGroupNavigation.IsMember(isRadioItem: false, isButton: true, markedMember: true));
        Assert.False(SaGroupNavigation.IsMember(isRadioItem: false, isButton: true, markedMember: false));
        Assert.False(SaGroupNavigation.IsMember(isRadioItem: false, isButton: false, markedMember: true));

        // Overview, Servers, History, the footer link, Settings: down from History reaches the link, then Settings.
        Assert.Equal(3, SaGroupNavigation.ArrowTarget(VirtualKey.Down, 2, 5));
        Assert.Equal(4, SaGroupNavigation.ArrowTarget(VirtualKey.Down, 3, 5));
        Assert.Equal(3, SaGroupNavigation.ArrowTarget(VirtualKey.Up, 4, 5));
        Assert.Equal(2, SaGroupNavigation.ArrowTarget(VirtualKey.Up, 3, 5));

        // The sidebar marks the link and puts it in the children just before Settings, with the contributed items
        // before it (the visual order); the group walks every member and selects radio items only.
        var sidebar = AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        var build = Body(sidebar, "private void AddFooterLink(ShellViewModel shell)");
        Assert.Contains("SaGroupNavigation.SetIsGroupMember(_footerLink, true);", build, StringComparison.Ordinal);
        Assert.Contains("NavGrid.Children.Insert(NavGrid.Children.IndexOf(SettingsItem), _footerLink);", build, StringComparison.Ordinal);
        Assert.Contains("var settingsIndex = NavGrid.Children.IndexOf((UIElement?)_footerLink ?? SettingsItem);", sidebar, StringComparison.Ordinal);
        AssertBefore(sidebar, "AddFooterLink(shell);", "var settingsIndex =");
        var group = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaGroupNavigation.cs");
        Assert.Contains("if (e.OriginalSource is not Control item || !IsMemberOf(item))", group, StringComparison.Ordinal);
        Assert.Contains(".Where(i => IsMemberOf(i) && i.IsEnabled && i.Visibility == Visibility.Visible)", group, StringComparison.Ordinal);
        Assert.Contains("if (items[target] is RadioButton radio && SelectsOnFocusMove(", group, StringComparison.Ordinal);
        Assert.Contains("IsMember(control is RadioButton, control is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase, GetIsGroupMember(control))", group, StringComparison.Ordinal);
        // the group is one Tab stop entered at the selected item, like Definições
        Assert.Contains("p:SaGroupNavigation.Mode=\"FocusOnly\"", File.ReadAllText(AppSourceTree.Full("Controls/SaSidebar.xaml")), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- r4 (Prism P-5): the way back follows the padding

    [Fact]
    public void TheWayBack_IsAlignedWithThePagesPadding_WideAndNarrow()
    {
        var spacing = File.ReadAllText(AppSourceTree.Full("Styles/Tokens/Spacing.xaml"));
        Assert.Contains($"<Thickness x:Key=\"SaPagePadding\">{ShellReturnHeader.Side}</Thickness>", spacing, StringComparison.Ordinal);
        Assert.Contains($"<Thickness x:Key=\"SaPagePaddingCompact\">{ShellReturnHeader.CompactSide},40,{ShellReturnHeader.CompactSide},24</Thickness>", spacing, StringComparison.Ordinal);
        Assert.Contains($"MaxWidth=\"{ShellReturnHeader.NarrowBelow}\"", File.ReadAllText(AppSourceTree.Full("Views/SettingsPage.xaml")), StringComparison.Ordinal);

        Assert.Equal(new Microsoft.UI.Xaml.Thickness(24, 36, 24, 0), ShellReturnHeader.MarginFor(699));
        Assert.Equal(new Microsoft.UI.Xaml.Thickness(40, 36, 40, 0), ShellReturnHeader.MarginFor(700));
        Assert.Equal(new Microsoft.UI.Xaml.Thickness(40, 36, 40, 0), ShellReturnHeader.MarginFor(double.PositiveInfinity));

        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("ContentFrame.SizeChanged += OnContentFrameSizeChanged;", window, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.SizeChanged -= OnContentFrameSizeChanged;", window, StringComparison.Ordinal);
        Assert.Contains("_returnHeader.Margin = Controls.ShellReturnHeader.MarginFor(args.NewSize.Width);", window, StringComparison.Ordinal);
        Assert.Contains("_returnHeader.Margin = Controls.ShellReturnHeader.MarginFor(ContentFrame.ActualWidth);", window, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static int Int(XElement element, string name) =>
        element.Attribute(name) is { } attribute ? int.Parse(attribute.Value, CultureInfo.InvariantCulture) : 0;

    private static void AssertBefore(string code, string first, string then)
    {
        var a = code.IndexOf(first, StringComparison.Ordinal);
        var b = code.IndexOf(then, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + first);
        Assert.True(b >= 0, "missing: " + then);
        Assert.True(a < b, $"'{first}' must come before '{then}'");
    }

    /// <summary>The body of the method that starts with <paramref name="signature"/>.</summary>
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return code[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }
}
