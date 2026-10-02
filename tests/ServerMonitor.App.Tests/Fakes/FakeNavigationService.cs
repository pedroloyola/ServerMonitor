using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>Inert <see cref="INavigationService"/> for ViewModel tests. Records the last navigation.</summary>
internal sealed class FakeNavigationService : INavigationService
{
    public int DashboardCount { get; private set; }

    public Guid? LastHistoryServerId { get; private set; }

    public Guid? LastWorkloadsServerId { get; private set; }

    public void Initialize(Frame frame)
    {
    }

    public void NavigateTo<TPage>() where TPage : Page
    {
    }

    public event EventHandler? NavigatedAwayFromOverview;

    private void Raise() => NavigatedAwayFromOverview?.Invoke(this, EventArgs.Empty);

    public void GoToDashboard() => DashboardCount++;

    public void RequestBackgroundSettingsFocus() => BackgroundSettingsFocusRequests++;

    public int BackgroundSettingsFocusRequests { get; private set; }

    public bool ConsumeBackgroundSettingsFocus()
    {
        if (BackgroundSettingsFocusRequests == 0)
        {
            return false;
        }

        BackgroundSettingsFocusRequests--;
        return true;
    }

    public int SettingsCount { get; private set; }

    public void GoToSettings()
    {
        SettingsCount++;
        Raise();
    }

    public void GoToHistory(Guid serverId, string serverName)
    {
        LastHistoryServerId = serverId;
        Raise();
    }

    public void GoToWorkloads(Guid serverId, string serverName)
    {
        LastWorkloadsServerId = serverId;
        Raise();
    }

    public int ServersCount { get; private set; }

    public void GoToServers()
    {
        ServersCount++;
        Raise();
    }

    public List<(Guid ServerId, ServerDetailOrigin Origin)> ServerDetailRequests { get; } = [];

    public void GoToServerDetail(Guid serverId, ServerDetailOrigin origin)
    {
        ServerDetailRequests.Add((serverId, origin));
        Raise();
    }
}
