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
        if (_contributions is not null) _contributions.LinkTextChanged += OnLinkTextChanged;
    }

    /// <summary>M15.4a: the one link at the foot of the navigation (host-drawn), or null: then nothing is drawn.</summary>
    public RealizedShellLink? FooterLink => _contributions?.FooterLink;

    /// <summary>M15.4a: the footer link's current label (never the raw id); null without a footer link.</summary>
    public string? FooterLinkLabel => FooterLink is { } link ? _contributions!.LabelOf(link) : null;

    /// <summary>M15.4a: the footer link's current secondary text, or null.</summary>
    public string? FooterLinkHint => FooterLink is { } link ? _contributions!.HintOf(link) : null;

    /// <summary>M15.4a: opens the footer link's page, as a sidebar navigation (through the exit guard).</summary>
    public void OpenFooterLink()
    {
        if (FooterLink is { } link) NavigateToContribution(link.TargetId);
    }

    /// <summary>M15.4a: true when there is an unlisted page at all (only then does the window draw a way back).</summary>
    public bool HasUnlistedPages => _contributions is { UnlistedPages.Count: > 0 };

    /// <summary>M15.4a: an unlisted page is shown: the window draws "‹ Definições" above it.</summary>
    public bool IsUnlistedPageShown => HasUnlistedPages && _contributions!.IsUnlistedPage(SelectedContributionId);

    /// <summary>
    /// M15.4a: the way back from an unlisted page, drawn by the window: Settings, through the exit guard like every
    /// navigation (an unlisted page cannot navigate by itself).
    /// </summary>
    public void ReturnToSettings() => _navigation.GoToSettings();

    /// <summary>
    /// M15.4a: the links read their label and hint again (the window calls this when it becomes active and after a theme
    /// change; every navigation does too). Nothing happens without links.
    /// </summary>
    public void RefreshLinkText() => _contributions?.RefreshLinkText();

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
        OnPropertyChanged(nameof(IsUnlistedPageShown));
        RefreshLinkText();
    }

    private void OnLinkTextChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(FooterLinkLabel));
        OnPropertyChanged(nameof(FooterLinkHint));
    }

    public void Dispose()
    {
        _navigation.Navigated -= OnNavigated;
        if (_contributions is not null) _contributions.LinkTextChanged -= OnLinkTextChanged;
    }
}
