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
        NavigationDestination.Servers or NavigationDestination.Detail or NavigationDestination.Workloads => ShellDestination.Servers,
        NavigationDestination.History => ShellDestination.History,
        NavigationDestination.Settings or NavigationDestination.SettingsData => ShellDestination.Settings,
        _ => null
    };

    public void Navigate(ShellDestination destination)
    {
        if (SelectedDestination == destination) return;
        switch (destination)
        {
            case ShellDestination.Overview: _navigation.GoToDashboard(); break;
            case ShellDestination.Servers: _navigation.GoToServers(); break;
            case ShellDestination.History: _navigation.GoToHistory(); break;
            case ShellDestination.Settings: _navigation.GoToSettings(); break;
        }
    }

    private void OnNavigated(object? sender, EventArgs args) => OnPropertyChanged(nameof(SelectedDestination));
    public void Dispose() => _navigation.Navigated -= OnNavigated;
}
