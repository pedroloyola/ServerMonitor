using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.UI.Xaml.Automation.Peers;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.ViewModels;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 B12B-02-R (real Narrator, human: after Ocultar on the Detail only the Servidores heading was heard). The return
/// notice arrived as a polite live region in the same turn as the page-entry focus change, which Narrator speaks instead.
/// Now the toast does not announce itself (<c>AnnouncesItself="False"</c>, this toast only) and the page raises its text
/// once as a UIA notification in the turn AFTER its focus decision (Cortex research; Boss direction), never after the
/// page left (Atlas L1). The scheduling is <see cref="ReturnNoticeAnnouncer"/> (no WinUI types, so its queue is driven
/// here); the page wiring and the event are pinned by source (App.Tests has no XAML runtime); "only on success" is the
/// view model's. What Narrator says stays runtime QA with the human.
/// </summary>
public sealed partial class Ui12ReturnNoticeNotificationTests
{
    private const string Spoken = "Servidor ocultado. Podes mostrar web de novo nas Definições.";

    // ---- the pure rule ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, Spoken, true)]
    [InlineData(true, true, Spoken, false)]  // once per page
    [InlineData(false, false, Spoken, false)] // no notice / closed
    [InlineData(false, true, "", false)]
    [InlineData(false, true, "   ", false)]
    [InlineData(false, true, null, false)]
    public void TheNotice_IsAnnouncedOnce_OnlyWhileOpen_AndOnlyWithText(bool announced, bool open, string? content, bool expected) =>
        Assert.Equal(expected, ReturnNoticeAnnouncer.ShouldAnnounce(announced, open, content));

    [Fact]
    public void TheNotification_IsAFixedSource_ThatDoesNotCutOffTheHeading()
    {
        Assert.Equal("ServersNoticeShown", ServersPage.ReturnNoticeActivityId);
        Assert.Equal(AutomationNotificationProcessing.CurrentThenMostRecent, ServersPage.ReturnNoticeProcessing);

        // Never localized: the activity id is a code constant, not a resource key in any culture.
        foreach (var culture in new[] { "pt-PT", "pt-BR", "en-US" })
        {
            var resw = File.ReadAllText(AppSourceTree.Full($"Resources/{culture}/Resources.resw"));
            Assert.DoesNotContain("ServersNoticeShown", resw, StringComparison.Ordinal);
        }
    }

    // ---- the announcer: a later turn, once per page, never after the page left (Atlas A02-01, L1) ----------------------

    [Fact]
    public void AfterFocus_QueuesTheRaise_ForALaterTurn_ThenSaysItOnce()
    {
        var h = new AnnouncerHarness();

        h.Announcer.AfterFocus();
        Assert.Single(h.Queue);   // queued, not raised in the focus turn
        Assert.Empty(h.Raised);

        h.Drain();
        Assert.Equal(new[] { Spoken }, h.Raised);

        h.Announcer.AfterFocus(); // a second Loaded of the same page (remount) ...
        h.Drain();
        Assert.Equal(new[] { Spoken }, h.Raised); // ... never repeats it
    }

    [Fact]
    public void TwoCallbacksQueuedBeforeEitherRan_StillSayItOnce()
    {
        var h = new AnnouncerHarness();

        h.Announcer.AfterFocus();
        h.Announcer.AfterFocus();
        h.Drain();

        Assert.Equal(new[] { Spoken }, h.Raised);
    }

    [Fact]
    public void OncePerPage_NotPerProcess_TwoPagesEachSayTheirOwn()
    {
        var first = new AnnouncerHarness();
        var second = new AnnouncerHarness();

        first.Announcer.AfterFocus();
        first.Drain();
        second.Announcer.AfterFocus();
        second.Drain();

        Assert.Equal(new[] { Spoken }, first.Raised);
        Assert.Equal(new[] { Spoken }, second.Raised);
    }

    [Fact]
    public void FocusThenEnqueueThenThePageCloses_ThenDrain_SaysNothing()
    {
        var h = new AnnouncerHarness();

        h.Announcer.AfterFocus();
        h.Announcer.Close();      // Unloaded / navigation replaced the page while the raise was queued
        h.Drain();

        Assert.Empty(h.Raised);
        h.Announcer.AfterFocus(); // and a closed page queues nothing more
        Assert.Empty(h.Queue);
    }

