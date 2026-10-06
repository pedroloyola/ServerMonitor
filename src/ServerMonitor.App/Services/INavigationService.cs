using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

public interface INavigationService
{
    NavigationDestination? CurrentDestination { get; }
    event EventHandler? Navigated;
    void EnsureInitialNavigation();
    void GoToHistory();
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

    /// <summary>Settings, General sub-page ("Definições") — the tray, the toast and the overview's button land here.</summary>
    void GoToSettings();

    /// <summary>
    /// UI.5 §4 (Cortex 2): one of the two Settings sub-pages. <see cref="SettingsSection.About"/> opens "Dados e
    /// servidores" with its About card brought into view (H-UI5-4). Targeting the sub-page already shown re-notifies it
    /// (<see cref="ISettingsNavigationTarget"/>) instead of doing nothing, so a pending section request is never left
    /// hanging (Cortex #6).
    /// </summary>
    void GoToSettings(SettingsSection section);

    /// <summary>
    /// Asks the Settings page to bring the Background section into view. Set BEFORE or AFTER navigating there: when the
    /// General sub-page is already shown it is notified at once (UI.5, Cortex #6), otherwise on its next load, so the
    /// notice's activation lands on the right section without the Dashboard ever appearing (M13 S2 §D.1). Consumed once.
    /// </summary>
    void RequestBackgroundSettingsFocus();

    /// <summary>Consumes a pending background-section request. True at most once per request.</summary>
    bool ConsumeBackgroundSettingsFocus();

    /// <summary>Consumes a pending About-card request (<see cref="SettingsSection.About"/>). True at most once.</summary>
    bool ConsumeAboutSettingsFocus();

    void GoToHistory(Guid serverId, string serverName);

    void GoToWorkloads(Guid serverId, string serverName);

    /// <summary>UI.4 D-UI4-NAV: the Servidores directory (temporary entry "Ver todos" until UI.6).</summary>
    void GoToServers();

    /// <summary>
    /// UI.5: the Server Detail page of one server (over its live card), with a breadcrumb back to
    /// <paramref name="origin"/>. A server that is no longer listed lands on the origin instead (UI.4 MUST-1).
    /// </summary>
    void GoToServerDetail(Guid serverId, ServerDetailOrigin origin);

    /// <summary>
    /// UI.4 Beacon r1 SHOULD-4 (Boss decision): the way back from Histórico / Serviços e containers leads to the Server
    /// Detail page of THAT server, with the origin it was last opened from — "Servidores" when none is remembered (UI.5
    /// A-2). A server that is no longer listed lands on its remembered origin, or the Visão geral when none is remembered
    /// (UI.4, unchanged); <see cref="Guid.Empty"/> lands on the Visão geral.
    /// </summary>
    void ReturnToServerDetail(Guid serverId);
}
