using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IServerService _serverService;
    private readonly IServerProfileService _serverProfileService;
    private readonly IServerDialogService _dialogService;
    // UI.7 B-3: the editor page's lifetime owner; every Add / Edit / discovery / SSH-import entry point opens it.
    private readonly IServerEditorSession _editorSession;
    private readonly IServerConnectionStateStore _connectionStateStore;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IServerMonitoringStateStore _monitoringStateStore;
    private readonly IMonitoringEngine _monitoringEngine;
    private readonly IServerDiscoveryService _discoveryService;
    private readonly INavigationService _navigationService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<DashboardViewModel> _logger;
    // Captured on the UI thread so engine state changes (raised on background loops) can be
    // marshalled back before touching bound properties. Null in unit tests, where handlers run
    // inline on the calling thread.
    private readonly DispatcherQueue? _dispatcherQueue = TryGetDispatcher();

    // GetForCurrentThread() throws a WinRT COMException in a non-UI/unpackaged host (e.g. the test
    // runner). Treat that as "no dispatcher" so the view model stays constructible there. [P-010, L-016]
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
    // Normalized "host|port" of every configured server (visible and hidden), so a suggestion
    // already added is suppressed. This is a UX de-duplication only — never a trust decision.
    private HashSet<string> _configuredEndpoints = new(StringComparer.Ordinal);
    private bool _hasVisibleServers;
    private bool _hasDiscoveredServers;
    private int _discoveredCount;
    private bool _isOperationErrorOpen;
    private bool _isConfigurationLockedOpen;
    // Non-null only when the composed discovery service is the live one (M14.5 empty-state search indicator).
    private readonly IServerDiscoveryActivity? _discoveryActivity;

    // UI.4 overview. Every field below is read null-safely: the runtime-free contract tests build this view model with
    // RuntimeHelpers.GetUninitializedObject (no constructor, no field initializers) and still call LoadAsync.
    private readonly IRefreshAllCoordinator? _refreshAllCoordinator;
    private readonly PriorityProblemSelector? _prioritySelector;
    private readonly PresentationClock? _clock;
    private bool _isLoading;
    private bool _isRefreshingAll;
    private int _hiddenServerCount;
    private HealthSummary? _healthSummary;
    private IReadOnlyList<HealthBarSegment>? _healthBarSegments;
    private IReadOnlyList<HealthChip>? _healthChips;
    private string? _healthAutomationName;
    private bool _hasReadings;
    private bool _overviewDirty;
    private bool _overviewScheduled;
    private IReadOnlyList<HealthSegment>? _healthSegments;
    private PriorityProblem? _priorityProblem;
    private string? _updatedAgoDisplay;
    private string? _overviewSearchText;
    private IReadOnlyList<ServerDirectoryRowViewModel>? _overviewRows;
    private IReadOnlyList<ServerDirectoryRowViewModel>? _overviewServers;
    private AsyncRelayCommand? _refreshAllCommand;
    private (OverviewReturnTarget Target, Guid ServerId) _returnFocus;
    private Guid? _directoryReturnFocus;

    public DashboardViewModel(
        IServerService serverService,
        IServerProfileService serverProfileService,
        IServerDialogService dialogService,
        IServerEditorSession editorSession,
        IServerConnectionStateStore connectionStateStore,
        IServerMetricsStore metricsStore,
        IServerMonitoringStateStore monitoringStateStore,
        IMonitoringEngine monitoringEngine,
        IServerDiscoveryService discoveryService,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<DashboardViewModel> logger,
        IRefreshAllCoordinator? refreshAllCoordinator = null,
        MonitoringOptions? monitoringOptions = null,
        PresentationClock? clock = null)
    {
        _refreshAllCoordinator = refreshAllCoordinator;
        // D-UI4-PRIORITY: the SAME thresholds instance the engine is composed with (App registers one MonitoringOptions).
        _prioritySelector = new PriorityProblemSelector((monitoringOptions ?? MonitoringOptions.Default).Thresholds);
        _clock = clock;
        _isLoading = true;
        _serverService = serverService;
        _serverProfileService = serverProfileService;
        _dialogService = dialogService;
        _editorSession = editorSession;
        _connectionStateStore = connectionStateStore;
        _metricsStore = metricsStore;
        _monitoringStateStore = monitoringStateStore;
        _monitoringEngine = monitoringEngine;
        _discoveryService = discoveryService;
        _navigationService = navigationService;
        _localizationService = localizationService;
        _logger = logger;
        _serverService.ServersChanged += OnServersChanged;
        _connectionStateStore.StateChanged += OnConnectionStateChanged;
        _monitoringStateStore.StateChanged += OnMonitoringStateChanged;
        _discoveryService.DiscoveredChanged += OnDiscoveredChanged;
        _discoveryActivity = discoveryService as IServerDiscoveryActivity;
        if (_discoveryActivity is not null)
        {
            _discoveryActivity.IsSearchingChanged += OnDiscoverySearchingChanged;
        }

        AddServerCommand = new AsyncRelayCommand(AddServerAsync);
        ImportFromSshCommand = new AsyncRelayCommand(ImportFromSshAsync);
        OpenServerDirectoryCommand = new RelayCommand(() =>
        {
            RememberReturnFocus(OverviewReturnTarget.DirectoryLink, Guid.Empty);
            navigationService.GoToServers();
        });
        // Cortex r1 SHOULD-1: the user moving to another page cancels a deep-link still waiting for its server.
        navigationService.NavigatedAwayFromOverview += OnNavigatedAwayFromOverview;
        ClearOverviewSearchCommand = new RelayCommand(() => OverviewSearchText = string.Empty);
        OpenPriorityProblemCommand = new RelayCommand(OpenPriorityProblem);
    }

    public ObservableCollection<ServerCardViewModel> VisibleServers { get; } = [];

    // Lazily created so the view model is robust to partial (constructor-bypassing) construction in the
    // runtime-free contract tests, mirroring the nullable _dispatcherQueue seam above. In production this
    // is realized on first access and then persists for the object's lifetime; PendingServerFocus starts
    // empty, so lazy vs. eager construction is behaviorally identical.
    private PendingServerFocus? _pendingFocus;
    private PendingServerFocus PendingFocus => _pendingFocus ??= new();

    /// <summary>
    /// Raised at the end of every server-list rebuild (load, add/edit/hide/remove, restore), AFTER the new cards are in
    /// <see cref="VisibleServers"/>. Consumers (the Servidores directory, the interim server page) re-resolve here and
    /// never on the collection's own Reset, which fires while the list is momentarily empty.
    /// </summary>
    public event EventHandler? ServersReloaded;

    /// <summary>
    /// A widget "open server" deep-link (<c>serveralyzer://server/{id}</c>, §H). UI.4 D-UI4-DETAIL: it opens the interim
    /// server page for that server. If the server is not (yet) loaded the request stays pending and is retried after the
    /// next load; a removed server simply never resolves, so the app stays on the overview without an error (§11).
    /// </summary>
    public void FocusServer(Guid serverId)
    {
        PendingFocus.Request(serverId);
        TryApplyPendingFocus();
    }

    /// <summary>Clears any pending server focus (a dashboard deep-link supersedes an older server one, §M-3).</summary>
    public void ClearServerFocus() => PendingFocus.Clear();

    /// <summary>True when the server is in the current (visible) list — the interim page is only opened for such a server.</summary>
    public bool HasVisibleServer(Guid serverId) => VisibleServers?.Any(card => card.Server.Id == serverId) == true;

    private void OnNavigatedAwayFromOverview(object? sender, EventArgs e) => ClearServerFocus();

    private void TryApplyPendingFocus()
    {
        if (!PendingFocus.HasPending)
        {
            return;
        }

        var currentIds = VisibleServers.Select(card => card.Server.Id).ToArray();
        if (PendingFocus.TryResolve(currentIds) is { } id)
        {
            OpenServerDetail(id, ServerDetailOrigin.Overview);
        }
    }

    /// <summary>
    /// Beacon r1 SHOULD-1: where keyboard focus returns when the user comes back to the Visão geral - the row, the
    /// priority card or "Ver todos" that opened the page they come from. Taken once by the view on its next Loaded; when
    /// there is none the view focuses the content (never the window-mode button).
    /// </summary>
    public (OverviewReturnTarget Target, Guid ServerId) TakeReturnFocus()
    {
        var target = _returnFocus;
        _returnFocus = (OverviewReturnTarget.None, Guid.Empty);
        return target;
    }

    /// <summary>The Servidores row that opened the interim page, for the next Servidores page to refocus (taken once).</summary>
    public Guid? TakeDirectoryReturnFocus()
    {
        var id = _directoryReturnFocus;
        _directoryReturnFocus = null;
        return id;
    }

    internal void RememberDirectoryReturnFocus(Guid serverId) => _directoryReturnFocus = serverId;

    private void RememberReturnFocus(OverviewReturnTarget target, Guid serverId) => _returnFocus = (target, serverId);

    /// <summary>Opens the interim server page (D-UI4-DETAIL) for a server of the list.</summary>
    public void OpenServerDetail(Guid serverId, ServerDetailOrigin origin)
    {
        var navigation = _navigationService;
        if (navigation is null)
        {
            return;
        }

        // Deferred when a dispatcher exists: a resolution can happen inside a load triggered by a page's Loaded handler,
        // and the frame must not be swapped re-entrantly from there.
        if (_dispatcherQueue is null)
        {
            navigation.GoToServerDetail(serverId, origin);
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => navigation.GoToServerDetail(serverId, origin));
        }
    }

    public ObservableCollection<DiscoveredServerViewModel> DiscoveredServers { get; } = [];

    public ICommand AddServerCommand { get; }

    /// <summary>Empty state "Import from SSH": the normal add editor with the ssh-config import panel open.</summary>
    public ICommand ImportFromSshCommand { get; }


    public bool HasVisibleServers
    {
        get => _hasVisibleServers;
        private set
        {
            if (SetProperty(ref _hasVisibleServers, value))
            {
                OnEmptyStateDiscoveryChanged();
                OnPropertyChanged(nameof(ShowOverviewContent));
                OnPropertyChanged(nameof(ShowEmptyState));
                NotifyEmptyStates();
                OnPropertyChanged(nameof(ShowNoProblems));
                OnPropertyChanged(nameof(ShowNoReadings));
            }
        }
    }

    public bool HasDiscoveredServers
    {
        get => _hasDiscoveredServers;
        private set
        {
            if (SetProperty(ref _hasDiscoveredServers, value))
            {
                OnEmptyStateDiscoveryChanged();
            }
        }
    }

    /// <summary>
    /// Empty dashboard only: discovery is really running and has found nothing yet. False whenever discovery
    /// is disabled, unavailable or replaced by a stand-in — the UI never shows a search that is not happening.
    /// </summary>
    public bool IsSearchingLocalNetwork =>
        !HasVisibleServers && !HasDiscoveredServers && _discoveryActivity?.IsSearching == true;

    /// <summary>Empty dashboard with suggestions: they are listed inside the empty state.</summary>
    public bool ShowEmptyStateDiscoveries => !HasVisibleServers && HasDiscoveredServers;

    /// <summary>The separate "Encontrados na rede" section, exactly as before, once a server exists.</summary>
    public bool ShowDiscoverySection => HasVisibleServers && HasDiscoveredServers;

    private void OnEmptyStateDiscoveryChanged()
    {
        OnPropertyChanged(nameof(IsSearchingLocalNetwork));
        OnPropertyChanged(nameof(ShowEmptyStateDiscoveries));
        OnPropertyChanged(nameof(ShowDiscoverySection));
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
        set
        {
            if (!value)
            {
                OperationErrorServerId = null;
            }

            SetProperty(ref _isOperationErrorOpen, value);
        }
    }

    /// <summary>
    /// UI.5 fix round 2 (Boss decision 3): the server a failed per-server operation (Editar / Ocultar / Remover) was about,
    /// or null for a global error (load, add, discovery). Set together with <see cref="IsOperationErrorOpen"/>, so a Server
    /// Detail shows a server-scoped error only on THAT server's page; global errors keep the UI.4 SHOULD-3 behaviour
    /// (shown everywhere). App-layer bookkeeping only - nothing in Core changes.
    /// Cortex C2 R-2 (accepted): ONE shared notice, so the scope is last-wins - an error about X followed by one about Y
    /// moves the scope to Y; X's Detail then stops showing it while the Visão geral (unscoped) still does.
    /// </summary>
    public Guid? OperationErrorServerId { get; private set; }

    /// <summary>Opens the shared operation error, scoped to <paramref name="serverId"/> (null = global).</summary>
    internal void ReportOperationError(Guid? serverId)
    {
        OperationErrorServerId = serverId;
        if (_isOperationErrorOpen)
        {
            OnPropertyChanged(nameof(IsOperationErrorOpen)); // the scope changed: listeners re-read it
            return;
        }

        IsOperationErrorOpen = true;
        OperationErrorServerId = serverId;
    }

    /// <summary>
    /// A write was refused because a restore holds the configuration (M14.6). Separate from the generic
    /// error: "try again" would be wrong, the change can only be made after ServerAlyzer restarts.
    /// </summary>
    public bool IsConfigurationLockedOpen
    {
        get => _isConfigurationLockedOpen;
        set => SetProperty(ref _isConfigurationLockedOpen, value);
    }

    public string ConfigurationLockedMessage =>
        _localizationService?.GetString(BackupMessageKeys.ConfigurationLocked) ?? string.Empty;

    // --- UI.4 overview -----------------------------------------------------------------------------------------

    public ICommand OpenServerDirectoryCommand { get; } = null!;

    public ICommand ClearOverviewSearchCommand { get; } = null!;

    public ICommand OpenPriorityProblemCommand { get; } = null!;

    /// <summary>The header's round "Atualizar": the existing Refresh All coordinator (engine facade), never a new path.</summary>
    public ICommand RefreshAllCommand => _refreshAllCommand ??= new AsyncRelayCommand(
        RefreshAllAsync,
        () => _refreshAllCoordinator is not null && !_isRefreshingAll);

    /// <summary>True until the first server load has completed (or failed). Additive: false on a constructor-less instance.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowNoProblems));
                OnPropertyChanged(nameof(ShowNoReadings));
                OnPropertyChanged(nameof(ShowOverviewContent));
                OnPropertyChanged(nameof(ShowEmptyState));
                NotifyEmptyStates();
                OnPropertyChanged(nameof(HeaderContextDisplay));
                OnPropertyChanged(nameof(HasHeaderContext));
            }
        }
    }

    /// <summary>The overview's content (health, priority, list) — only once loaded and with at least one visible server.</summary>
    public bool ShowOverviewContent => !IsLoading && HasVisibleServers;

    /// <summary>The empty state ("Adicionar" / "Importar do SSH" / discovery) — only once loaded, never during loading.</summary>
    public bool ShowEmptyState => !IsLoading && !HasVisibleServers;
    private bool _configurationChanged;
    private bool _configurationUnavailable;
    private Task<ServerLoadStatus>? _startupDiagnosis;
    public bool ShowConfigurationUnavailable => !_configurationChanged && _configurationUnavailable;
    public bool ShowFirstServerState => ShowEmptyState && HiddenServerCount == 0 && !ShowConfigurationUnavailable;
    public bool ShowAllHiddenState => ShowEmptyState && HiddenServerCount > 0 && !ShowConfigurationUnavailable;
    private ICommand? _restoreHiddenServersCommand;
    public ICommand RestoreHiddenServersCommand => _restoreHiddenServersCommand ??= new RelayCommand(() => _navigationService.GoToSettings(SettingsSection.Data));
    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(ShowConfigurationUnavailable));
        OnPropertyChanged(nameof(ShowFirstServerState));
        OnPropertyChanged(nameof(ShowAllHiddenState));
    }

    public bool IsRefreshingAll
    {
        get => _isRefreshingAll;
        private set
        {
            if (SetProperty(ref _isRefreshingAll, value))
            {
                _refreshAllCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Configured but hidden servers (restorable in Definições). Drives the Servidores page's note.</summary>
    public int HiddenServerCount
    {
        get => _hiddenServerCount;
        private set { if (SetProperty(ref _hiddenServerCount, value)) NotifyEmptyStates(); }
    }

    public HealthSummary HealthSummary => _healthSummary ?? HealthSummary.Empty;

    public IReadOnlyList<HealthSegment> HealthSegments => _healthSegments ?? [];

    /// <summary>
    /// The rendered health bar (Figma 112:1018): one width per segment inside the fixed 213-wide bar; an aggregated
    /// segment (more than 12 servers) carries its state's count as a tooltip ("496 saudáveis"). Cached per recompute.
    /// </summary>
    public IReadOnlyList<HealthBarSegment> HealthBarSegments => _healthBarSegments ?? [];

    /// <summary>The engine's thresholds (the one MonitoringOptions instance), shared with every row that colours a metric.</summary>
    public ServerMonitor.Core.Monitoring.MonitoringThresholds Thresholds =>
        _prioritySelector?.Thresholds ?? MonitoringOptions.Default.Thresholds;

    /// <summary>"4" of "4 de 6 saudáveis".</summary>
    public string HealthyCountDisplay => HealthSummary.Healthy.ToString(CultureInfo.CurrentUICulture);

    /// <summary>"de 6 saudáveis".</summary>
    public string HealthOfTotalDisplay => Format("OverviewHealthOfTotalFormat", HealthSummary.Total);

    /// <summary>The exception chips, in fixed order, only for states with a count &gt; 0 ("1 atenção", "1 sem ligação").
    /// Cached per recompute (Cortex r1 SHOULD-2): a binding read never allocates.</summary>
    public IReadOnlyList<HealthChip> HealthChips => _healthChips ?? [];

    /// <summary>"4 de 6 servidores saudáveis, 1 em atenção, 1 sem ligação" — one readable sentence for the whole card.</summary>
    public string HealthAutomationName => _healthAutomationName ?? BuildHealthAutomationName(HealthSummary);

    public PriorityProblem? PriorityProblem => _priorityProblem;

    public bool HasPriorityProblem => _priorityProblem is not null;

    /// <summary>
    /// The neutral "Sem problemas" state: loaded, servers present, at least one current reading, and no candidate above
    /// the engine limits. Cortex r1 NIT-3: never next to a Warning/Critical count. Prism (e): with NO current reading
    /// (every server offline or without data) "Sem problemas" would lie — <see cref="ShowNoReadings"/> shows instead.
    /// </summary>
    public bool ShowNoProblems => QuietPriority && _hasReadings;

    /// <summary>Prism (e): "Sem leituras" — nothing to judge yet (every server offline or without data).</summary>
    public bool ShowNoReadings => QuietPriority && !_hasReadings;

    private bool QuietPriority =>
        !IsLoading && HasVisibleServers && _priorityProblem is null && HealthSummary.Warning + HealthSummary.Critical == 0;

    /// <summary>"Disco quase cheio" / "CPU elevada" / "Memória quase cheia".</summary>
    public string? PriorityTitle => _priorityProblem is { } problem ? Text($"OverviewPriorityTitle{problem.Metric}") : null;

    public string? PriorityPercentDisplay => _priorityProblem is { } problem
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", problem.Percent)
        : null;

    public double PriorityPercentValue => _priorityProblem?.Percent ?? 0;

    public ServerHealth PrioritySeverity => _priorityProblem?.Severity ?? ServerHealth.Healthy;

    public PriorityMetric PriorityMetric => _priorityProblem?.Metric ?? PriorityMetric.Disk;

    public string? PriorityServerName => _priorityProblem?.ServerName;

    /// <summary>"Disco quase cheio: 92% em prod-db-01, crítico. Abrir servidor."</summary>
    public string? PriorityAutomationName => _priorityProblem is { } problem
        ? Format(
            "OverviewPriorityAutomationFormat",
            PriorityTitle ?? string.Empty,
            PriorityPercentDisplay ?? string.Empty,
            problem.ServerName,
            Text($"ServerStatus{problem.Severity}"))
        : null;

    /// <summary>"Atualizado há 8 s" from the most recent successful collection; null (hidden) when nothing was collected yet.</summary>
    public string? UpdatedAgoDisplay
    {
        get => _updatedAgoDisplay;
        private set
        {
            if (SetProperty(ref _updatedAgoDisplay, value))
            {
                OnPropertyChanged(nameof(HasUpdatedAgo));
                OnPropertyChanged(nameof(HeaderContextDisplay));
                OnPropertyChanged(nameof(HasHeaderContext));
            }
        }
    }

    public bool HasUpdatedAgo => _updatedAgoDisplay is not null;

    /// <summary>
    /// The line under "Visão geral": "A recolher dados…" while loading (Figma §14 112:15583, the RENDERED text), then
    /// "Atualizado há …" — collapsed when nothing was ever read (Prism h: nothing fabricated).
    /// </summary>
    public string? HeaderContextDisplay => IsLoading ? Text("OverviewCollecting") : _updatedAgoDisplay;

    public bool HasHeaderContext => HeaderContextDisplay is not null;

    /// <summary>The overview list's search ("Procurar"): name or address, partial, case-insensitive.</summary>
    public string OverviewSearchText
    {
        get => _overviewSearchText ?? string.Empty;
        set
        {
            if (SetProperty(ref _overviewSearchText, value ?? string.Empty))
            {
                ApplyOverviewFilter();
            }
        }
    }

    /// <summary>The summary list: the filtered servers, at most <see cref="OverviewPresentation.OverviewListLimit"/>.</summary>
    public IReadOnlyList<ServerDirectoryRowViewModel> OverviewServers => _overviewServers ?? [];

    /// <summary>Matches beyond the list limit, reachable through "Ver todos".</summary>
    public int OverviewMoreCount { get; private set; }

    public bool HasOverviewMore => OverviewMoreCount > 0;

    /// <summary>Prism r1 (b): the explicit footer of a truncated list, "Mais 12 servidores · Ver todos" (no silent cut).</summary>
    public string OverviewMoreDisplay => Format(PluralKey("OverviewMoreServers", OverviewMoreCount), OverviewMoreCount);

    public bool HasOverviewSearchNoResults =>
        HasVisibleServers && !string.IsNullOrWhiteSpace(OverviewSearchText) && OverviewServers.Count == 0;

    // Beacon r1 NIT-4: the same §13 copy as the Servidores page (title + the query in the message).
    public string OverviewNoResultsTitle => Text("ServersNoResultsTitle");

    public string OverviewNoResultsMessage => Format("ServersNoResultsMessageFormat", OverviewSearchText.Trim());

    public async Task LoadAsync()
    {
        try
        {
            var servers = await _serverService.GetAllAsync();
            var all = servers.ToList();
            _configuredEndpoints = BuildConfiguredEndpoints(all);
            HiddenServerCount = all.Count(server => server.IsHidden);
            SetServers(all.Where(server => !server.IsHidden));
            RebuildDiscovered();
            if (!_configurationChanged && _serverService is IServerLoadStatusSource source)
            {
                _startupDiagnosis ??= DiagnoseConfigurationAsync(source);
                _configurationUnavailable = await _startupDiagnosis == ServerLoadStatus.Unavailable;
                NotifyEmptyStates();
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "load servers");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<ServerLoadStatus> DiagnoseConfigurationAsync(IServerLoadStatusSource source)
    {
        try { return await source.GetLoadStatusAsync(); }
        catch (Exception exception)
        {
            _logger?.LogWarning("Configuration diagnosis failed. Type: {Type}.", exception.GetType().Name);
            return ServerLoadStatus.Unavailable;
        }
    }

    private async Task RefreshAllAsync()
    {
        if (_refreshAllCoordinator is null)
        {
            return;
        }

        IsRefreshingAll = true;
        try
        {
            await _refreshAllCoordinator.RefreshAllAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancelled the batch: expected, nothing to report.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The engine records each server's outcome in the monitoring-state store; a failure is reflected there,
            // never as a dashboard error (Cortex r1 NIT-5: Warning, it is not a cancellation).
            _logger?.LogWarning("Refresh all ended early. Exception type: {ExceptionType}.", exception.GetType().Name);
        }
        finally
        {
            IsRefreshingAll = false;
            ScheduleOverview();
        }
    }

    private void OpenPriorityProblem()
    {
        if (_priorityProblem is { } problem)
        {
            RememberReturnFocus(OverviewReturnTarget.Priority, problem.ServerId);
            OpenServerDetail(problem.ServerId, ServerDetailOrigin.Overview);
        }
    }

    /// <summary>
    /// TEST/MEASUREMENT SEAM (Cortex r1 SHOULD-2): replaces the UI dispatcher as the place a coalesced recompute is
    /// queued. Null in production (the dispatcher is used) and in ordinary tests (no dispatcher: recompute at once).
    /// </summary>
    internal Func<Action, bool>? OverviewScheduler { get; set; }

    /// <summary>Runs a pending (coalesced) overview recompute now.</summary>
    internal void FlushOverview()
    {
        if (_overviewDirty)
        {
            RecomputeOverview();
        }
    }

    /// <summary>
    /// Cortex r1 SHOULD-2: an engine cycle publishes one state per server; the overview is recomputed ONCE per burst
    /// (a single queued pass on the UI thread), not once per server. Without a dispatcher (unit tests) it runs at once.
    /// </summary>
    private void ScheduleOverview()
    {
        _overviewDirty = true;
        if (_overviewScheduled)
        {
            return;
        }

        var scheduler = OverviewScheduler;
        if (scheduler is not null)
        {
            _overviewScheduled = scheduler(RunScheduledOverview);
        }
        else if (_dispatcherQueue is not null)
        {
            _overviewScheduled = _dispatcherQueue.TryEnqueue(RunScheduledOverview);
        }

        if (!_overviewScheduled)
        {
            RecomputeOverview();
        }
    }

    private void RunScheduledOverview()
    {
        _overviewScheduled = false;
        FlushOverview();
    }

    /// <summary>
    /// Recomputes every overview aggregate from the cards' engine-owned state: health counts and segments, the priority
    /// problem and "Atualizado há …". Runs on load, after a burst of engine state changes and after "Atualizar" — never
    /// on a timer. Only what actually changed is announced (Cortex r1 SHOULD-2): a routine cycle that re-publishes the
    /// same states raises nothing.
    /// </summary>
    private void RecomputeOverview()
    {
        _overviewDirty = false;
        var cards = VisibleServers;
        if (cards is null)
        {
            return;
        }

        var summary = HealthSummary.From(cards.Select(card => card.Health));
        var selector = _prioritySelector ?? new PriorityProblemSelector(MonitoringOptions.Default.Thresholds);
        var problem = selector.Select(cards
            .Select(card => new PriorityServerInput(card.Server.Id, card.Name, card.Health, card.MetricsSnapshot))
            .ToList());
        var hasReadings = summary.Healthy + summary.Warning + summary.Critical > 0;
        var lastSuccess = cards.Select(card => card.LastSuccessAt).Where(at => at is not null).Max();

        var summaryChanged = _healthSummary is null || summary != _healthSummary;
        var problemChanged = problem != _priorityProblem;
        var readingsChanged = hasReadings != _hasReadings;

        if (summaryChanged)
        {
            _healthSummary = summary;
            _healthSegments = OverviewPresentation.Segments(summary);
            _healthBarSegments = BuildBarSegments(summary);
            _healthChips = BuildHealthChips(summary);
            _healthAutomationName = BuildHealthAutomationName(summary);
            OnPropertyChanged(nameof(HealthSummary));
            OnPropertyChanged(nameof(HealthSegments));
            OnPropertyChanged(nameof(HealthBarSegments));
            OnPropertyChanged(nameof(HealthyCountDisplay));
            OnPropertyChanged(nameof(HealthOfTotalDisplay));
            OnPropertyChanged(nameof(HealthChips));
            OnPropertyChanged(nameof(HealthAutomationName));
        }

        if (problemChanged)
        {
            _priorityProblem = problem;
            OnPropertyChanged(nameof(PriorityProblem));
            OnPropertyChanged(nameof(HasPriorityProblem));
            OnPropertyChanged(nameof(PriorityTitle));
            OnPropertyChanged(nameof(PriorityPercentDisplay));
            OnPropertyChanged(nameof(PriorityPercentValue));
            OnPropertyChanged(nameof(PrioritySeverity));
            OnPropertyChanged(nameof(PriorityMetric));
            OnPropertyChanged(nameof(PriorityServerName));
            OnPropertyChanged(nameof(PriorityAutomationName));
        }

        _hasReadings = hasReadings;
        if (summaryChanged || problemChanged || readingsChanged)
        {
            OnPropertyChanged(nameof(ShowNoProblems));
            OnPropertyChanged(nameof(ShowNoReadings));
        }

        UpdatedAgoDisplay = lastSuccess is { } at ? FormatUpdatedAgo(at) : null;
    }

    private IReadOnlyList<HealthBarSegment> BuildBarSegments(HealthSummary summary)
    {
        var aggregated = summary.Total > OverviewPresentation.MaxDiscreteHealthSegments;
        return OverviewPresentation.BarSegments(summary)
            .Select(segment => aggregated
                ? segment with { ToolTip = Format(PluralKey($"OverviewHealthSegment{segment.Health}", segment.Count), segment.Count) }
                : segment)
            .ToList();
    }

    private IReadOnlyList<HealthChip> BuildHealthChips(HealthSummary summary) => OverviewPresentation.ChipOrder
        .Select(health => (health, count: summary.CountOf(health)))
        .Where(entry => entry.count > 0)
        .Select(entry => new HealthChip(
            entry.health,
            entry.count,
            Format(PluralKey($"OverviewHealthChip{entry.health}", entry.count), entry.count)))
        .ToList();

    private string BuildHealthAutomationName(HealthSummary summary)
    {
        var parts = new List<string> { Format("OverviewHealthAutomationFormat", summary.Healthy, summary.Total) };
        foreach (var health in OverviewPresentation.ChipOrder)
        {
            var count = summary.CountOf(health);
            if (count > 0)
            {
                parts.Add(Format(PluralKey($"OverviewHealthAutomation{health}", count), count));
            }
        }

        return string.Join(", ", parts);
    }

    // D-UI3-9 semantics (same buckets as the Serviços e containers page): <1 s "agora mesmo", then s / min / h / d.
    private string FormatUpdatedAgo(DateTimeOffset lastSuccessUtc)
    {
        var now = (_clock ?? PresentationClock.System).UtcNow;
        var age = now - lastSuccessUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 1)
        {
            return Text("OverviewUpdatedJustNow");
        }

        if (age.TotalMinutes < 1)
        {
            return Format("OverviewUpdatedSecondsFormat", (int)age.TotalSeconds);
        }

        if (age.TotalHours < 1)
        {
            return Format("OverviewUpdatedMinutesFormat", (int)age.TotalMinutes);
        }

        return age.TotalDays < 1
            ? Format("OverviewUpdatedHoursFormat", (int)age.TotalHours)
            : Format("OverviewUpdatedDaysFormat", (int)age.TotalDays);
    }

    private void RebuildOverviewRows()
    {
        if (_overviewRows is { } previous)
        {
            foreach (var row in previous)
            {
                row.Dispose();
            }
        }

        var localization = _localizationService;
        _overviewRows = localization is null
            ? []
            : VisibleServers
                .Select(card => new ServerDirectoryRowViewModel(
                    card,
                    localization,
                    selected =>
                    {
                        RememberReturnFocus(OverviewReturnTarget.ServerRow, selected.Server.Id);
                        OpenServerDetail(selected.Server.Id, ServerDetailOrigin.Overview);
                    },
                    Thresholds))
                .ToList();
        ApplyOverviewFilter();
    }

    private void ApplyOverviewFilter()
    {
        var query = OverviewSearchText;
        var matches = (_overviewRows ?? [])
            .Where(row => OverviewPresentation.MatchesSearch(row.Name, row.Card.Host, row.Card.Port, query))
            .ToList();
        _overviewServers = matches.Take(OverviewPresentation.OverviewListLimit).ToList();
        OverviewMoreCount = Math.Max(0, matches.Count - OverviewPresentation.OverviewListLimit);
        OnPropertyChanged(nameof(OverviewServers));
        OnPropertyChanged(nameof(OverviewMoreCount));
        OnPropertyChanged(nameof(HasOverviewMore));
        OnPropertyChanged(nameof(OverviewMoreDisplay));
        OnPropertyChanged(nameof(HasOverviewSearchNoResults));
        OnPropertyChanged(nameof(OverviewNoResultsTitle));
        OnPropertyChanged(nameof(OverviewNoResultsMessage));
    }

    private string Text(string key) => _localizationService?.GetString(key) ?? key;

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Text(key), args);

    private static string PluralKey(string baseKey, int count) => WorkloadPresentation.PluralKey(baseKey, count);

    public void Dispose()
    {
        _serverService.ServersChanged -= OnServersChanged;
        if (_navigationService is not null)
        {
            _navigationService.NavigatedAwayFromOverview -= OnNavigatedAwayFromOverview;
        }
        _connectionStateStore.StateChanged -= OnConnectionStateChanged;
        _monitoringStateStore.StateChanged -= OnMonitoringStateChanged;
        _discoveryService.DiscoveredChanged -= OnDiscoveredChanged;
        if (_discoveryActivity is not null)
        {
            _discoveryActivity.IsSearchingChanged -= OnDiscoverySearchingChanged;
        }
    }

    private async Task AddServerAsync()
    {
        try
        {
            await _editorSession.OpenAddAsync(PersistAddedServerAsync);
        }
        catch (Exception exception)
        {
            HandleError(exception, "add server");
        }
    }

    private async Task ImportFromSshAsync()
    {
        try
        {
            // A normal add: cancel persists nothing, a save goes through the same profile path.
            await _editorSession.OpenSshImportAsync(PersistAddedServerAsync);
        }
        catch (Exception exception)
        {
            HandleError(exception, "add server from ssh config");
        }
    }

    private async Task AddDiscoveredAsync(DiscoveredServerViewModel discovered)
    {
        try
        {
            // Exactly the normal add flow, only pre-filled: cancel persists nothing; a successful save reaches
            // monitoring solely through ServersChanged.
            await _editorSession.OpenDiscoveryAsync(discovered.ToPrefill(), PersistAddedServerAsync);
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

    /// <summary>
    /// UI.7 B-4: the add write path, unchanged (IServerProfileService.AddAsync, then the transient connection state). The
    /// editor page waits for this answer: a failure (or an exception, incl. ConfigurationLocked) keeps the page and its
    /// form; the session disposes the result. A saved server is listed before the session opens its Detail page.
    /// </summary>
    internal async Task<ServerOperationResult> PersistAddedServerAsync(ServerEditorResult editorResult)
    {
        var result = await _serverProfileService.AddAsync(editorResult.Profile);
        if (result.Succeeded)
        {
            if (editorResult.ConnectionResult is not null)
            {
                _connectionStateStore.Set(result.Server!.Id, editorResult.ConnectionResult);
            }

            await EnsureListedAsync(result.Server!.Id);
        }

        return result;
    }

    private async Task EditServerAsync(Server server)
    {
        try
        {
            await _editorSession.OpenEditAsync(server, editorResult => PersistEditedServerAsync(server, editorResult));
        }
        catch (Exception exception)
        {
            HandleError(exception, "edit server", server.Id);
        }
    }

    /// <summary>UI.7 B-4: the edit write path, unchanged (UpdateAsync, then metrics and connection-state bookkeeping).</summary>
    internal async Task<ServerOperationResult> PersistEditedServerAsync(Server server, ServerEditorResult editorResult)
    {
        var result = await _serverProfileService.UpdateAsync(server, editorResult.Profile);
        if (result.Succeeded)
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

        return result;
    }

    // The list refreshes on ServersChanged (async); the Detail page of a just-added server needs it listed now.
    private async Task EnsureListedAsync(Guid serverId)
    {
        if (!HasVisibleServer(serverId))
        {
            await LoadAsync();
        }
    }

    private async Task HideServerAsync(Server server)
    {
        try
        {
            if (!await _serverService.HideAsync(server.Id))
            {
                ReportOperationError(server.Id);
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "hide server", server.Id);
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
                ReportOperationError(server.Id);
            }
            else
            {
                _connectionStateStore.Remove(server.Id);
                _metricsStore.Remove(server.Id);
            }
        }
        catch (Exception exception)
        {
            HandleError(exception, "remove server", server.Id);
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
                () => RemoveServerAsync(server),
                () =>
                {
                    _navigationService.GoToHistory(server.Id, server.Name);
                    return Task.CompletedTask;
                },
                () =>
                {
                    _navigationService.GoToWorkloads(server.Id, server.Name);
                    return Task.CompletedTask;
                }));
        }

        HasVisibleServers = VisibleServers.Count > 0;
        RebuildOverviewRows();
        RecomputeOverview();
        ServersReloaded?.Invoke(this, EventArgs.Empty);

        // A widget deep-link may have asked to open a server before it was loaded — retry now (§18).
        TryApplyPendingFocus();
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

    // Raised on the discovery service's lifecycle thread; marshal to the UI thread first.
    private void OnDiscoverySearchingChanged(object? sender, EventArgs args)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            OnPropertyChanged(nameof(IsSearchingLocalNetwork));
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => OnPropertyChanged(nameof(IsSearchingLocalNetwork)));
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

    private async void OnServersChanged(object? sender, EventArgs args)
    {
        _configurationChanged = true;
        NotifyEmptyStates();
        await LoadAsync();
    }

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
        if (card is null)
        {
            return;
        }

        card.ApplyMonitoringState(_monitoringStateStore.Get(serverId));
        ScheduleOverview();
    }

    internal void HandleError(Exception exception, string operation, Guid? serverId = null)
    {
        if (exception is ConfigurationLockedException)
        {
            IsConfigurationLockedOpen = true;
            return;
        }

        _logger.LogError(
            "Could not {Operation}. Exception type: {ExceptionType}.",
            operation,
            exception.GetType().Name);
        ReportOperationError(serverId);
    }
}