    [Theory]
    [InlineData(false, true, "notice closed (by the user or its timer) before the turn ran")]
    [InlineData(true, false, "notice no longer shown on the live tree")]
    public void AQueuedRaise_IsDropped_WhenTheNoticeIsGoneByThen(bool openAtDrain, bool liveAtDrain, string because)
    {
        var h = new AnnouncerHarness();

        h.Announcer.AfterFocus();
        h.Open = openAtDrain;
        h.Live = liveAtDrain;
        h.Drain();

        Assert.True(h.Raised.Count == 0, because);
    }

    [Fact]
    public void WithoutANotice_NothingIsEvenQueued()
    {
        var h = new AnnouncerHarness { Open = false };

        h.Announcer.AfterFocus();

        Assert.Empty(h.Queue);
        Assert.Empty(h.Raised);
    }

    [Fact]
    public async Task TheRealVisit_FocusThenEnqueueThenDispose_ThenDrain_SaysNothing()
    {
        var (kit, notice) = await HiddenWebAsync("pt-PT");
        var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, Clock(), notice);
        var h = new AnnouncerHarness(() => servers.IsNoticeOpen);

        h.Announcer.AfterFocus();
        Assert.Single(h.Queue);
        servers.Dispose();        // the visit ended (the page's Dispose also closes the announcer; here only the VM)
        h.Drain();

