using ServerMonitor.App.Services;

namespace ServerMonitor.App.ViewModels;

/// <summary>Process singleton. Selection is a projection of the router, never independent state.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly ShellContributionHost? _contributions;
    public ShellViewModel(INavigationService navigation)
        : this(navigation, null)
    {
    }

    /// <summary>M15.4a: with the contributed navigation entries (the container uses this constructor).</summary>
    public ShellViewModel(INavigationService navigation, ShellContributionHost? contributions)
    {
        _navigation = navigation;
        _contributions = contributions;
        _navigation.Navigated += OnNavigated;
    }

    /// <summary>
    /// M15.4a: the contributed navigation entries, in display order (realized on first use, on the UI thread). Empty
    /// when nothing was contributed, which leaves the sidebar exactly as it is.
    /// </summary>
    public IReadOnlyList<RealizedShellContribution> ContributedEntries =>
        _contributions?.NavigationEntries ?? [];

    /// <summary>M15.4a: the contributed entry shown, or null (a built-in destination is selected instead).</summary>
    public string? SelectedContributionId =>
        _navigation.CurrentDestination == NavigationDestination.Contribution ? _navigation.CurrentContributionId : null;

    /// <summary>M15.4a: opens a contributed entry by id, as a sidebar navigation.</summary>
    public void NavigateToContribution(string id)
    {
        IsSidebarNavigation = true;
        try
        {
            _navigation.GoToContribution(id);
        }
        finally { IsSidebarNavigation = false; }
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
        OnPropertyChanged(nameof(SelectedContributionId));
    }
    public void Dispose() => _navigation.Navigated -= OnNavigated;
}
