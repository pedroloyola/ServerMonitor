using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

public interface INavigationService
{
    void Initialize(Frame frame);

    /// <summary>
    /// Raised after every navigation to a page OTHER than the Visão geral (UI.4 Cortex r1 SHOULD-1): the user moving
    /// elsewhere is newer than a widget deep-link still waiting for its server, so the dashboard drops that pending
    /// request instead of yanking the user away later (e.g. after a restore or an unhide brings the server back).
    /// Navigating TO the overview never raises it: the shell's own startup navigation and a widget activation both land
    /// there, and must not cancel the deep-link they carry.
    /// </summary>
    event EventHandler? NavigatedAwayFromOverview;

    void NavigateTo<TPage>() where TPage : Page;

    void GoToDashboard();

    void GoToSettings();

    /// <summary>
    /// Asks the Settings page to bring the Background section into view when it next loads. Set BEFORE
    /// the window is shown, so the notice's activation lands on the right section without the Dashboard
    /// ever appearing (M13 S2 §D.1). Consumed once by the page.
    /// </summary>
    void RequestBackgroundSettingsFocus();

    /// <summary>Consumes a pending background-section request. True at most once per request.</summary>
    bool ConsumeBackgroundSettingsFocus();

    void GoToHistory(Guid serverId, string serverName);

    void GoToWorkloads(Guid serverId, string serverName);

    /// <summary>UI.4 D-UI4-NAV: the Servidores directory (temporary entry "Ver todos" until UI.6).</summary>
    void GoToServers();

    /// <summary>
    /// UI.4 D-UI4-DETAIL: the interim page hosting the current <c>ServerFullCard</c> of one server, with a breadcrumb back
    /// to <paramref name="origin"/>. Replaced in UI.5.
    /// </summary>
    void GoToServerDetail(Guid serverId, ServerDetailOrigin origin);
}
