using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

public interface INavigationService
{
    void Initialize(Frame frame);

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
}
