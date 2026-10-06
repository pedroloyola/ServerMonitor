using ServerMonitor.App.Services;

namespace ServerMonitor.App.ViewModels;

/// <summary>Process singleton. Selection is a projection of the router, never independent state.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly INavigationService _navigation;
    public ShellViewModel(INavigationService navigation)
    {
        _navigation = navigation;
        _navigation.Navigated += OnNavigated;
    }

    public ShellDestination? SelectedDestination => _navigation.CurrentDestination switch
    {
        NavigationDestination.Overview => ShellDestination.Overview,
        // UI.7 B-1 (UI.6 D-4): the server editor belongs to Servidores.
        NavigationDestination.Servers or NavigationDestination.Detail or NavigationDestination.Workloads
            or NavigationDestination.ServerEditor => ShellDestination.Servers,
        NavigationDestination.History => ShellDestination.History,
        NavigationDestination.Settings or NavigationDestination.SettingsData => ShellDestination.Settings,
        _ => null
    };

    public bool IsOverviewSelected => SelectedDestination == ShellDestination.Overview;
    public bool IsServersSelected => SelectedDestination == ShellDestination.Servers;
    public bool IsHistorySelected => SelectedDestination == ShellDestination.History;
    public bool IsSettingsSelected => SelectedDestination == ShellDestination.Settings;
    public bool IsSidebarNavigation { get; private set; }

    public void Navigate(ShellDestination destination)
    {
        IsSidebarNavigation = true;
        try
        {
        switch (destination)
        {
            case ShellDestination.Overview: _navigation.GoToDashboard(); break;
            case ShellDestination.Servers: _navigation.GoToServers(); break;
            case ShellDestination.History: _navigation.GoToHistory(); break;
            case ShellDestination.Settings: _navigation.GoToSettings(); break;
        }
        }
        finally { IsSidebarNavigation = false; }
    }

    private void OnNavigated(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(SelectedDestination));
        OnPropertyChanged(nameof(IsOverviewSelected)); OnPropertyChanged(nameof(IsServersSelected));
        OnPropertyChanged(nameof(IsHistorySelected)); OnPropertyChanged(nameof(IsSettingsSelected));
    }
    public void Dispose() => _navigation.Navigated -= OnNavigated;
}
