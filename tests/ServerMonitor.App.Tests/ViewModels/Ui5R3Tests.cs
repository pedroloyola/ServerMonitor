using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.5 fix round 3 (Beacon / Atlas / Cortex C2): focus back to the history button that opened a destructive dialog,
/// no system clock behind any notice or toast in tests, the queued-callback race of the notice countdown, every error
/// producer's scope, and the UIA contracts of the hidden-servers list and the affected-data line. Synthetic; no wall clock.
/// </summary>
public sealed class Ui5R3Tests
{
    private static readonly DateTimeOffset Now = Ui4TestKit.Now;

    // ---- Beacon C2 R2-M1: focus returns to the invoking history button --------------------------------------------------

    public static TheoryData<HistoryAction, string> HistoryEndings => new()
    {
        { HistoryAction.Clear, "cancelled" }, { HistoryAction.Clear, "done" }, { HistoryAction.Clear, "unavailable" }, { HistoryAction.Clear, "throws" },
        { HistoryAction.Reset, "cancelled" }, { HistoryAction.Reset, "done" }, { HistoryAction.Reset, "unavailable" }, { HistoryAction.Reset, "throws" }
    };

    [Theory]
    [MemberData(nameof(HistoryEndings))]
    public async Task EveryEndOfAHistoryDialog_AsksForFocusOnItsButton_Once(HistoryAction action, string ending)
    {
        var maintenance = new OutcomeMaintenance(ending);
        var viewModel = Settings(maintenance);
        var requests = new List<HistoryAction>();
        viewModel.HistoryFocusRequested += (_, requested) => requests.Add(requested);

        var command = (AsyncRelayCommand)(action == HistoryAction.Clear ? viewModel.ClearHistoryCommand : viewModel.ResetHistoryCommand);
        await command.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { action }, requests);
        Assert.True(command.CanExecute(null)); // re-enabled when the page acts on the request (Low priority, afterwards)
    }

    [Fact]
    public void TheDataPage_RefocusesTheHistoryButton_ReplacingTheDialogsLostFocus()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/SettingsDataPage.xaml.cs");

        Assert.Contains("ViewModel.HistoryFocusRequested += OnHistoryFocusRequested;", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.HistoryFocusRequested -= OnHistoryFocusRequested;", code, StringComparison.Ordinal);
        Assert.Contains("FocusTargetFor(action, ResetHistoryButton, ClearHistoryButton).Focus(", code, StringComparison.Ordinal);
    }

    // ---- Atlas C2 finding 1: no system clock behind a notice or toast in any test ------------------------------------------

    /// <summary>
    /// Guard: every test construction of the two notice owners passes an injected clock (a FakeTimeProvider-based
    /// PresentationClock): SettingsViewModel always (any success can publish a toast), ServersViewModel whenever it is given
    /// a return notice. The countdowns are proven to run on the fake by TimerRecordingTimeProvider in the B2 tests.
    /// </summary>
    [Fact]
    public void NoTest_BuildsANoticeOwner_OnTheSystemClock()
    {
        var root = Path.Combine(AppSourceTree.RepositoryRoot, "tests", "ServerMonitor.App.Tests");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"new (SettingsViewModel|ServersViewModel)\("))
            {
                var arguments = Arguments(text, match.Index + match.Length);
                var needsClock = match.Groups[1].Value == "SettingsViewModel" || arguments.Count(c => c == ',') >= 3; // + notice
                if (needsClock && !arguments.Contains("PresentationClock", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{text[..match.Index].Count(c => c == '\n') + 1} {match.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // ---- Atlas C2 finding 2: a callback already queued for the UI never closes a newer notice ------------------------------

    [Fact]
    public void AQueuedCallback_OfASupersededNotice_IsANoOp_TheNewOneKeepsItsTime()
    {
        var clock = new FakeTimeProvider(Now);
        var queue = new Queue<Action>();
        using var timer = new TransientNoticeTimer(clock) { EnqueueOverride = action => { queue.Enqueue(action); return true; } };
        var closed = new List<string>();

        timer.Start(() => closed.Add("A"));
        clock.Advance(TransientNoticeTimer.Duration); // A expires: its callback is QUEUED for the UI, not run
        Assert.Single(queue);
        timer.Start(() => closed.Add("B"));            // a new notice before the UI drained A's callback
        Drain(queue);                                  // A's callback runs late

        Assert.Empty(closed);                          // it closed nothing: B is still open
        Assert.True(timer.IsRunning);
        clock.Advance(TransientNoticeTimer.Duration - TimeSpan.FromMilliseconds(1));
        Drain(queue);
        Assert.Empty(closed);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Drain(queue);
        Assert.Equal(new[] { "B" }, closed);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public void AQueuedCallback_AfterCancelOrDispose_IsANoOp(string how)
    {
        var clock = new FakeTimeProvider(Now);
        var queue = new Queue<Action>();
        var timer = new TransientNoticeTimer(clock) { EnqueueOverride = action => { queue.Enqueue(action); return true; } };
        var closed = 0;

        timer.Start(() => closed++);
        clock.Advance(TransientNoticeTimer.Duration);
        if (how == "cancel")
        {
            timer.Cancel();
        }
        else
        {
            timer.Dispose();
        }

        Drain(queue);
        Assert.Equal(0, closed);
    }

    [Fact]
    public void TheDispatcher_IsCapturedAtStart_NotAtConstruction()
    {
        var code = AppSourceTree.CodeWithoutComments("ViewModels/TransientNoticeTimer.cs");

        Assert.DoesNotContain("readonly DispatcherQueue", code, StringComparison.Ordinal);
        Assert.True(code.IndexOf("TryGetDispatcher() is { } dispatcher", StringComparison.Ordinal)
            > code.IndexOf("public void Start(", StringComparison.Ordinal), "the dispatcher must be captured inside Start");
    }

    // ---- Atlas C2 finding 3: every producer records its server; locked configuration stays global ---------------------------

    public static TheoryData<string, string> Producers => new()
    {
        { "edit", "result" }, { "edit", "throws" },
        { "hide", "result" }, { "hide", "throws" },
        { "remove", "result" }, { "remove", "throws" }
    };

    [Theory]
    [MemberData(nameof(Producers))]
    public async Task EveryFailedServerOperation_IsScopedToItsServer(string operation, string failure)
    {
        var (kit, web, nas, webId) = await TwoServersAsync(failure);

        await Run(web, operation);

        Assert.True(kit.Dashboard.IsOperationErrorOpen, $"{operation}/{failure}: no error reported");
        Assert.Equal(webId, kit.Dashboard.OperationErrorServerId);
        Assert.True(web.IsOperationErrorOpen);
        Assert.False(nas.IsOperationErrorOpen);
        web.Dispose();
        nas.Dispose();
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("hide")]
    [InlineData("remove")]
    public async Task ALockedConfiguration_IsGlobal_NeverAServerScopedError(string operation)
    {
        var (kit, web, nas, _) = await TwoServersAsync("locked");

        await Run(web, operation);

        Assert.True(kit.Dashboard.IsConfigurationLockedOpen);
        Assert.False(kit.Dashboard.IsOperationErrorOpen);
        Assert.True(web.IsConfigurationLockedOpen);
        Assert.True(nas.IsConfigurationLockedOpen);
        web.Dispose();
        nas.Dispose();
    }

    // ---- Beacon C2 R2-N1 / R2-N2, Cortex C2 R-1 / R-2 -----------------------------------------------------------------------

    [Fact]
    public void TheHiddenServersList_IsANamedListForUiAutomation()
    {
        var page = AppSourceTree.LoadXaml("Views/SettingsDataPage.xaml");
        var host = page.Descendants().Single(e => e.Name.LocalName == "SaListHost");

        Assert.Equal("{Binding HiddenServersListName}", (string?)host.Attribute("AutomationProperties.Name"));
        var repeater = Assert.Single(host.Elements(), e => e.Name.LocalName == "ItemsRepeater");
        Assert.Equal("Local", (string?)repeater.Attribute("TabFocusNavigation"));
        Assert.Null(repeater.Attribute("AutomationProperties.Name")); // a peer-less repeater exposes no name
        var peer = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaListHost.cs");
        Assert.Contains("AutomationControlType.List", peer, StringComparison.Ordinal);
        Assert.Contains("IsTabStop = false", peer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAffectedDataLine_IsReadOnce()
    {
        var row = AppSourceTree.LoadXaml("Views/DestructiveConfirmDialog.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "AffectedRow");

        Assert.Null(row.Attribute("AutomationProperties.Name"));
        Assert.Single(row.Descendants(), e => e.Name.LocalName == "TextBlock");
    }

    [Fact]
    public void TheThemeRemount_KeepsTheScrollOffset()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaThemeRefresh.cs");

        Assert.True(code.IndexOf("var offset = scroller?.VerticalOffset", StringComparison.Ordinal)
            < code.IndexOf("page.Content = null;", StringComparison.Ordinal), "the offset is read before the remount");
        Assert.Contains("scroller?.ChangeView(null, offset, null, disableAnimation: true)", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheErrorScope_IsLastWins_Documented()
    {
        var (kit, web, nas, _) = await TwoServersAsync("result");
        await Run(web, "hide");
        await Run(nas, "hide"); // a second error, about nas, while the first is still open

        Assert.False(web.IsOperationErrorOpen);
        Assert.True(nas.IsOperationErrorOpen);
        Assert.True(kit.Dashboard.IsOperationErrorOpen); // the Visão geral (unscoped) still shows it
        Assert.Contains("last-wins", File.ReadAllText(AppSourceTree.Full("ViewModels/DashboardViewModel.cs")), StringComparison.Ordinal);
        web.Dispose();
        nas.Dispose();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static string Arguments(string text, int start)
    {
        var depth = 1;
        var index = start;
        while (index < text.Length && depth > 0)
        {
            depth += text[index] switch { '(' => 1, ')' => -1, _ => 0 };
            index++;
        }

        return text[start..Math.Max(start, index - 1)];
    }

    private static void Drain(Queue<Action> queue)
    {
        while (queue.Count > 0)
        {
            queue.Dequeue()();
        }
    }

    private static SettingsViewModel Settings(IHistoryMaintenanceService maintenance) => new(
        new Ui5SettingsTests.RecordingTheme(AppThemePreference.System),
        new FakeLocalizationService(),
        new FakeNavigationService(),
        new FakeServerService(),
        new Ui5SettingsTests.RecordingDiscovery(),
        new SilentNotifications(),
        new FakeBackgroundMonitoringSettingsService(enabled: true),
        new BackgroundDegradationNotice(),
        maintenance,
        new AppVersionProvider(),
        NullLogger<SettingsViewModel>.Instance,
        new PresentationClock(new FakeTimeProvider(Now)));

    private static async Task<(Ui4TestKit.Harness Kit, ServerDetailViewModel Web, ServerDetailViewModel Nas, Guid WebId)> TwoServersAsync(string failure)
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("nas", ServerHealth.Healthy, 1, 2, 3);
        var producers = new FailingProducers(failure);
        var kit = Ui4TestKit.Create(fleet, dialogs: producers, profiles: producers);
        kit.Servers.HideOverride = _ => producers.Fail<bool>(false);
        await kit.Dashboard.LoadAsync();
        return (kit, Open(kit, "web"), Open(kit, "nas"), fleet.IdOf("web"));
    }

    private static async Task Run(ServerDetailViewModel detail, string operation)
    {
        switch (operation)
        {
            case "edit":
                detail.EditCommand.Execute(null); // completes synchronously over the synchronous doubles
                break;
            case "hide":
                await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
                break;
            default:
                await ((AsyncRelayCommand)detail.RemoveCommand).ExecuteAsync();
                break;
        }
    }

    private static ServerDetailViewModel Open(Ui4TestKit.Harness kit, string name)
    {
        var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization, history: null, returnFocus: null,
            monitoringOptions: null, clock: new PresentationClock(new FakeTimeProvider(Now)));
        detail.Load(kit.Dashboard.VisibleServers.Single(card => card.Name == name).Server.Id, ServerDetailOrigin.Overview);
        return detail;
    }

    /// <summary>The Editar / Remover producers fail as asked: a negative result, a synthetic exception, or a locked configuration.</summary>
    private sealed class FailingProducers(string failure) : IServerDialogService, Core.Interfaces.IServerProfileService
    {
        public Task<T> Fail<T>(T negative) => failure switch
        {
            "throws" => throw new InvalidOperationException("synthetic"),
            "locked" => throw new ConfigurationLockedException(),
            _ => Task.FromResult(negative)
        };

        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(new ServerEditorResult
        {
            Profile = new ServerProfileInput { Configuration = new ServerInput { Name = server?.Name ?? "web" }, CredentialChange = CredentialChange.Keep }
        });

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() => Task.FromResult<ServerEditorResult?>(null);

        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(true);

        public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerOperationResult> UpdateAsync(Server current, ServerProfileInput input, CancellationToken cancellationToken = default) =>
            Fail(ServerOperationResult.Failure());

        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default) => Fail(false);
    }

    private sealed class OutcomeMaintenance(string ending) : IHistoryMaintenanceService
    {
        public bool IsAvailable => true;

        public Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync() => ending switch
        {
            "throws" => throw new InvalidOperationException("synthetic"),
            "cancelled" => Task.FromResult(HistoryClearOutcome.Cancelled),
            "done" => Task.FromResult(HistoryClearOutcome.Cleared),
            _ => Task.FromResult(HistoryClearOutcome.Unavailable)
        };

        public Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync() => ending switch
        {
            "throws" => throw new InvalidOperationException("synthetic"),
            "cancelled" => Task.FromResult(HistoryResetOutcome.Cancelled),
            "done" => Task.FromResult(HistoryResetOutcome.Reset),
            _ => Task.FromResult(HistoryResetOutcome.Unavailable)
        };
    }

    private sealed class SilentNotifications : INotificationSettingsService
    {
        public event EventHandler? NotificationsEnabledChanged { add { } remove { } }

        public bool NotificationsEnabled => true;

        public void SetNotificationsEnabled(bool enabled)
        {
        }
    }
}
