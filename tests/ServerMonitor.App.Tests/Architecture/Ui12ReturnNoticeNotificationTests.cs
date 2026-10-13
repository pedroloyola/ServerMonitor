using System.Text.RegularExpressions;
using System.Xml.Linq;
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
/// once as a UIA notification in the turn AFTER its focus decision (Cortex research; Boss direction). The order and the
/// event are pinned by source (App.Tests has no XAML runtime); the rule is pure; "only on success" is the view model's.
/// What Narrator says stays runtime QA with the human.
/// </summary>
public sealed partial class Ui12ReturnNoticeNotificationTests
{
    // ---- the pure rule ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, "Servidor ocultado. Podes mostrar web de novo nas Definições.", true)]
    [InlineData(true, true, "Servidor ocultado. Podes mostrar web de novo nas Definições.", false)] // once per page
    [InlineData(false, false, "Servidor ocultado. Podes mostrar web de novo nas Definições.", false)] // no notice / closed
    [InlineData(false, true, "", false)]
    [InlineData(false, true, "   ", false)]
    [InlineData(false, true, null, false)]
    public void TheNotice_IsAnnouncedOnce_OnlyWhileOpen_AndOnlyWithText(bool announced, bool open, string? content, bool expected) =>
        Assert.Equal(expected, ServersPage.ShouldAnnounceReturnNotice(announced, open, content));

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

    // ---- the page: order and the one raise ---------------------------------------------------------------------------

    [Fact]
    public void ThePage_AnnouncesAfterItsFocusDecision_AlsoWhenTheSidebarKeepsFocus()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServersPage.xaml.cs");

        Assert.Matches(SidebarPathAnnounces(), page);
        Assert.Matches(FocusThenAnnounce(), page);
        Assert.Equal(2, Regex.Matches(page, Regex.Escape("AnnounceReturnNoticeAfterFocus();")).Count);
    }

    [Fact]
    public void TheNotification_IsRaisedOnce_InALaterTurn_OnlyWithANotice_WithTheToastsOwnText()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServersPage.xaml.cs");

        Assert.Matches(AnnounceBody(), page);
        Assert.Single(Regex.Matches(page, Regex.Escape("RaiseNotificationEvent(")));
        Assert.Single(Regex.Matches(page, Regex.Escape("_returnNoticeAnnounced = true;")));
        Assert.DoesNotContain("RaiseAutomationEvent", page, StringComparison.Ordinal);
        Assert.Matches(
            @"internal static bool ShouldAnnounceReturnNotice\(bool announced, bool noticeOpen, string\? content\) =>\s*!announced && noticeOpen && !string\.IsNullOrWhiteSpace\(content\);",
            page);
    }

    // ---- the toast: opt-in silence, default unchanged ---------------------------------------------------------------

    [Fact]
    public void TheToast_RaisesItsLiveRegionOnlyWhenItAnnouncesItself_DefaultTrue()
    {
        var toast = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaToast.cs");

        Assert.Matches(@"nameof\(AnnouncesItself\), typeof\(bool\), typeof\(SaToast\), new PropertyMetadata\(true, ", toast);
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
        var clock = new PresentationClock(new FakeTimeProvider(Ui4TestKit.Now));

        var failedNotice = new ServersReturnNotice();
        using (var detail = Open(kit, "db", failedNotice))
        {
            kit.Servers.HideOverride = _ => Task.FromResult(false);
            await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        }

        using (var afterFailure = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, clock, failedNotice))
        {
            Assert.False(afterFailure.IsNoticeOpen); // nothing to announce: the rule says no
            Assert.False(ServersPage.ShouldAnnounceReturnNotice(false, afterFailure.IsNoticeOpen,
                SaInlineNotice.AccessibleName(afterFailure.NoticeTitle, afterFailure.NoticeMessage)));
        }

        var notice = new ServersReturnNotice();
        kit.Servers.HideOverride = HideIn(kit);
        using (var detail = Open(kit, "web", notice))
        {
            await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        }

        using var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, clock, notice);
        var spoken = SaInlineNotice.AccessibleName(servers.NoticeTitle, servers.NoticeMessage);
        Assert.True(ServersPage.ShouldAnnounceReturnNotice(false, servers.IsNoticeOpen, spoken));
        Assert.Equal(title, servers.NoticeTitle);
        Assert.Equal(message, servers.NoticeMessage);

        Assert.Contains("web", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("10.9.8.7", spoken, StringComparison.Ordinal); // display name only, never the address
    }

    private static ServerDetailViewModel Open(Ui4TestKit.Harness kit, string name, ServersReturnNotice notice)
    {
        var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization, history: null, returnFocus: null,
            monitoringOptions: null, clock: new PresentationClock(new FakeTimeProvider(Ui4TestKit.Now)), serversNotice: notice);
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

    [GeneratedRegex(@"if \(ShellPageFocus\.GetKeepSidebar\(this\)\)\s*\{\s*AnnounceReturnNoticeAfterFocus\(\);\s*return;\s*\}")]
    private static partial Regex SidebarPathAnnounces();

    [GeneratedRegex(@"DispatcherQueuePriority\.Low, \(\) =>\s*\{\s*if \(!EditorReturnFocus\.TryFocusNow\(this, editorOpener\) && !RepeaterFocus\.FocusIndex\(ServersRepeater, index\)\)\s*\{\s*ShellPageFocus\.FocusHeading\(this\);\s*\}\s*AnnounceReturnNoticeAfterFocus\(\);\s*\}\);")]
    private static partial Regex FocusThenAnnounce();

    [GeneratedRegex(@"private void AnnounceReturnNoticeAfterFocus\(\)\s*\{\s*if \(!ViewModel\.IsNoticeOpen\)\s*\{\s*return;\s*\}\s*DispatcherQueue\.TryEnqueue\(Microsoft\.UI\.Dispatching\.DispatcherQueuePriority\.Low, \(\) =>\s*\{\s*var content = AutomationProperties\.GetName\(ReturnNotice\);\s*if \(!ShouldAnnounceReturnNotice\(_returnNoticeAnnounced, ViewModel\.IsNoticeOpen, content\)\s*\|\| \(FrameworkElementAutomationPeer\.FromElement\(ReturnNotice\) \?\? FrameworkElementAutomationPeer\.CreatePeerForElement\(ReturnNotice\)\) is not \{ \} peer\)\s*\{\s*return;\s*\}\s*_returnNoticeAnnounced = true;\s*peer\.RaiseNotificationEvent\(AutomationNotificationKind\.ActionCompleted, ReturnNoticeProcessing, content, ReturnNoticeActivityId\);\s*\}\);\s*\}")]
    private static partial Regex AnnounceBody();
}
