using System.Windows.Input;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.4 D-UI4-DETAIL: the INTERIM server page (replaced in UI.5). It hosts the current <see cref="ServerCardViewModel"/>
/// of one server — the very instance the dashboard keeps up to date — so refresh, Histórico, Serviços e containers,
/// Editar, Ocultar and Remover behave exactly as on the card today. After every list rebuild it re-resolves the card by
/// id (an edit produces a new card instance); when the server is gone (hidden or removed, from here or elsewhere) it
/// returns to the origin once. Per-visit: disposed with its page.
/// </summary>
public sealed class ServerDetailViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly INavigationService _navigation;
    private readonly ILocalizationService _localization;
    private ServerCardViewModel? _card;
    private Guid _serverId;
    private bool _subscribed;
    private bool _left;
    private bool _disposed;

    // Null in unit tests (no WinUI dispatcher): the exit then runs inline.
    private readonly DispatcherQueue? _dispatcherQueue = TryGetDispatcher();

    public ServerDetailViewModel(DashboardViewModel dashboard, INavigationService navigation, ILocalizationService localization)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        GoBackCommand = new RelayCommand(GoBack);
        _dashboard.PropertyChanged += OnDashboardPropertyChanged;
    }

    /// <summary>
    /// Beacon r1 SHOULD-3: a failed Ocultar/Remover started here is reported HERE - the same error the Visão geral shows
    /// (one source: the dashboard view model), not only after going back.
    /// </summary>
    public bool IsOperationErrorOpen
    {
        get => _dashboard.IsOperationErrorOpen;
        set => _dashboard.IsOperationErrorOpen = value;
    }

    public bool IsConfigurationLockedOpen
    {
        get => _dashboard.IsConfigurationLockedOpen;
        set => _dashboard.IsConfigurationLockedOpen = value;
    }

    public string ConfigurationLockedMessage => _dashboard.ConfigurationLockedMessage;

    private void OnDashboardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.IsOperationErrorOpen) or nameof(DashboardViewModel.IsConfigurationLockedOpen))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    public ServerDetailOrigin Origin { get; private set; }

    /// <summary>The hosted card (null only before <see cref="Load"/> or after the server disappeared).</summary>
    public ServerCardViewModel? Card
    {
        get => _card;
        private set
        {
            if (SetProperty(ref _card, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public string Title => _card?.Name ?? string.Empty;

    /// <summary>The breadcrumb's parent: "Visão geral" or "Servidores", where the page was opened from.</summary>
    public string ParentText => _localization.GetString(
        Origin == ServerDetailOrigin.Servers ? "ServersPageTitle" : "OverviewPageTitle");

    public ICommand GoBackCommand { get; }

    /// <summary>Binds the page to one server. A server that is not in the list (anymore) returns to the origin at once.</summary>
    public void Load(Guid serverId, ServerDetailOrigin origin)
    {
        _serverId = serverId;
        Origin = origin;
        OnPropertyChanged(nameof(Origin));
        OnPropertyChanged(nameof(ParentText));
        if (!_subscribed)
        {
            _dashboard.ServersReloaded += OnServersReloaded;
            _subscribed = true;
        }

        Resolve();
    }

    public void Dispose()
    {
        _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        // A deferred Resolve queued before the page was replaced must not navigate afterwards.
        _disposed = true;
        if (_subscribed)
        {
            _dashboard.ServersReloaded -= OnServersReloaded;
            _subscribed = false;
        }
    }

    // Cortex r1 NIT-1: raised inside the dashboard's list rebuild; never swap the frame re-entrantly from there.
    private void OnServersReloaded(object? sender, EventArgs e)
    {
        if (_dispatcherQueue is null || !_dispatcherQueue.TryEnqueue(Resolve))
        {
            Resolve();
        }
    }

    private static DispatcherQueue? TryGetDispatcher()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Resolve()
    {
        if (_left || _disposed)
        {
            return;
        }

        var card = _dashboard.VisibleServers.FirstOrDefault(candidate => candidate.Server.Id == _serverId);
        if (card is null)
        {
            // Hidden or removed (here or elsewhere): back to where the user came from, exactly once.
            GoBack();
            return;
        }

        Card = card;
    }

    private void GoBack()
    {
        if (_left)
        {
            return;
        }

        _left = true;
        Dispose();
        if (Origin == ServerDetailOrigin.Servers)
        {
            _navigation.GoToServers();
        }
        else
        {
            _navigation.GoToDashboard();
        }
    }
}
