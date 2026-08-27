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

    public void GoToDashboard() => DashboardCount++;

    public void GoToSettings()
    {
    }

    public void GoToHistory(Guid serverId, string serverName) => LastHistoryServerId = serverId;

    public void GoToWorkloads(Guid serverId, string serverName) => LastWorkloadsServerId = serverId;
}
