using System.ComponentModel;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.5 fix round 2 (Boss decisions + Prism / Beacon / Cortex / Atlas C1): transient notices that close themselves on a
/// FakeTimeProvider and never outlive the visit, server-scoped operation errors, the Figma section-11 destructive
/// confirmation, the Ligação card's rows, the accessible names of the list row and the inline notice, the hidden-servers
/// list semantics, and the layout contracts of the three pages. Synthetic data only; no wall clock.
/// </summary>
public sealed class Ui5R2Tests
{
    private static readonly DateTimeOffset Now = Ui4TestKit.Now;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);
    private static readonly XNamespace Primitives = "using:ServerMonitor.App.Controls.Primitives";

    // ---- Boss decision 2: transient notices ---------------------------------------------------------------------------

    [Fact]
    public async Task TheServersNotice_ClosesItself_AfterTheFixedDuration_NeverEarlier()
    {
        var (kit, notice) = await HiddenFromTheDetailAsync();
        var clock = new FakeTimeProvider(Now);
        using var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, new PresentationClock(clock), notice);
        var raised = Recorder(servers);

        Assert.True(servers.IsNoticeOpen);
        clock.Advance(TransientNoticeTimer.Duration - Tick);
        Assert.True(servers.IsNoticeOpen);
        clock.Advance(Tick);

        Assert.False(servers.IsNoticeOpen);
        Assert.Equal(1, raised.Count(name => name == nameof(ServersViewModel.IsNoticeOpen)));
        Assert.Equal(TimeSpan.FromSeconds(8), TransientNoticeTimer.Duration);
    }

    [Fact]
    public async Task TheServersNotice_EndsWithTheVisit_AndIsNeverShownAgain()
    {
        var (kit, notice) = await HiddenFromTheDetailAsync();
        var clock = new FakeTimeProvider(Now);
        var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, new PresentationClock(clock), notice);
        var raised = Recorder(servers);

        servers.Dispose(); // the page is left before the notice closed itself
        clock.Advance(TransientNoticeTimer.Duration * 2);

        Assert.Empty(raised); // the countdown died with the visit: nothing fires into a disposed view model
        using var again = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, new PresentationClock(clock), notice);
        Assert.False(again.IsNoticeOpen);
    }

    [Fact]
    public async Task ClosingTheServersNotice_StopsItsCountdown()
    {
        var (kit, notice) = await HiddenFromTheDetailAsync();
        var clock = new FakeTimeProvider(Now);
        using var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, new PresentationClock(clock), notice);

        servers.DismissNoticeCommand.Execute(null);
        var raised = Recorder(servers);
        clock.Advance(TransientNoticeTimer.Duration);

        Assert.False(servers.IsNoticeOpen);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task TheDataToast_ClosesItself_AndANewerToastGetsItsFullTime()
    {
        var clock = new FakeTimeProvider(Now);
        var settings = await SettingsWithHiddenAsync(clock);

        settings.ViewModel.HiddenServers[0].RestoreCommand.Execute(null); // "Servidor restaurado" at t0
        clock.Advance(TimeSpan.FromSeconds(5));
        settings.ViewModel.ResetIgnoredCommand.Execute(null);            // "Dispositivos repostos" at t0 + 5 s
        Assert.Equal("Dispositivos repostos", settings.ViewModel.ToastTitle);

        clock.Advance(TimeSpan.FromSeconds(5)); // the first toast's countdown (t0 + 8 s) must not close the newer one
        Assert.True(settings.ViewModel.IsToastOpen);
        clock.Advance(TransientNoticeTimer.Duration - TimeSpan.FromSeconds(5) - Tick);
        Assert.True(settings.ViewModel.IsToastOpen);
        clock.Advance(Tick);

        Assert.False(settings.ViewModel.IsToastOpen);
    }

    [Fact]
    public async Task TheDataToast_EndsWhenThePageIsLeft_AndALaterVisitNeverShowsIt()
    {
        var clock = new FakeTimeProvider(Now);
        var settings = await SettingsWithHiddenAsync(clock);
        settings.ViewModel.HiddenServers[0].RestoreCommand.Execute(null);
        Assert.True(settings.ViewModel.IsToastOpen);

        settings.ViewModel.NotifyDataNavigatedFrom(); // Unloaded (Cortex C1 N-C2)
        Assert.False(settings.ViewModel.IsToastOpen);

        settings.ViewModel.NotifyDataNavigatedTo(); // a later visit
        var raised = Recorder(settings.ViewModel);
        clock.Advance(TransientNoticeTimer.Duration);
        Assert.False(settings.ViewModel.IsToastOpen);
        Assert.DoesNotContain(nameof(SettingsViewModel.IsToastOpen), raised);
    }

    [Theory]
    [InlineData("Views/ServersPage.xaml")]
    [InlineData("Views/SettingsDataPage.xaml")]
    public void TheToasts_HaveTheirOwnRow_UnderTheContent_AndStayLive(string file)
    {
        var document = AppSourceTree.LoadXaml(file);
        var host = document.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PageHost");
        var rows = host.Elements().Single(e => e.Name.LocalName == "Grid.RowDefinitions").Elements().ToList();
        var toast = Assert.Single(document.Descendants(), e => e.Name.LocalName == "SaToast");

        Assert.Equal(new[] { "*", "Auto" }, rows.Select(row => (string?)row.Attribute("Height")));
        Assert.Equal("1", (string?)toast.Attribute("Grid.Row"));
        Assert.Equal("Polite", (string?)toast.Attribute("AutomationProperties.LiveSetting"));
    }

    // ---- Boss decision 3: an error about one server never shows on another server's Detail ---------------------------

    [Fact]
    public async Task AServerScopedError_ShowsOnlyOnThatServersDetail_AGlobalOneEverywhere()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("nas", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet);
        kit.Servers.HideOverride = _ => Task.FromResult(false);
        await kit.Dashboard.LoadAsync();
        using var web = Open(kit, "web");
        using var nas = Open(kit, "nas");

        await ((AsyncRelayCommand)web.HideCommand).ExecuteAsync(); // fails: an error about web

        Assert.True(kit.Dashboard.IsOperationErrorOpen);
        Assert.Equal(fleet.IdOf("web"), kit.Dashboard.OperationErrorServerId);
        Assert.True(web.IsOperationErrorOpen);
        Assert.False(nas.IsOperationErrorOpen); // Beacon C1 N4: never presented as nas's own failure

        kit.Dashboard.HandleError(new InvalidOperationException("synthetic"), "load servers"); // a global error
        Assert.Null(kit.Dashboard.OperationErrorServerId);
        Assert.True(nas.IsOperationErrorOpen);
        Assert.True(web.IsOperationErrorOpen);

        nas.IsOperationErrorOpen = false; // closing it anywhere closes the shared notice (UI.4 SHOULD-3)
        Assert.False(kit.Dashboard.IsOperationErrorOpen);
        Assert.False(web.IsOperationErrorOpen);
    }

    [Fact]
    public async Task ARescopedError_IsAnnounced_ToTheDetailItNowConcerns()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("nas", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet);
        kit.Servers.HideOverride = _ => Task.FromResult(false);
        await kit.Dashboard.LoadAsync();
        using var web = Open(kit, "web");
        using var nas = Open(kit, "nas");
        await ((AsyncRelayCommand)web.HideCommand).ExecuteAsync();
        var raised = Recorder(nas);

        await ((AsyncRelayCommand)nas.HideCommand).ExecuteAsync(); // still open, now about nas

        Assert.Contains(nameof(ServerDetailViewModel.IsOperationErrorOpen), raised);
        Assert.True(nas.IsOperationErrorOpen);
        Assert.False(web.IsOperationErrorOpen);
    }

    // ---- runtime QA: focus goes back to "…" when Ocultar / Remover does not leave the page ------------------------------

    [Fact]
    public async Task ACancelledRemove_AsksTheViewToRefocusTheActions_ASuccessDoesNot()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var dialogs = new DecidingDialogs();
        var kit = Ui4TestKit.Create(fleet, dialogs: dialogs);
        kit.Servers.HideOverride = id =>
        {
            var index = kit.Servers.Servers.FindIndex(server => server.Id == id);
            kit.Servers.Servers[index] = kit.Servers.Servers[index] with { IsHidden = true };
            kit.Servers.RaiseChanged();
            return Task.FromResult(true);
        };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");
        var requests = 0;
        detail.ActionsFocusRequested += (_, _) => requests++;

        await ((AsyncRelayCommand)detail.RemoveCommand).ExecuteAsync(); // cancelled in its confirmation
        Assert.Equal(1, requests);

        await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync(); // succeeds: the page leaves, no refocus
        Assert.Equal(1, requests);
        Assert.Equal(1, kit.Navigation.ServersCount); // its own Ocultar: to Servidores, with the notice
    }

    [Fact]
    public void TheThemeRefresh_ListensToTheWindowRoot_AndRemountsThePage()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaThemeRefresh.cs");

        Assert.Contains("XamlRoot?.Content is FrameworkElement root", code, StringComparison.Ordinal);
        Assert.Contains("root.ActualThemeChanged += OnRootThemeChanged", code, StringComparison.Ordinal);
        Assert.Contains("page.Content = null;", code, StringComparison.Ordinal);
        Assert.Contains("page.Content = content;", code, StringComparison.Ordinal);
    }

    // ---- Prism C1 M-3: the Figma section-11 destructive confirmation ------------------------------------------------------

    [Fact]
    public void TheRemoveConfirmation_IsTheFigmaCopy_WithTheAffectedServer()
    {
        var server = new Server { Id = Guid.NewGuid(), Name = "prod-web-01", Host = "192.168.1.10", Port = 22 };

        var copy = DestructiveConfirmation.RemoveServer(server, new ResWLocalizationService("pt-PT"), "M0 0");

        Assert.Equal("Remover este servidor?", copy.Title);
        Assert.Equal("A configuração de prod-web-01 será removida permanentemente deste dispositivo.", copy.Body);
        Assert.Equal("prod-web-01 · 192.168.1.10:22", copy.Affected);
        Assert.Equal("O servidor remoto não é alterado. Esta ação não pode ser anulada.", copy.Note);
        Assert.Equal("Remover servidor", copy.PrimaryButtonText);
        Assert.Equal("Cancelar", copy.CloseButtonText);
        Assert.Equal("M0 0", copy.AffectedIconData);
        Assert.Equal("v6 · [::1]:2222",
            DestructiveConfirmation.RemoveServer(server with { Name = "v6", Host = "::1", Port = 2222 }, new ResWLocalizationService("pt-PT"), "").Affected);
    }

    [Fact]
    public void TheHistoryConfirmations_NameTheAffectedData()
    {
        var pt = new ResWLocalizationService("pt-PT");

        var clear = DestructiveConfirmation.ClearHistory(pt, "");
        Assert.Equal("Limpar todo o histórico?", clear.Title);
        Assert.Equal("Todos os registos de métricas guardados neste dispositivo serão eliminados.", clear.Body);
        Assert.Equal("Histórico local · Todos os servidores", clear.Affected);
        Assert.Equal("Servidores, credenciais e definições são mantidos. Esta ação não pode ser anulada.", clear.Note);
        Assert.Equal("Limpar histórico", clear.PrimaryButtonText);

        var reset = DestructiveConfirmation.ResetHistory(pt, "");
        Assert.Equal("Histórico local · base indisponível", reset.Affected);
        Assert.Equal("Repor histórico", reset.PrimaryButtonText);
        Assert.Equal("Redefinir histórico", DestructiveConfirmation.ResetHistory(new ResWLocalizationService("pt-BR"), "").PrimaryButtonText);
    }

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void EveryConfirmation_IsTranslated_InEveryCulture(string culture)
    {
        var localization = new ResWLocalizationService(culture);
        var server = new Server { Id = Guid.NewGuid(), Name = "web", Host = "web.local" };
        foreach (var copy in new[]
                 {
                     DestructiveConfirmation.RemoveServer(server, localization, ""),
                     DestructiveConfirmation.ClearHistory(localization, ""),
                     DestructiveConfirmation.ResetHistory(localization, "")
                 })
        {
            foreach (var text in new[] { copy.Title, copy.Body, copy.Affected, copy.Note, copy.PrimaryButtonText, copy.CloseButtonText })
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("Confirm", text, StringComparison.Ordinal); // never a raw resource key
            }
        }
    }

    [Fact]
    public void TheConfirmationDialog_IsTheSaDestructiveDialog_AndTheOldOneIsGone()
    {
        var root = AppSourceTree.LoadXaml("Views/DestructiveConfirmDialog.xaml").Root!;
        Assert.Equal("Destructive", (string?)root.Attribute(Primitives + "SaDialog.Kind"));
        Assert.Contains(root.Descendants(), e => e.Name.LocalName == "StaticResource" && (string?)e.Attribute("ResourceKey") == "SaDialogStyle");
        Assert.Contains(root.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource SaInsetTintedSurfaceStyle}");
        Assert.False(File.Exists(AppSourceTree.Full("Views/RemoveServerDialog.xaml")));

        // Kind=Destructive keeps the UI.4 / M14 semantics: Enter and the first focus are the safe button.
        Assert.Equal((Microsoft.UI.Xaml.Controls.ContentDialogButton.Close, "CloseButton", true), SaDialog.BehaviourFor(SaDialogKind.Destructive));
        Assert.Contains("new DestructiveConfirmDialog(", AppSourceTree.CodeWithoutComments("Services/ServerDialogService.cs"), StringComparison.Ordinal);
        Assert.Contains("new DestructiveConfirmDialog(", AppSourceTree.CodeWithoutComments("Services/HistoryMaintenanceDialogService.cs"), StringComparison.Ordinal);
    }

    // ---- Prism C1 M-6 / N-7, Cortex N-C3 ------------------------------------------------------------------------------

    [Theory]
    [InlineData(ServerConnectionState.Connected, false)]
    [InlineData(ServerConnectionState.NeverConnected, true)]
    [InlineData(ServerConnectionState.Connecting, true)]
    [InlineData(ServerConnectionState.AuthenticationFailed, true)]
    [InlineData(ServerConnectionState.Unreachable, true)]
    public async Task TheConnectionStateRow_AppearsOnlyWhenTheConnectionIsNotVerified(ServerConnectionState state, bool shown)
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");

        kit.Connections.Set(fleet.IdOf("web"), new SshConnectionResult { State = state });

        Assert.Equal(shown, detail.ShowsConnectionStateRow);
    }

    [Fact]
    public async Task AConnectionProblem_IsTheOnlyNotice_NeverTheGenericCollectionErrorToo()
    {
        var fleet = new Ui4TestKit.Fleet().Add("auth", ServerHealth.Unknown, snapshot: false);
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "auth");
        kit.States.Set(kit.States.Get(fleet.IdOf("auth")) with { LastError = MetricsCollectionErrorCode.ConnectionFailed, LastAttemptAt = Now });
        Assert.True(detail.ShowsCollectionError);

        kit.Connections.Set(fleet.IdOf("auth"), new SshConnectionResult { State = ServerConnectionState.AuthenticationFailed });

        Assert.True(detail.HasCollectionError);
        Assert.True(detail.HasConnectionProblem);
        Assert.False(detail.ShowsCollectionError);
    }

    [Fact]
    public async Task AnUndefinedAuthenticationMethod_ReadsAsNotConfigured_NeverAMissingKey()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        var index = kit.Servers.Servers.FindIndex(server => server.Name == "web");
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { AuthenticationMethod = (AuthenticationMethod)99 };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");

        Assert.Equal(new ResWLocalizationService("pt-PT").GetString("ServerDetailAuthenticationNotConfigured"), detail.AuthenticationDisplay);
        Assert.DoesNotContain("99", detail.AuthenticationDisplay, StringComparison.Ordinal);
    }

    // ---- Beacon C1 M1 / N3: accessible names ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Histórico de prod-web-01", "Histórico", "Histórico de prod-web-01")]
    [InlineData(null, "Histórico", "Histórico")]
    [InlineData("", "Histórico", "Histórico")]
    [InlineData(null, "", "fallback")]
    public void AListRow_KeepsAnExplicitName_ElseItsTitle(string? explicitName, string title, string expected) =>
        Assert.Equal(expected, SaListRowAutomationPeer.NameFor(explicitName, title, () => "fallback"));

    [Fact]
    public void TheExploreRows_BindTheirServerNames_AndTheRowNoLongerOverwritesThem()
    {
        var rows = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml").Descendants().Where(e => e.Name.LocalName == "SaListRow").ToList();
        Assert.Equal(new[] { "{Binding ViewHistoryAutomationName}", "{Binding ViewWorkloadsAutomationName}" },
            rows.Select(row => (string?)row.Attribute("AutomationProperties.Name")));
        Assert.DoesNotContain("AutomationProperties.SetName", AppSourceTree.CodeWithoutComments("Controls/Primitives/SaListRow.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Não foi possível atualizar as métricas.", "", "Não foi possível atualizar as métricas.")]
    [InlineData("Não foi possível atualizar as métricas.", null, "Não foi possível atualizar as métricas.")]
    [InlineData("Falha de autenticação", "Revê as credenciais em Editar.", "Falha de autenticação. Revê as credenciais em Editar.")]
    [InlineData("Falha de autenticação.", "Revê as credenciais.", "Falha de autenticação. Revê as credenciais.")]
    [InlineData(null, "Só a mensagem.", "Só a mensagem.")]
    [InlineData("  ", "  ", "")]
    public void AnInlineNotice_IsNamedWithoutADoubledStop(string? title, string? message, string expected) =>
        Assert.Equal(expected, SaInlineNotice.AccessibleName(title, message));

    // ---- Beacon C1 M2: the hidden-servers list ---------------------------------------------------------------------------

    [Fact]
    public async Task EveryHiddenServer_SaysItsPositionInTheList()
    {
        var settings = await SettingsWithHiddenAsync(new FakeTimeProvider(Now), count: 3);

        Assert.Equal(new[] { 1, 2, 3 }, settings.ViewModel.HiddenServers.Select(item => item.PositionInSet));
        Assert.All(settings.ViewModel.HiddenServers, item => Assert.Equal(3, item.SizeOfSet));
        Assert.Equal("Servidores ocultos", settings.ViewModel.HiddenServersListName);

        var list = AppSourceTree.LoadXaml("Views/SettingsDataPage.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "HiddenServersList");
        Assert.Equal("Local", (string?)list.Attribute("TabFocusNavigation")); // every Restaurar is a Tab stop
        var restore = list.Descendants().Single(e => e.Name.LocalName == "Button");
        Assert.Equal("{Binding PositionInSet}", (string?)restore.Attribute("AutomationProperties.PositionInSet"));
        Assert.Equal("{Binding SizeOfSet}", (string?)restore.Attribute("AutomationProperties.SizeOfSet"));
        var icon = list.Descendants().Single(e => e.Name.LocalName == "SaIcon");
        Assert.Equal("{ThemeResource SaTextSecondaryBrush}", (string?)icon.Attribute("Foreground")); // Prism C1 M-7
    }

    // ---- Cortex C1 N-C1: the log carries the exception TYPE only ---------------------------------------------------------

    [Fact]
    public async Task ALoadFailure_IsLoggedByType_NeverWithItsMessage()
    {
        var logger = new RecordingLogger();
        var viewModel = new SettingsViewModel(
            new Ui5SettingsTests.RecordingTheme(AppThemePreference.System),
            new FakeLocalizationService(),
            new FakeNavigationService(),
            new FakeServerService(),
            new Ui5SettingsTests.RecordingDiscovery(),
            new SilentNotifications(),
            new FakeBackgroundMonitoringSettingsService(enabled: true),
            new BackgroundDegradationNotice(),
            new ThrowingMaintenance(),
            new AppVersionProvider(),
            logger,
            new PresentationClock(new FakeTimeProvider(Now)));

        await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", entry.Message, StringComparison.Ordinal);
    }

    // ---- layout and keyboard contracts of the three pages (Prism C1 M-1/M-4/M-5/M-6/M-8, N-1/N-2/N-4/N-5, Beacon) ----

    [Fact]
    public void TheMeters_FillTheCard_AndThreeColumnsStartAt1120()
    {
        var page = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml");
        var meters = page.Descendants().Where(e => e.Name.LocalName is "SaPulseBars" or "SaSegmentMeter").ToList();

        Assert.Equal(3, meters.Count);
        // UI.10 F04 (H02, Figma 112:1818/1855/1890 Fill): no fixed 288 track - each meter stretches over the card's interior
        // (the same samples / segment counts, only wider).
        Assert.All(meters, meter =>
        {
            Assert.Null(meter.Attribute("Width"));
            Assert.Null(meter.Attribute("MaxWidth"));
            Assert.Equal("Stretch", (string?)meter.Attribute("HorizontalAlignment"));
        });
        Assert.Equal("{Binding CpuPulseCeiling}", (string?)meters.Single(e => e.Name.LocalName == "SaPulseBars").Attribute("Ceiling"));
        var wide = page.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "Wide");
        Assert.Equal("1120", (string?)wide.Descendants().Single(e => e.Name.LocalName == "SaContentWidthTrigger").Attribute("MinWidth"));
    }

    [Fact]
    public void TheFirstReading_AndTheInfoStrip_HaveTheFigmaHeights()
    {
        var page = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml");
        var firstReading = page.Descendants().Where(e => e.Name.LocalName == "Border"
            && ((string?)e.Attribute("Visibility") ?? string.Empty).StartsWith("{Binding IsFirstReading,", StringComparison.Ordinal)
            && !((string?)e.Attribute("Visibility"))!.Contains("Invert", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, firstReading.Count); // the collecting card and the info strip card
        Assert.All(firstReading, card => Assert.Equal("{StaticResource SaSolidSurfaceStyle}", (string?)card.Attribute("Style")));
        Assert.Equal("216", (string?)firstReading[0].Attribute("Height"));
        Assert.Equal("86", (string?)firstReading[1].Attribute("MinHeight"));
        var strip = page.Descendants().Single(e => e.Name.LocalName == "Grid" && (string?)e.Attribute("Margin") == "4,0");
        Assert.Equal("86", (string?)strip.Attribute("MinHeight"));
        var stateRow = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == "ServerDetailConnectionStateRow");
        Assert.StartsWith("{Binding ShowsConnectionStateRow,", (string?)stateRow.Attribute("Visibility"), StringComparison.Ordinal);
        var refresh = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "RefreshButton");
        Assert.Equal("134", (string?)refresh.Attribute("MinWidth"));
        var actions = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "IdentityActions");
        Assert.Equal("{StaticResource SaSpace16}", (string?)actions.Attribute("Spacing"));
    }

    [Fact]
    public void TheInlineKeyValueRow_Is23High()
    {
        var inline = AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants()
            .Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "Inline");

        Assert.Contains(inline.Descendants(), e => (string?)e.Attribute("Target") == "RootGrid.MinHeight" && (string?)e.Attribute("Value") == "23");
    }

    [Fact]
    public void TheRefreshingText_IsTheLiveRegion_AnnouncedWhenARefreshStarts()
    {
        var text = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "RefreshingText");

        Assert.Equal("Polite", (string?)text.Attribute("AutomationProperties.LiveSetting"));
        Assert.Contains("RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", AppSourceTree.CodeWithoutComments("Views/ServerDetailPage.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsRows_AreHeadings_TheThemeGroupIsOneTabStop_AndNarrowSpacesTheRows()
    {
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var rowTitles = new[] { "SettingsThemeTitle", "SettingsLanguageTitle", "SettingsBackgroundTitle", "SettingsNotificationsTitle", "SettingsCompactTitle", "SettingsAlwaysOnTopTitle" };

        Assert.All(rowTitles, uid => Assert.Equal("Level3",
            (string?)page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == uid).Attribute("AutomationProperties.HeadingLevel")));
        var group = page.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == "SettingsThemeGroup");
        Assert.Equal("SelectionFollowsFocus", (string?)group.Attribute(Primitives + "SaGroupNavigation.Mode"));
        var narrow = page.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "Narrow");
        foreach (var row in new[] { "ThemeRow", "BackgroundSection", "CompactRow" })
        {
            Assert.Contains(narrow.Descendants(), e => (string?)e.Attribute("Target") == row + ".Margin" && (string?)e.Attribute("Value") == "0,0,0,16");
        }

        var icons = page.Descendants().Where(e => e.Name.LocalName == "SaIcon"
            && e.Ancestors().Any(a => a.Name.LocalName == "Button")).ToList();
        Assert.Equal(5, icons.Count); // Entrar chevron + 2 disclosures × (icon, chevron) - Prism C1 N-5
        Assert.All(icons, icon => Assert.Equal("{ThemeResource SaTextSecondaryBrush}", (string?)icon.Attribute("Foreground")));
    }

    [Fact]
    public void TheThemeSelector_UsesItsOwnMeasuredBrushes()
    {
        var forms = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml").Root!.Elements().ToList();
        var track = forms.Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaSegmentedThemeTrackStyle");
        var item = forms.Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaSegmentedThemeItemStyle");

        Assert.Contains(track.Elements(), e => (string?)e.Attribute("Property") == "Background" && (string?)e.Attribute("Value") == "{ThemeResource SaSegmentedThemeTrackBrush}");
        Assert.Contains(item.Descendants(), e => (string?)e.Attribute("Target") == "Shell.Background" && (string?)e.Attribute("Value") == "{ThemeResource SaSegmentedThemeSelectedBrush}");
    }

    [Fact]
    public void TheDataBackButton_IsCentredInItsHeader()
    {
        var back = AppSourceTree.LoadXaml("Views/SettingsDataPage.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == "SettingsDataBackButton");

        Assert.Equal("Center", (string?)back.Attribute("VerticalAlignment"));
        Assert.Null(back.Attribute("Margin"));
    }

    [Theory]
    [InlineData("Views/SettingsDataPage.xaml.cs", "AboutSection.StartBringIntoView()", "GitHubButton.Focus(")]
    [InlineData("Views/SettingsPage.xaml.cs", "BackgroundSection.StartBringIntoView()", "BackgroundControl.Focus(")]
    public void ASectionRequest_BringsTheSectionIntoView_AndFocusesIt(string file, string bringIntoView, string focus)
    {
        var code = AppSourceTree.CodeWithoutComments(file);
        Assert.Contains(bringIntoView, code, StringComparison.Ordinal);
        Assert.True(code.IndexOf(focus, StringComparison.Ordinal) > code.IndexOf(bringIntoView, StringComparison.Ordinal), $"{file}: {focus} after {bringIntoView}");
    }

    [Theory]
    [InlineData("Views/ServerDetailPage.xaml")]
    [InlineData("Views/SettingsPage.xaml")]
    [InlineData("Views/SettingsDataPage.xaml")]
    public void TheGlassOfTheOpenPage_FollowsALiveThemeSwitch(string file)
    {
        Assert.Null(AppSourceTree.LoadXaml(file).Root!.Attribute(Primitives + "SaThemeRefresh.IsEnabled"));
        var shell = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("SaThemeRefresh.Remount(page)", shell);
        Assert.Contains("SaThemeRefresh.Remount(Sidebar, Sidebar.Content", shell);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private static async Task<(Ui4TestKit.Harness Kit, ServersReturnNotice Notice)> HiddenFromTheDetailAsync()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        kit.Servers.HideOverride = id =>
        {
            var index = kit.Servers.Servers.FindIndex(server => server.Id == id);
            kit.Servers.Servers[index] = kit.Servers.Servers[index] with { IsHidden = true };
            kit.Servers.RaiseChanged();
            return Task.FromResult(true);
        };
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();
        using var detail = Open(kit, "web", notice);
        await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        return (kit, notice);
    }

    private static async Task<SettingsHarness> SettingsWithHiddenAsync(FakeTimeProvider clock, int count = 1)
    {
        var servers = new FakeServerService();
        for (var index = 0; index < count; index++)
        {
            servers.Servers.Add(new Server { Id = Guid.NewGuid(), Name = $"old-{index}", Host = $"old-{index}.local", IsHidden = true, CreatedAt = Now.AddMinutes(index) });
        }

        servers.RestoreOverride = _ => Task.FromResult(true);
        var viewModel = new SettingsViewModel(
            new Ui5SettingsTests.RecordingTheme(AppThemePreference.System),
            new ResWLocalizationService("pt-PT"),
            new FakeNavigationService(),
            servers,
            new Ui5SettingsTests.RecordingDiscovery(),
            new SilentNotifications(),
            new FakeBackgroundMonitoringSettingsService(enabled: true),
            new BackgroundDegradationNotice(),
            new NullHistoryMaintenanceService(),
            new AppVersionProvider(),
            NullLogger<SettingsViewModel>.Instance,
            new PresentationClock(clock));
        await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return new SettingsHarness(viewModel, servers);
    }

    private static ServerDetailViewModel Open(Ui4TestKit.Harness kit, string name, ServersReturnNotice? notice = null)
    {
        var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization, history: null, returnFocus: null,
            monitoringOptions: null, clock: new PresentationClock(new FakeTimeProvider(Now)), serversNotice: notice);
        detail.Load(kit.Dashboard.VisibleServers.Single(card => card.Name == name).Server.Id, ServerDetailOrigin.Overview);
        return detail;
    }

    private static List<string> Recorder(INotifyPropertyChanged source)
    {
        var raised = new List<string>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        return raised;
    }

    private sealed class DecidingDialogs : IEditorScript
    {
        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() => Task.FromResult<ServerEditorResult?>(null);

        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(false);
    }

    private sealed record SettingsHarness(SettingsViewModel ViewModel, FakeServerService Servers);

    private sealed class SilentNotifications : INotificationSettingsService
    {
        public event EventHandler? NotificationsEnabledChanged { add { } remove { } }

        public bool NotificationsEnabled => true;

        public void SetNotificationsEnabled(bool enabled)
        {
        }
    }

    private sealed class ThrowingMaintenance : IHistoryMaintenanceService
    {
        public bool IsAvailable => throw new InvalidOperationException("secret-host unreachable at /home/user/.ssh");

        public Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync() => throw new NotSupportedException();

        public Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync() => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<SettingsViewModel>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
