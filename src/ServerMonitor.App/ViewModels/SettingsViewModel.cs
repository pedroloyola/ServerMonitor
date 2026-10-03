using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
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
    private readonly INavigationService _navigationService;
    private readonly IBackgroundDegradationNotice _backgroundDegradationNotice;
    private bool _isBackgroundDegradedNoticeOpen;
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
    private bool _isConfigurationLockedOpen;

    public SettingsViewModel(
        IThemeService themeService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        IServerService serverService,
        IServerDiscoveryService discoveryService,
        INotificationSettingsService notificationSettingsService,
        IBackgroundMonitoringSettingsService backgroundSettingsService,
        IBackgroundDegradationNotice backgroundDegradationNotice,
        IHistoryMaintenanceService historyMaintenance,
        IAppVersionProvider appVersionProvider,
        ILogger<SettingsViewModel> logger,
        PresentationClock? clock = null)
    {
        _toastTimer = new TransientNoticeTimer((clock ?? PresentationClock.System).TimeProvider);
        _themeService = themeService;
        AppVersion = appVersionProvider.DisplayVersion;
        _localizationService = localizationService;
        _serverService = serverService;
        _discoveryService = discoveryService;
        _notificationSettingsService = notificationSettingsService;
        _backgroundSettingsService = backgroundSettingsService;
        _navigationService = navigationService;
        _backgroundDegradationNotice = backgroundDegradationNotice;
        _isBackgroundDegradedNoticeOpen = backgroundDegradationNotice.IsDegraded;
        backgroundDegradationNotice.Changed += OnBackgroundDegradationChanged;
        _backgroundMonitoringEnabled = backgroundSettingsService.BackgroundMonitoringEnabled;
        _historyMaintenance = historyMaintenance;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
        _notificationSettingsService.NotificationsEnabledChanged += OnNotificationsEnabledChanged;
        BackCommand = new RelayCommand(navigationService.GoToDashboard);
        // UI.5 Cortex 2: the in-page links between the two sub-pages (no sidebar until UI.6).
        OpenDataCommand = new RelayCommand(() => navigationService.GoToSettings(SettingsSection.Data));
        OpenAboutCommand = new RelayCommand(() => navigationService.GoToSettings(SettingsSection.About));
        BackToGeneralCommand = new RelayCommand(() => navigationService.GoToSettings(SettingsSection.General));
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

    /// <summary>Figma 112:8271 "Versão 1.1.1" with the REAL version (the Figma number is illustrative).</summary>
    public string AboutVersionText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture, _localizationService.GetString("SettingsAboutVersionFormat"), AppVersion);

    private string? _toastTitle;
    private string? _toastMessage;
    private readonly TransientNoticeTimer _toastTimer;

    /// <summary>
    /// UI.5 (Figma §3.2 112:20712 / 112:21538 / 112:21293): the "Dados e servidores" page's transient success notice —
    /// one at a time, the latest success wins. Fix round 2 (Boss decision 2, Cortex C1 N-C2): it closes itself after
    /// <see cref="TransientNoticeTimer.Duration"/>, when the user closes it, and when the page is left
    /// (<see cref="NotifyDataNavigatedFrom"/>) - a later visit never shows it again. Page-local (not a global toast
    /// system); errors stay inline next to their setting.
    /// </summary>
    public bool IsToastOpen => _toastTitle is not null;

    public string ToastTitle => _toastTitle ?? string.Empty;

    public string ToastMessage => _toastMessage ?? string.Empty;

    public string ToastCloseAutomationName => _localizationService.GetString("SettingsToastCloseName");

    public ICommand DismissToastCommand => _dismissToastCommand ??= new RelayCommand(() => ShowToast(null, null));

    private RelayCommand? _dismissToastCommand;

    private void ShowToast(string? titleKey, string? messageKey)
    {
        if (titleKey is null)
        {
            _toastTimer.Cancel();
            if (_toastTitle is null)
            {
                return;
            }
        }
        else
        {
            _toastTimer.Start(() => ShowToast(null, null));
        }

        _toastTitle = titleKey is null ? null : _localizationService.GetString(titleKey);
        _toastMessage = messageKey is null ? null : _localizationService.GetString(messageKey);
        OnPropertyChanged(nameof(IsToastOpen));
        OnPropertyChanged(nameof(ToastTitle));
        OnPropertyChanged(nameof(ToastMessage));
    }

    public ObservableCollection<HiddenServerItemViewModel> HiddenServers { get; } = [];

    /// <summary>Beacon C1 M2: the hidden-servers list's accessible name (its card title).</summary>
    public string HiddenServersListName => _localizationService.GetString("SettingsHiddenServersTitle.Text");

    public ICommand BackCommand { get; }

    /// <summary>"Definições" → "Dados e servidores".</summary>
    public ICommand OpenDataCommand { get; }

    /// <summary>H-UI5-4: the "Sobre" disclosure in General opens Data with its About card in view.</summary>
    public ICommand OpenAboutCommand { get; }

    /// <summary>"Dados e servidores" → "Definições" (the Data page's breadcrumb parent).</summary>
    public ICommand BackToGeneralCommand { get; }

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
    private bool _isBackgroundSectionRequested;

    /// <summary>
    /// Open while this session has no notification-area icon. The window appears without the user asking
    /// for it in that case, so it must arrive WITH the explanation: closing now quits, and the saved
    /// preference was not changed (§13).
    /// </summary>
    public bool IsBackgroundDegradedNoticeOpen
    {
        get => _isBackgroundDegradedNoticeOpen;
        set => SetProperty(ref _isBackgroundDegradedNoticeOpen, value);
    }

    /// <summary>
    /// The persistent half of the degradation UX: true for as long as the session is degraded, even
    /// after the InfoBar is dismissed; the view collapses the caption otherwise. (UI.5: a bool — view
    /// models expose no WinUI Visibility.)
    /// </summary>
    public bool IsBackgroundDegraded => _backgroundDegradationNotice.IsDegraded;

    private void OnBackgroundDegradationChanged(object? sender, EventArgs args)
    {
        IsBackgroundDegradedNoticeOpen = _backgroundDegradationNotice.IsDegraded;
        OnPropertyChanged(nameof(IsBackgroundDegraded));
    }

    /// <summary>
    /// True when this navigation was asked to land on the Background section — the activation of the
    /// one-time notice. The page brings the section into view when it is set.
    /// </summary>
    public bool IsBackgroundSectionRequested
    {
        get => _isBackgroundSectionRequested;
        private set => SetProperty(ref _isBackgroundSectionRequested, value);
    }

    /// <summary>
    /// Called by the page when it is navigated to. This is where the pending "focus the Background
    /// section" request is actually CONSUMED — the request exists precisely so the notice's activation
    /// can land on the right section, and a request nobody consumes is the same class of defect as a
    /// RefreshAll nobody calls.
    /// </summary>
    public void NotifyNavigatedTo() =>
        IsBackgroundSectionRequested = _navigationService.ConsumeBackgroundSettingsFocus();

    private bool _isAboutSectionRequested;

    /// <summary>H-UI5-4: true when this visit of "Dados e servidores" was asked to bring the About card into view.</summary>
    public bool IsAboutSectionRequested
    {
        get => _isAboutSectionRequested;
        private set => SetProperty(ref _isAboutSectionRequested, value);
    }

    /// <summary>Called by the Data sub-page when it is navigated to (Loaded, or again while shown): consumes the About request.</summary>
    public void NotifyDataNavigatedTo() =>
        IsAboutSectionRequested = _navigationService.ConsumeAboutSettingsFocus();

    /// <summary>Called by the Data sub-page when it is left (Unloaded): its transient toast ends with the visit.</summary>
    public void NotifyDataNavigatedFrom() => ShowToast(null, null);

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
                if (exception is ConfigurationLockedException)
                {
                    IsConfigurationLockedOpen = true;
                }
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
                if (exception is ConfigurationLockedException)
                {
                    IsConfigurationLockedOpen = true;
                }
                else
                {
                    IsNotificationSettingsErrorOpen = true;
                }
            }
        }
    }

    public bool IsNotificationSettingsErrorOpen
    {
        get => _isNotificationSettingsErrorOpen;
        set => SetProperty(ref _isNotificationSettingsErrorOpen, value);
    }

    /// <summary>
    /// A write was refused because a restore holds the configuration (M14.6). It gets its own message:
    /// "try again" would be wrong, the change can only be made after ServerAlyzer restarts.
    /// </summary>
    public bool IsConfigurationLockedOpen
    {
        get => _isConfigurationLockedOpen;
        set => SetProperty(ref _isConfigurationLockedOpen, value);
    }

    public string ConfigurationLockedMessage => _localizationService.GetString(BackupMessageKeys.ConfigurationLocked);

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

    private readonly Lock _loadGate = new();
    private Task? _loadTask;
    private bool _reloadRequested;

    /// <summary>
    /// UI.5 §4: idempotent and safe under concurrency. Both sub-pages call it on Loaded and a ServersChanged can arrive
    /// meanwhile; overlapping calls share ONE running load (single-flight) and a call made while it runs schedules exactly
    /// one more pass afterwards, so the hidden-servers list is rebuilt from the newest data and never twice at once (no
    /// duplicated or flickering rows). Ordering is established under one lock, never by timing.
    /// </summary>
    public Task LoadAsync()
    {
        TaskCompletionSource done;
        lock (_loadGate)
        {
            if (_loadTask is { } running)
            {
                _reloadRequested = true;
                return running;
            }

            _reloadRequested = false;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loadTask = done.Task;
        }

        _ = RunLoadsAsync(done);
        return done.Task;
    }

    private async Task RunLoadsAsync(TaskCompletionSource done)
    {
        try
        {
            while (true)
            {
                try
                {
                    await LoadOnceAsync();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Cortex B1 N-7: never a silent success. LoadOnceAsync reports its own failures; anything that escapes
                    // it is logged and surfaced through the same notice, and the next pass (if requested) still runs.
                    _logger.LogError("Settings could not be loaded. Exception type: {ExceptionType}.", exception.GetType().Name);
                    IsServerOperationErrorOpen = true;
                }

                lock (_loadGate)
                {
                    if (!_reloadRequested)
                    {
                        // Released under the same lock a new caller checks, so a request can never be lost in between.
                        _loadTask = null;
                        break;
                    }

                    _reloadRequested = false;
                }
            }
        }
        finally
        {
            lock (_loadGate)
            {
                // Defensive: an unexpected exception must not leave a finished load registered as running forever.
                if (ReferenceEquals(_loadTask, done.Task))
                {
                    _loadTask = null;
                }
            }

            done.TrySetResult();
        }
    }

    private async Task LoadOnceAsync()
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
            ShowToast("SettingsResetIgnoredSuccess.Title", "SettingsResetIgnoredSuccess.Message");
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
                    // A-10: the feedback stays here, where the user acted (no implicit navigation to Histórico).
                    ShowToast("SettingsHistoryClearedSuccess.Title", "SettingsHistoryClearedSuccess.Message");
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
                    ShowToast("SettingsHistoryResetSuccess.Title", "SettingsHistoryResetSuccess.Message");
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
            else
            {
                ShowToast("SettingsServerRestoredTitle", "SettingsServerRestoredMessage");
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
        var ordered = servers.OrderBy(server => server.CreatedAt).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var server = ordered[index];
            HiddenServers.Add(new HiddenServerItemViewModel(
                server,
                _localizationService,
                () => RestoreAsync(server),
                positionInSet: index + 1,
                sizeOfSet: ordered.Count));
        }

        HasHiddenServers = HiddenServers.Count > 0;
    }

    private async void OnServersChanged(object? sender, EventArgs args) => await LoadAsync();

    private void HandleError(Exception exception)
    {
        if (exception is ConfigurationLockedException)
        {
            IsConfigurationLockedOpen = true;
            return;
        }

        _logger.LogError(exception, "Could not manage hidden servers.");
        IsServerOperationErrorOpen = true;
    }
}
