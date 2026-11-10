using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
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
        AssertBefore(settings, "if (_linkCards.Count == 0) return;", "DisclosureGrid.RowSpacing =");
        AssertBefore(settings, "if (_linkCards.Count == 0) return;", "PlaceLinkCards(narrow: false);");
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
        Assert.Contains("DisclosureGrid.Children.Add(card);", settings, StringComparison.Ordinal);
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
        Assert.Equal(CardLayout.DeclaredRows, grid.Descendants().Count(e => e.Name.LocalName == "RowDefinition"));
        Assert.Equal(["SettingsOpenDataButton", "SettingsOpenAboutButton"], cards.Select(c => (string?)c.Attribute(AppSourceTree.Xaml + "Uid")));
        for (var i = 0; i < cards.Count; i++)
        {
            Assert.Equal(CardLayout.Place(i, narrow: false), (Int(cards[i], "Grid.Row"), Int(cards[i], "Grid.Column")));
        }

        // The Narrow state stacks "Sobre" under "Dados e servidores": the same cell the layout gives it.
        var setters = page.Descendants().Where(e => e.Name.LocalName == "Setter").Where(e => e.Attribute("Target") is not null).GroupBy(e => (string)e.Attribute("Target")!).ToDictionary(g => g.Key, g => (string)g.First().Attribute("Value")!);
        Assert.Equal(CardLayout.Place(1, narrow: true),
            (int.Parse(setters["AboutDisclosure.(Grid.Row)"], CultureInfo.InvariantCulture), int.Parse(setters["AboutDisclosure.(Grid.Column)"], CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoCard_TheRowOfCardsIsTheXamls(bool narrow) =>
        Assert.Equal(CardLayout.DeclaredRows, CardLayout.Rows(0, narrow));

    [Fact]
    public void WithOneCard_ItFollowsSobre_AndReflows()
    {
        // Wide: two per row, so the card starts the second row; narrow: one per row, the third.
        Assert.Equal((1, 0), CardLayout.Place(2, narrow: false));
        Assert.Equal(2, CardLayout.Rows(1, narrow: false));
        Assert.Equal((2, 0), CardLayout.Place(2, narrow: true));
        Assert.Equal(3, CardLayout.Rows(1, narrow: true));
        // and two cards share the second row in the wide layout
        Assert.Equal((1, 1), CardLayout.Place(3, narrow: false));
        Assert.Equal(2, CardLayout.Rows(2, narrow: false));
    }

    [Fact]
    public void TheSettingsPage_PlacesTheCardsAfterTheBuiltInOnes_InTheShellsOrder_ForTheCurrentWidthState()
    {
        var settings = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");

        Assert.Contains("foreach (var link in contributions.SettingsCards)", settings, StringComparison.Ordinal);
        Assert.Contains("SettingsLinkCardLayout.Place(SettingsLinkCardLayout.BuiltInCards + i, narrow);", settings, StringComparison.Ordinal);
        Assert.Contains("WidthStates.CurrentStateChanged += (_, args) => PlaceLinkCards(args.NewState?.Name == \"Narrow\");", settings, StringComparison.Ordinal);
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
