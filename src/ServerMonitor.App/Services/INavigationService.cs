using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

public interface INavigationService
{
    void Initialize(Frame frame);

    void NavigateTo<TPage>() where TPage : Page;

    void GoToDashboard();

    void GoToSettings();

    void GoToHistory(Guid serverId, string serverName);
}
