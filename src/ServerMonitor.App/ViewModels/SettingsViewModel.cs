using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localizationService;
    private readonly IThemeService _themeService;
    private readonly IServerService _serverService;
    private readonly IServerDiscoveryService _discoveryService;
    private readonly INotificationSettingsService _notificationSettingsService;
    private readonly IBackgroundMonitoringSettingsService _backgroundSettingsService;
    private readonly IHistoryMaintenanceService _historyMaintenance;
    private readonly ILogger<SettingsViewModel> _logger;
    private int _selectedLanguageIndex;
    private int _selectedThemeIndex;
    private bool _isRestartNoticeOpen;
    private bool _hasHiddenServers;
    private bool _isServerOperationErrorOpen;
    private bool _isResetIgnoredSuccessOpen;
    private bool _isResetIgnoredErrorOpen;
    private bool _notificationsEnabled;
    private bool _isNotificationSettingsErrorOpen;
    private bool _isHistoryClearedOpen;
    private bool _isHistoryClearErrorOpen;
    private bool _isHistoryResetAvailable;
    private bool _isHistoryResetOpen;
    private bool _isHistoryResetErrorOpen;

    public SettingsViewModel(
        IThemeService themeService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        IServerService serverService,
        IServerDiscoveryService discoveryService,
        INotificationSettingsService notificationSettingsService,
        IBackgroundMonitoringSettingsService backgroundSettingsService,
        IHistoryMaintenanceService historyMaintenance,
        IAppVersionProvider appVersionProvider,
        ILogger<SettingsViewModel> logger)
    {
        _themeService = themeService;
        AppVersion = appVersionProvider.DisplayVersion;
        _localizationService = localizationService;
        _serverService = serverService;
        _discoveryService = discoveryService;
        _notificationSettingsService = notificationSettingsService;
        _backgroundSettingsService = backgroundSettingsService;
        _backgroundMonitoringEnabled = backgroundSettingsService.BackgroundMonitoringEnabled;
        _historyMaintenance = historyMaintenance;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
        _notificationSettingsService.NotificationsEnabledChanged += OnNotificationsEnabledChanged;
        BackCommand = new RelayCommand(navigationService.GoToDashboard);
        ResetIgnoredCommand = new AsyncRelayCommand(ResetIgnoredAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);
        ResetHistoryCommand = new AsyncRelayCommand(ResetHistoryAsync);
        _selectedThemeIndex = (int)themeService.Current;
        _notificationsEnabled = notificationSettingsService.NotificationsEnabled;
        _selectedLanguageIndex = localizationService.CurrentLanguageOverride switch
        {
            "pt-BR" => 1,
            "pt-PT" => 2,
            "en-US" => 3,
            _ => 0
        };
    }

    /// <summary>Real product version for the About section (packaged identity or assembly fallback).</summary>
    public string AppVersion { get; }

    public ObservableCollection<HiddenServerItemViewModel> HiddenServers { get; } = [];

    public ICommand BackCommand { get; }

    public ICommand ResetIgnoredCommand { get; }

    public ICommand ClearHistoryCommand { get; }

    public ICommand ResetHistoryCommand { get; }

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (SetProperty(ref _selectedThemeIndex, value) && Enum.IsDefined((AppThemePreference)value))
            {
                _themeService.Apply((AppThemePreference)value);
            }
        }
    }

    public int SelectedLanguageIndex
    {
        get => _selectedLanguageIndex;
        set
        {
            if (!SetProperty(ref _selectedLanguageIndex, value))
            {
                return;
            }

            var languageTag = value switch
            {
                1 => "pt-BR",
                2 => "pt-PT",
                3 => "en-US",
                _ => null
            };

            _localizationService.SetLanguage(languageTag);
            IsRestartNoticeOpen = true;
        }
    }

    public bool IsRestartNoticeOpen
    {
        get => _isRestartNoticeOpen;
        set => SetProperty(ref _isRestartNoticeOpen, value);
    }

    public bool HasHiddenServers
    {
        get => _hasHiddenServers;
        private set => SetProperty(ref _hasHiddenServers, value);
    }

    public bool IsServerOperationErrorOpen
    {
        get => _isServerOperationErrorOpen;
        set => SetProperty(ref _isServerOperationErrorOpen, value);
    }

    public bool IsResetIgnoredSuccessOpen
    {
        get => _isResetIgnoredSuccessOpen;
        set => SetProperty(ref _isResetIgnoredSuccessOpen, value);
    }

    public bool IsResetIgnoredErrorOpen
    {
        get => _isResetIgnoredErrorOpen;
        set => SetProperty(ref _isResetIgnoredErrorOpen, value);
    }

    private bool _backgroundMonitoringEnabled;

    /// <summary>
    /// Whether closing the window keeps ServerAlyzer monitoring in the background (M13 S2). This is the
    /// durable half of the explanation: the one-time toast may never arrive — notifications can be off —
    /// so this section, its description and its HelpText are what the user can always come back to.
    /// </summary>
    public bool BackgroundMonitoringEnabled
    {
        get => _backgroundMonitoringEnabled;
        set
        {
            if (_backgroundMonitoringEnabled == value)
            {
                return;
            }

            try
            {
                _backgroundSettingsService.SetBackgroundMonitoringEnabled(value);
                SetProperty(
                    ref _backgroundMonitoringEnabled,
                    _backgroundSettingsService.BackgroundMonitoringEnabled);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Could not persist the background setting. Exception type: {ExceptionType}.",
                    exception.GetType().Name);
                // The service commits its property only after the atomic replace, so re-notify to make
                // the toggle visibly return to the last committed value.
                OnPropertyChanged(nameof(BackgroundMonitoringEnabled));
            }
        }
    }

    /// <summary>Global switch for all server-health notifications.</summary>
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (_notificationsEnabled == value)
            {
                return;
            }

            try
            {
                _notificationSettingsService.SetNotificationsEnabled(value);
                SetProperty(ref _notificationsEnabled, _notificationSettingsService.NotificationsEnabled);
                IsNotificationSettingsErrorOpen = false;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Could not persist notification settings. Exception type: {ExceptionType}.",
                    exception.GetType().Name);
                // The service commits its property only after the atomic replace. Re-notify the
                // binding so the ToggleSwitch visibly returns to the last committed value.
                OnPropertyChanged(nameof(NotificationsEnabled));
                IsNotificationSettingsErrorOpen = true;
            }
        }
    }

    public bool IsNotificationSettingsErrorOpen
    {
        get => _isNotificationSettingsErrorOpen;
        set => SetProperty(ref _isNotificationSettingsErrorOpen, value);
    }

    public bool IsHistoryClearedOpen
    {
        get => _isHistoryClearedOpen;
        set => SetProperty(ref _isHistoryClearedOpen, value);
    }

    public bool IsHistoryClearErrorOpen
    {
        get => _isHistoryClearErrorOpen;
        set => SetProperty(ref _isHistoryClearErrorOpen, value);
    }

    public bool IsHistoryResetAvailable
    {
        get => _isHistoryResetAvailable;
        private set => SetProperty(ref _isHistoryResetAvailable, value);
    }

    public bool IsHistoryResetOpen
    {
        get => _isHistoryResetOpen;
        set => SetProperty(ref _isHistoryResetOpen, value);
    }

    public bool IsHistoryResetErrorOpen
    {
        get => _isHistoryResetErrorOpen;
        set => SetProperty(ref _isHistoryResetErrorOpen, value);
    }

    public async Task LoadAsync()
    {
        IsHistoryResetAvailable = !_historyMaintenance.IsAvailable;
        try
        {
            var servers = await _serverService.GetAllAsync();
            SetHiddenServers(servers.Where(server => server.IsHidden));
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
    }

    public void Dispose()
    {
        _serverService.ServersChanged -= OnServersChanged;
        _notificationSettingsService.NotificationsEnabledChanged -= OnNotificationsEnabledChanged;
    }

    private void OnNotificationsEnabledChanged(object? sender, EventArgs args) =>
        SetProperty(ref _notificationsEnabled, _notificationSettingsService.NotificationsEnabled,
            nameof(NotificationsEnabled));

    private async Task ResetIgnoredAsync()
    {
        IsResetIgnoredSuccessOpen = false;
        IsResetIgnoredErrorOpen = false;
        try
        {
            await _discoveryService.ResetIgnoredAsync();
            IsResetIgnoredSuccessOpen = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Could not reset ignored discoveries. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            IsResetIgnoredErrorOpen = true;
        }
    }

    private async Task ClearHistoryAsync()
    {
        IsHistoryClearedOpen = false;
        IsHistoryClearErrorOpen = false;
        try
        {
            var outcome = await _historyMaintenance.ClearHistoryWithConfirmationAsync();
            switch (outcome)
            {
                case HistoryClearOutcome.Cleared:
                    IsHistoryClearedOpen = true;
                    break;
                case HistoryClearOutcome.Unavailable:
                    IsHistoryClearErrorOpen = true;
                    IsHistoryResetAvailable = true;
                    break;
                // Cancelled: leave both closed.
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Could not clear history. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            IsHistoryClearErrorOpen = true;
        }
    }

    private async Task ResetHistoryAsync()
    {
        IsHistoryResetOpen = false;
        IsHistoryResetErrorOpen = false;
        try
        {
            var outcome = await _historyMaintenance.ResetHistoryWithConfirmationAsync();
            switch (outcome)
            {
                case HistoryResetOutcome.Reset:
                    IsHistoryResetOpen = true;
                    IsHistoryResetAvailable = false;
                    IsHistoryClearErrorOpen = false;
                    break;
                case HistoryResetOutcome.Unavailable:
                    IsHistoryResetErrorOpen = true;
                    IsHistoryResetAvailable = true;
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Could not reset history. Exception type: {ExceptionType}.",
                exception.GetType().Name);
            IsHistoryResetErrorOpen = true;
            IsHistoryResetAvailable = true;
        }
    }

    private async Task RestoreAsync(Server server)
    {
        try
        {
            if (!await _serverService.RestoreAsync(server.Id))
            {
                IsServerOperationErrorOpen = true;
            }
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
    }

    private void SetHiddenServers(IEnumerable<Server> servers)
    {
        HiddenServers.Clear();
        foreach (var server in servers.OrderBy(server => server.CreatedAt))
        {
            HiddenServers.Add(new HiddenServerItemViewModel(
                server,
                _localizationService,
                () => RestoreAsync(server)));
        }

        HasHiddenServers = HiddenServers.Count > 0;
    }

    private async void OnServersChanged(object? sender, EventArgs args) => await LoadAsync();

    private void HandleError(Exception exception)
    {
        _logger.LogError(exception, "Could not manage hidden servers.");
        IsServerOperationErrorOpen = true;
    }
}
