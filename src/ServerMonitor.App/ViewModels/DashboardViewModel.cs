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
    private readonly IServerDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<DashboardViewModel> _logger;
    private bool _hasVisibleServers;
    private bool _isOperationErrorOpen;

    public DashboardViewModel(
        IServerService serverService,
        IServerDialogService dialogService,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<DashboardViewModel> logger)
    {
        _serverService = serverService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
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

    public void Dispose() => _serverService.ServersChanged -= OnServersChanged;

    private async Task AddServerAsync()
    {
        try
        {
            var input = await _dialogService.ShowEditorAsync(null);
            if (input is null)
            {
                return;
            }

            var result = await _serverService.AddAsync(input);
            if (!result.Succeeded)
            {
                IsOperationErrorOpen = true;
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
            var input = await _dialogService.ShowEditorAsync(server);
            if (input is null)
            {
                return;
            }

            var result = await _serverService.UpdateAsync(server.Id, input);
            if (!result.Succeeded)
            {
                IsOperationErrorOpen = true;
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

            if (!await _serverService.RemoveAsync(server.Id))
            {
                IsOperationErrorOpen = true;
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
                _localizationService,
                () => EditServerAsync(server),
                () => HideServerAsync(server),
                () => RemoveServerAsync(server)));
        }

        HasVisibleServers = VisibleServers.Count > 0;
    }

    private async void OnServersChanged(object? sender, EventArgs args) => await LoadAsync();

    private void HandleError(Exception exception, string operation)
    {
        _logger.LogError(exception, "Could not {Operation}.", operation);
        IsOperationErrorOpen = true;
    }
}
