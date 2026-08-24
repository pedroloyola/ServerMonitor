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
    private readonly ILogger<SettingsViewModel> _logger;
    private int _selectedLanguageIndex;
    private int _selectedThemeIndex;
    private bool _isRestartNoticeOpen;
    private bool _hasHiddenServers;
    private bool _isServerOperationErrorOpen;

    public SettingsViewModel(
        IThemeService themeService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        IServerService serverService,
        ILogger<SettingsViewModel> logger)
    {
        _themeService = themeService;
        _localizationService = localizationService;
        _serverService = serverService;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
        BackCommand = new RelayCommand(navigationService.GoToDashboard);
        _selectedThemeIndex = (int)themeService.Current;
        _selectedLanguageIndex = localizationService.CurrentLanguageOverride switch
        {
            "pt-BR" => 1,
            "pt-PT" => 2,
            "en-US" => 3,
            _ => 0
        };
    }

    public ObservableCollection<HiddenServerItemViewModel> HiddenServers { get; } = [];

    public ICommand BackCommand { get; }

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

    public async Task LoadAsync()
    {
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

    public void Dispose() => _serverService.ServersChanged -= OnServersChanged;

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
