using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IServerService _serverService;
    private readonly IServerProfileService _serverProfileService;
    private readonly IServerDialogService _dialogService;
    private readonly IServerConnectionStateStore _connectionStateStore;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<DashboardViewModel> _logger;
    private bool _hasVisibleServers;
    private bool _isOperationErrorOpen;

    public DashboardViewModel(
        IServerService serverService,
        IServerProfileService serverProfileService,
        IServerDialogService dialogService,
        IServerConnectionStateStore connectionStateStore,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<DashboardViewModel> logger)
    {
        _serverService = serverService;
        _serverProfileService = serverProfileService;
        _dialogService = dialogService;
        _connectionStateStore = connectionStateStore;
        _localizationService = localizationService;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
        _connectionStateStore.StateChanged += OnConnectionStateChanged;
        AddServerCommand = new AsyncRelayCommand(AddServerAsync);
        OpenSettingsCommand = new RelayCommand(navigationService.GoToSettings);
    }

    public ObservableCollection<ServerCardViewModel> VisibleServers { get; } = [];

    public ICommand AddServerCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public bool HasVisibleServers
    {
        get => _hasVisibleServers;
        private set => SetProperty(ref _hasVisibleServers, value);
    }

    public bool IsOperationErrorOpen
    {
        get => _isOperationErrorOpen;
        set => SetProperty(ref _isOperationErrorOpen, value);
    }

    public async Task LoadAsync()
    {
        try
        {
            var servers = await _serverService.GetAllAsync();
            SetServers(servers.Where(server => !server.IsHidden));
        }
        catch (Exception exception)
        {
            HandleError(exception, "load servers");
        }
    }

    public void Dispose()
    {
        _serverService.ServersChanged -= OnServersChanged;
        _connectionStateStore.StateChanged -= OnConnectionStateChanged;
    }

    private async Task AddServerAsync()
    {
        try
        {
            using var editorResult = await _dialogService.ShowEditorAsync(null);
            if (editorResult is null)
            {
                return;
            }

            var result = await _serverProfileService.AddAsync(editorResult.Profile);
            if (!result.Succeeded)
            {
                IsOperationErrorOpen = true;
            }
            else if (editorResult.ConnectionResult is not null)
            {
                _connectionStateStore.Set(result.Server!.Id, editorResult.ConnectionResult);
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "add server");
        }
    }

    private async Task EditServerAsync(Server server)
    {
        try
        {
            using var editorResult = await _dialogService.ShowEditorAsync(server);
            if (editorResult is null)
            {
                return;
            }

            var result = await _serverProfileService.UpdateAsync(server, editorResult.Profile);
            if (!result.Succeeded)
            {
                IsOperationErrorOpen = true;
            }
            else if (editorResult.ConnectionResult is not null)
            {
                _connectionStateStore.Set(server.Id, editorResult.ConnectionResult);
            }
            else
            {
                _connectionStateStore.Remove(server.Id);
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "edit server");
        }
    }

    private async Task HideServerAsync(Server server)
    {
        try
        {
            if (!await _serverService.HideAsync(server.Id))
            {
                IsOperationErrorOpen = true;
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "hide server");
        }
    }

    private async Task RemoveServerAsync(Server server)
    {
        try
        {
            if (!await _dialogService.ConfirmRemoveAsync(server))
            {
                return;
            }

            if (!await _serverProfileService.RemoveAsync(server))
            {
                IsOperationErrorOpen = true;
            }
            else
            {
                _connectionStateStore.Remove(server.Id);
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "remove server");
        }
    }

    private void SetServers(IEnumerable<Server> servers)
    {
        VisibleServers.Clear();
        foreach (var server in servers.OrderBy(server => server.CreatedAt))
        {
            VisibleServers.Add(new ServerCardViewModel(
                server,
                _connectionStateStore.Get(server.Id),
                _localizationService,
                () => EditServerAsync(server),
                () => HideServerAsync(server),
                () => RemoveServerAsync(server)));
        }

        HasVisibleServers = VisibleServers.Count > 0;
    }

    private async void OnServersChanged(object? sender, EventArgs args) => await LoadAsync();

    private async void OnConnectionStateChanged(object? sender, Guid serverId) => await LoadAsync();

    private void HandleError(Exception exception, string operation)
    {
        _logger.LogError(
            "Could not {Operation}. Exception type: {ExceptionType}.",
            operation,
            exception.GetType().Name);
        IsOperationErrorOpen = true;
    }
}
