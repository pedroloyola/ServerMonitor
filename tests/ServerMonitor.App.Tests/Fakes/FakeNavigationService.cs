using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>Inert <see cref="INavigationService"/> for ViewModel tests. Records the last navigation.</summary>
internal sealed class FakeNavigationService : INavigationService
{
    public NavigationDestination? CurrentDestination { get; set; }
    public event EventHandler? Navigated;
    public int NavigatedSubscribers => Navigated?.GetInvocationList().Length ?? 0;
    public void EnsureInitialNavigation() { if (CurrentDestination is null) GoToDashboard(); }
    public void GoToHistory() { CurrentDestination = NavigationDestination.History; Raise(); }

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

    private void Raise()
    {
        NavigatedAwayFromOverview?.Invoke(this, EventArgs.Empty);
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    public void GoToDashboard()
    {
        DashboardCount++;
        CurrentDestination = NavigationDestination.Overview;
        Navigated?.Invoke(this, EventArgs.Empty);
    }

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
        CurrentDestination = NavigationDestination.Settings;
        SettingsCount++;
        SettingsSections.Add(SettingsSection.General);
        Raise();
    }

    public List<SettingsSection> SettingsSections { get; } = [];

    public void GoToSettings(SettingsSection section)
    {
        CurrentDestination = section == SettingsSection.General ? NavigationDestination.Settings : NavigationDestination.SettingsData;
        SettingsCount++;
        SettingsSections.Add(section);
        if (section == SettingsSection.About)
        {
            AboutSettingsFocusRequests++;
        }

        Raise();
    }

    public int AboutSettingsFocusRequests { get; private set; }

    public bool ConsumeAboutSettingsFocus()
    {
        if (AboutSettingsFocusRequests == 0)
        {
            return false;
        }

        AboutSettingsFocusRequests--;
        return true;
    }

    public void GoToHistory(Guid serverId, string serverName)
    {
        LastHistoryServerId = serverId;
        CurrentDestination = NavigationDestination.History;
        Raise();
    }

    public void GoToWorkloads(Guid serverId, string serverName)
    {
        LastWorkloadsServerId = serverId;
        CurrentDestination = NavigationDestination.Workloads;
        Raise();
    }

    public int ServersCount { get; private set; }

    public void GoToServers()
    {
        ServersCount++;
        CurrentDestination = NavigationDestination.Servers;
        Raise();
    }

    public List<(Guid ServerId, ServerDetailOrigin Origin)> ServerDetailRequests { get; } = [];

    public List<Guid> ServerDetailReturns { get; } = [];

    public void ReturnToServerDetail(Guid serverId)
    {
        ServerDetailReturns.Add(serverId);
        CurrentDestination = NavigationDestination.Detail;
        Raise();
    }

    public void GoToServerDetail(Guid serverId, ServerDetailOrigin origin)
    {
        ServerDetailRequests.Add((serverId, origin));
        CurrentDestination = NavigationDestination.Detail;
        Raise();
    }

    public List<ServerEditorRequest> EditorRequests { get; } = [];

    public void GoToServerEditor(ServerEditorRequest request, Action? refused = null)
    {
        EditorRequests.Add(request);
        CurrentDestination = NavigationDestination.ServerEditor;
        Raise();
    }

    public int LeaveRequests { get; private set; }

    public void LeaveCurrentPageThen(Action continuation)
    {
        LeaveRequests++;
        continuation();
    }

    public void LeaveCurrentPageForActivation(Action continuation) => LeaveCurrentPageThen(continuation);
}
