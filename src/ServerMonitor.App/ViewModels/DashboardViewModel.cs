using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IServerService _serverService;
    private readonly IServerProfileService _serverProfileService;
    private readonly IServerDialogService _dialogService;
    private readonly IServerConnectionStateStore _connectionStateStore;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IServerMonitoringStateStore _monitoringStateStore;
    private readonly IMonitoringEngine _monitoringEngine;
    private readonly IServerDiscoveryService _discoveryService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<DashboardViewModel> _logger;
    // Captured on the UI thread so engine state changes (raised on background loops) can be
    // marshalled back before touching bound properties. Null in unit tests, where handlers run
    // inline on the calling thread.
    private readonly DispatcherQueue? _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    // Normalized "host|port" of every configured server (visible and hidden), so a suggestion
    // already added is suppressed. This is a UX de-duplication only — never a trust decision.
    private HashSet<string> _configuredEndpoints = new(StringComparer.Ordinal);
    private bool _hasVisibleServers;
    private bool _hasDiscoveredServers;
    private int _discoveredCount;
    private bool _isOperationErrorOpen;

    public DashboardViewModel(
        IServerService serverService,
        IServerProfileService serverProfileService,
        IServerDialogService dialogService,
        IServerConnectionStateStore connectionStateStore,
        IServerMetricsStore metricsStore,
        IServerMonitoringStateStore monitoringStateStore,
        IMonitoringEngine monitoringEngine,
        IServerDiscoveryService discoveryService,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<DashboardViewModel> logger)
    {
        _serverService = serverService;
        _serverProfileService = serverProfileService;
        _dialogService = dialogService;
        _connectionStateStore = connectionStateStore;
        _metricsStore = metricsStore;
        _monitoringStateStore = monitoringStateStore;
        _monitoringEngine = monitoringEngine;
        _discoveryService = discoveryService;
        _localizationService = localizationService;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
        _connectionStateStore.StateChanged += OnConnectionStateChanged;
        _monitoringStateStore.StateChanged += OnMonitoringStateChanged;
        _discoveryService.DiscoveredChanged += OnDiscoveredChanged;
        AddServerCommand = new AsyncRelayCommand(AddServerAsync);
        OpenSettingsCommand = new RelayCommand(navigationService.GoToSettings);
    }

    public ObservableCollection<ServerCardViewModel> VisibleServers { get; } = [];

    public ObservableCollection<DiscoveredServerViewModel> DiscoveredServers { get; } = [];

    public ICommand AddServerCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public bool HasVisibleServers
    {
        get => _hasVisibleServers;
        private set => SetProperty(ref _hasVisibleServers, value);
    }

    public bool HasDiscoveredServers
    {
        get => _hasDiscoveredServers;
        private set => SetProperty(ref _hasDiscoveredServers, value);
    }

    /// <summary>
    /// Number of suggestions currently visible in the "Encontrados na rede" section — after
    /// ignored identities and already-configured servers are filtered out. Kept in sync by
    /// <see cref="RebuildDiscovered"/>, so it tracks Ignore, Reset and suppression changes.
    /// </summary>
    public int DiscoveredCount
    {
        get => _discoveredCount;
        private set
        {
            if (SetProperty(ref _discoveredCount, value))
            {
                OnPropertyChanged(nameof(DiscoveredCountAutomationName));
            }
        }
    }

    /// <summary>Localized, screen-reader-friendly rendering of <see cref="DiscoveredCount"/>.</summary>
    public string DiscoveredCountAutomationName => string.Format(
        CultureInfo.CurrentUICulture,
        _localizationService.GetString("DashboardDiscoveryCountName"),
        DiscoveredCount);

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
            var all = servers.ToList();
            _configuredEndpoints = BuildConfiguredEndpoints(all);
            SetServers(all.Where(server => !server.IsHidden));
            RebuildDiscovered();
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
        _monitoringStateStore.StateChanged -= OnMonitoringStateChanged;
        _discoveryService.DiscoveredChanged -= OnDiscoveredChanged;
    }

    private async Task AddServerAsync()
    {
        try
        {
            using var editorResult = await _dialogService.ShowEditorAsync(null);
            await PersistEditorResultAsync(editorResult);
        }
        catch (Exception exception)
        {
            HandleError(exception, "add server");
        }
    }

    private async Task AddDiscoveredAsync(DiscoveredServerViewModel discovered)
    {
        try
        {
            // Exactly the normal add flow, only pre-filled: cancel returns null and persists
            // nothing; a successful save reaches monitoring solely through ServersChanged.
            using var editorResult = await _dialogService.ShowEditorForDiscoveryAsync(discovered.ToPrefill());
            await PersistEditorResultAsync(editorResult);
        }
        catch (Exception exception)
        {
            HandleError(exception, "add discovered server");
        }
    }

    private async Task IgnoreDiscoveredAsync(DiscoveredServerViewModel discovered)
    {
        try
        {
            await _discoveryService.IgnoreAsync(discovered.Discovered.Identity);
        }
        catch (Exception exception)
        {
            HandleError(exception, "ignore discovered server");
        }
    }

    private async Task PersistEditorResultAsync(ServerEditorResult? editorResult)
    {
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
            else
            {
                _metricsStore.Remove(server.Id);
                if (editorResult.ConnectionResult is not null)
                {
                    _connectionStateStore.Set(server.Id, editorResult.ConnectionResult);
                }
                else
                {
                    _connectionStateStore.Remove(server.Id);
                }
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
                _metricsStore.Remove(server.Id);
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
                _metricsStore,
                _connectionStateStore,
                _monitoringStateStore,
                _monitoringEngine,
                () => EditServerAsync(server),
                () => HideServerAsync(server),
                () => RemoveServerAsync(server)));
        }

        HasVisibleServers = VisibleServers.Count > 0;
    }

    private void RebuildDiscovered()
    {
        DiscoveredServers.Clear();
        // The service already excludes ignored identities; here we additionally hide any suggestion
        // that maps to a server already configured (same normalized host/address + port).
        foreach (var discovered in _discoveryService.GetDiscovered())
        {
            if (IsAlreadyConfigured(discovered))
            {
                continue;
            }

            DiscoveredServers.Add(new DiscoveredServerViewModel(
                discovered,
                _localizationService,
                AddDiscoveredAsync,
                IgnoreDiscoveredAsync));
        }

        DiscoveredCount = DiscoveredServers.Count;
        HasDiscoveredServers = DiscoveredServers.Count > 0;
    }

    // Raised on the discovery service's background thread; marshal to the UI thread first.
    private void OnDiscoveredChanged(object? sender, EventArgs args)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            RebuildDiscovered();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(RebuildDiscovered);
        }
    }

    private bool IsAlreadyConfigured(DiscoveredService discovered)
    {
        if (_configuredEndpoints.Contains(EndpointKey(discovered.HostName, discovered.Port)))
        {
            return true;
        }

        foreach (var address in discovered.Addresses)
        {
            if (_configuredEndpoints.Contains(EndpointKey(address.ToString(), discovered.Port)))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> BuildConfiguredEndpoints(IEnumerable<Server> servers)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            set.Add(EndpointKey(server.Host, server.Port));
        }

        return set;
    }

    // Case, trailing-dot and IPv6-bracket normalization only. Deliberately shallow: this is a
    // display-level match to avoid suggesting something already added, never proof of identity.
    private static string EndpointKey(string host, int port) => NormalizeHost(host) + "|" + port;

    private static string NormalizeHost(string host)
    {
        var value = host.Trim();
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            value = value[1..^1];
        }

        return value.TrimEnd('.').ToLowerInvariant();
    }

    private async void OnServersChanged(object? sender, EventArgs args) => await LoadAsync();

    private void OnConnectionStateChanged(object? sender, Guid serverId)
    {
        var card = VisibleServers.FirstOrDefault(card => card.Server.Id == serverId);
        card?.UpdateConnectionState(_connectionStateStore.Get(serverId));
    }

    // Raised by the engine on a background loop; marshal to the UI thread before touching the card.
    private void OnMonitoringStateChanged(object? sender, Guid serverId)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            ApplyMonitoringState(serverId);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => ApplyMonitoringState(serverId));
        }
    }

    private void ApplyMonitoringState(Guid serverId)
    {
        var card = VisibleServers.FirstOrDefault(card => card.Server.Id == serverId);
        card?.ApplyMonitoringState(_monitoringStateStore.Get(serverId));
    }

    private void HandleError(Exception exception, string operation)
    {
        _logger.LogError(
            "Could not {Operation}. Exception type: {ExceptionType}.",
            operation,
            exception.GetType().Name);
        IsOperationErrorOpen = true;
    }
}