        Assert.False(servers.IsNoticeOpen); // Atlas L1: a disposed visit holds no notice
        Assert.Empty(h.Raised);
    }

    [Fact]
    public async Task TheRealVisit_StillAlive_SaysItOnce()
    {
        var (kit, notice) = await HiddenWebAsync("pt-PT");
        using var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, Clock(), notice);
        var h = new AnnouncerHarness(() => servers.IsNoticeOpen);

        h.Announcer.AfterFocus();
        h.Drain();

        Assert.Equal(new[] { Spoken }, h.Raised);
    }

    [Fact]
    public void TheAnnouncedState_IsPerInstance_WrittenOnlyToSayIt()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/ReturnNoticeAnnouncer.cs");

        Assert.Contains("    private bool _announced;", code, StringComparison.Ordinal);
        Assert.Contains("    private bool _closed;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("static bool _", code, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(code, @"_announced\s*="));
        Assert.Single(Regex.Matches(code, @"_closed\s*="));
        Assert.Matches(@"_announced = true;\s*raise\(text!\);", code);
        Assert.Matches(@"public void Close\(\) => _closed = true;", code);
        Assert.Matches(
            @"internal static bool ShouldAnnounce\(bool announced, bool noticeOpen, string\? content\) =>\s*!announced && noticeOpen && !string\.IsNullOrWhiteSpace\(content\);",
            code);
    }

    // ---- the page: wiring, order and the one raise --------------------------------------------------------------------

    [Fact]
    public void ThePage_AnnouncesAfterItsFocusDecision_AlsoWhenTheSidebarKeepsFocus()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServersPage.xaml.cs");

        Assert.Matches(SidebarPathAnnounces(), page);
        Assert.Matches(FocusThenAnnounce(), page);
        Assert.Equal(2, Regex.Matches(page, Regex.Escape("_returnNotice.AfterFocus();")).Count);
    }

    [Fact]
    public void ThePage_WiresALowTurn_TheNoticeAndItsLiveTree_AndClosesTheAnnouncerWhenItLeaves()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServersPage.xaml.cs");

        Assert.Matches(AnnouncerWiring(), page);
        Assert.Matches(RaiseBody(), page);
        Assert.Matches(@"public void Dispose\(\)\s*\{\s*_returnNotice\.Close\(\);\s*ViewModel\.Dispose\(\);\s*\}", page);
        Assert.Single(Regex.Matches(page, Regex.Escape("RaiseNotificationEvent(")));
        Assert.Single(Regex.Matches(page, Regex.Escape("new ReturnNoticeAnnouncer(")));
        Assert.DoesNotContain("RaiseAutomationEvent", page, StringComparison.Ordinal);
        Assert.Matches(@"_disposed = true;\s*_noticeTimer\.Dispose\(\);\s*_notice = null;", AppSourceTree.CodeWithoutComments("ViewModels/ServersViewModel.cs"));
    }

    // ---- the toast: opt-in silence, default unchanged ---------------------------------------------------------------

    [Fact]
    public void TheToast_RaisesItsLiveRegionOnlyWhenItAnnouncesItself_DefaultTrue()
    {
        var toast = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaToast.cs");

        Assert.Matches(@"nameof\(AnnouncesItself\), typeof\(bool\), typeof\(SaToast\), new PropertyMetadata\(true, ", toast);
        // Atlas A02-02: the property IS the dependency property (a constant getter would ignore AnnouncesItself="False").
        Assert.Matches(
            @"public bool AnnouncesItself\s*\{\s*get => \(bool\)GetValue\(AnnouncesItselfProperty\);\s*set => SetValue\(AnnouncesItselfProperty, value\);\s*\}",
            toast);
        Assert.Matches(@"private void AnnounceLive\(\)\s*\{\s*if \(AnnouncesItself\)\s*\{\s*SaAnnounce\.RaiseAfterLayout\(this, _announcement\.Announced\);\s*\}\s*\}", toast);
        Assert.Single(Regex.Matches(toast, Regex.Escape("SaAnnounce.")));
        Assert.Contains(
            "AutomationProperties.SetLiveSetting(this, AnnouncesItself ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);",
            toast, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseNotificationEvent", toast, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheServersReturnNotice_OptsOut_EveryOtherToastIsUnchanged()
    {
        var toasts = AppSourceTree.Files(".xaml")
            .SelectMany(f => AppSourceTree.LoadXaml(f).Descendants().Where(e => e.Name.LocalName == "SaToast").Select(e => (File: f, Toast: e)))
            .ToList();

        var optedOut = Assert.Single(toasts, t => t.Toast.Attribute("AnnouncesItself") is not null);
        Assert.Equal("Views/ServersPage.xaml", optedOut.File);
        Assert.Equal("ReturnNotice", (string?)optedOut.Toast.Attribute(AppSourceTree.Xaml + "Name"));
        Assert.Equal("False", (string?)optedOut.Toast.Attribute("AnnouncesItself"));
        Assert.Null(optedOut.Toast.Attribute("AutomationProperties.LiveSetting"));
        Assert.True(toasts.Count > 1, "the other toasts (Detail, Data) must still exist and keep the default");
    }

    // ---- only on success (the view model posts the notice only then) ---------------------------------------------------

    [Theory]
    [InlineData("pt-PT", "Servidor ocultado", "Podes mostrar web de novo nas Definições.")]
    [InlineData("pt-BR", "Servidor ocultado", "Você pode exibir web novamente nas Configurações.")]
    [InlineData("en-US", "Server hidden", "You can show web again in Settings.")]
    public async Task ASuccessfulHide_OpensTheNotice_WithTheNameOnly_AFailedHide_NeverDoes(string culture, string title, string message)
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("web", ServerHealth.Healthy, 1, 2, 3, host: "10.9.8.7")
            .Add("db", ServerHealth.Healthy, 1, 2, 3, host: "10.9.8.6");
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService(culture));
        await kit.Dashboard.LoadAsync();

        var failedNotice = new ServersReturnNotice();
        using (var detail = Open(kit, "db", failedNotice))
        {
            kit.Servers.HideOverride = _ => Task.FromResult(false);
            await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        }

        using (var afterFailure = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, Clock(), failedNotice))
        {
            Assert.False(afterFailure.IsNoticeOpen); // nothing to announce: the rule says no
            Assert.False(ReturnNoticeAnnouncer.ShouldAnnounce(false, afterFailure.IsNoticeOpen,
                SaInlineNotice.AccessibleName(afterFailure.NoticeTitle, afterFailure.NoticeMessage)));
        }

        var notice = new ServersReturnNotice();
        kit.Servers.HideOverride = HideIn(kit);
        using (var detail = Open(kit, "web", notice))
        {
            await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        }

        using var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, Clock(), notice);
        var spoken = SaInlineNotice.AccessibleName(servers.NoticeTitle, servers.NoticeMessage);
        Assert.True(ReturnNoticeAnnouncer.ShouldAnnounce(false, servers.IsNoticeOpen, spoken));
        Assert.Equal(title, servers.NoticeTitle);
        Assert.Equal(message, servers.NoticeMessage);

        Assert.Contains("web", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("10.9.8.7", spoken, StringComparison.Ordinal); // display name only, never the address
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static PresentationClock Clock() => new(new FakeTimeProvider(Ui4TestKit.Now));

    private static async Task<(Ui4TestKit.Harness Kit, ServersReturnNotice Notice)> HiddenWebAsync(string culture)
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService(culture));
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();
        kit.Servers.HideOverride = HideIn(kit);
        using (var detail = Open(kit, "web", notice))
        {
            await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        }

        return (kit, notice);
    }

    private static ServerDetailViewModel Open(Ui4TestKit.Harness kit, string name, ServersReturnNotice notice)
    {
        var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization, history: null, returnFocus: null,
            monitoringOptions: null, clock: Clock(), serversNotice: notice);
        detail.Load(kit.Dashboard.VisibleServers.Single(card => card.Name == name).Server.Id, ServerDetailOrigin.Overview);
        return detail;
    }

    private static Func<Guid, Task<bool>> HideIn(Ui4TestKit.Harness kit) => id =>
    {
        var index = kit.Servers.Servers.FindIndex(server => server.Id == id);
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { IsHidden = true };
        kit.Servers.RaiseChanged();
        return Task.FromResult(true);
    };

    /// <summary>A controllable queue: nothing runs until <see cref="Drain"/>, exactly as a later dispatcher turn.</summary>
    private sealed class AnnouncerHarness
    {
        public AnnouncerHarness(Func<bool>? isOpen = null) =>
            Announcer = new ReturnNoticeAnnouncer(
                run =>
                {
                    Queue.Add(run);
                    return true;
                },
                isOpen ?? (() => Open),
                () => Live,
                () => Spoken,
                Raised.Add);

        public ReturnNoticeAnnouncer Announcer { get; }

        public List<Action> Queue { get; } = [];

        public List<string> Raised { get; } = [];

        public bool Open { get; set; } = true;

        public bool Live { get; set; } = true;

        public void Drain()
        {
            var turns = Queue.ToList();
            Queue.Clear();
            turns.ForEach(turn => turn());
        }
    }

    [GeneratedRegex(@"if \(ShellPageFocus\.GetKeepSidebar\(this\)\)\s*\{\s*_returnNotice\.AfterFocus\(\);\s*return;\s*\}")]
    private static partial Regex SidebarPathAnnounces();

    [GeneratedRegex(@"DispatcherQueuePriority\.Low, \(\) =>\s*\{\s*if \(!EditorReturnFocus\.TryFocusNow\(this, editorOpener\) && !RepeaterFocus\.FocusIndex\(ServersRepeater, index\)\)\s*\{\s*ShellPageFocus\.FocusHeading\(this\);\s*\}\s*_returnNotice\.AfterFocus\(\);\s*\}\);")]
    private static partial Regex FocusThenAnnounce();

    [GeneratedRegex(@"_returnNotice = new ReturnNoticeAnnouncer\(\s*run => DispatcherQueue\.TryEnqueue\(Microsoft\.UI\.Dispatching\.DispatcherQueuePriority\.Low, \(\) => run\(\)\),\s*\(\) => ViewModel\.IsNoticeOpen,\s*\(\) => ReturnNotice\.Visibility == Visibility\.Visible && SaLiveTree\.IsLive\(ReturnNotice\),\s*\(\) => AutomationProperties\.GetName\(ReturnNotice\),\s*RaiseReturnNotice\);")]
    private static partial Regex AnnouncerWiring();

    [GeneratedRegex(@"private void RaiseReturnNotice\(string content\) =>\s*\(FrameworkElementAutomationPeer\.FromElement\(ReturnNotice\) \?\? FrameworkElementAutomationPeer\.CreatePeerForElement\(ReturnNotice\)\)\s*\?\.RaiseNotificationEvent\(AutomationNotificationKind\.ActionCompleted, ReturnNoticeProcessing, content, ReturnNoticeActivityId\);")]
    private static partial Regex RaiseBody();
}
