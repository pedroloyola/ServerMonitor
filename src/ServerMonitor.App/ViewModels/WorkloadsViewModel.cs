using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.ViewModels;

/// <summary>Which Docker section surface is shown (§44). No infinite spinner: <see cref="Loading"/> is
/// only the pre-first-collection state and is left as soon as an attempt completes.</summary>
public enum DockerViewState
{
    Loading,
    Containers,
    Empty,
    NotInstalled,
    PermissionDenied,
    DaemonUnavailable,
    Error
}

/// <summary>Which Services section surface is shown (§45).</summary>
public enum ServicesViewState
{
    Loading,
    List,
    Empty,
    Unsupported,
    Unavailable,
    Error
}

/// <summary>
/// UI.3 page-wide filter (D-UI3-3) over both lists: "Todos" or "Com problemas" (Negative severity only —
/// transient Warning states such as "A iniciar"/"A reiniciar" are not problems).
/// </summary>
public enum WorkloadGlobalFilter
{
    All,
    Problems
}

/// <summary>
/// Presents one server's read-only workloads (M11): Docker containers and managed services. Like the
/// dashboard, it observes the transient <see cref="IServerWorkloadStore"/> and never runs a timer of its
/// own — the collector/coordinator refresh the snapshot, this VM re-renders on <c>WorkloadChanged</c>.
/// Docker and Services fail independently (§38), freshness is honest (<see cref="IsStale"/> +
/// <see cref="UpdatedAgoDisplay"/>, §39), and search/filter are purely local in-memory over an already
/// collected snapshot — never a new remote shell (§50). Sorting is stable (§49).
/// </summary>
public sealed class WorkloadsViewModel : ObservableObject, IDisposable
{
    private readonly IServerWorkloadStore _workloadStore;
    private readonly IWorkloadRefreshCoordinator _refreshCoordinator;
    private readonly ILocalizationService _localization;
    private readonly ILogger<WorkloadsViewModel> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherQueue? _dispatcherQueue = TryGetDispatcher();
    private readonly AsyncRelayCommand _refreshCommand;

    // The full, sorted projections; the bound collections are the filtered/searched view over these.
    private readonly List<ContainerRowViewModel> _allContainers = [];
    private readonly List<ServiceRowViewModel> _allServices = [];

    private Guid _serverId;
    private int _generation;
    private CancellationTokenSource? _refreshCts;
    private string _title = string.Empty;
    private bool _subscribed;
    private bool _disposed;

    private DockerViewState _dockerState = DockerViewState.Loading;
    private ServicesViewState _servicesState = ServicesViewState.Loading;
    private bool _dockerTruncated;
    private bool _servicesTruncated;
    private bool _isStale;
    private string? _updatedAgoDisplay;
    // UI.3 additions. Null-safe so a runtime-free host that skips field initializers can still read them.
    private readonly IServerMetricsStore? _metricsStore;
    private readonly RelayCommand? _clearSearchCommand;
    private string? _searchText;
    private WorkloadGlobalFilter _globalFilter;
    private ServiceManager _servicesManager;
    private string? _contextDisplay;
    private string? _dockerLifecycleSummary;
    private string? _servicesLifecycleSummary;
    private string? _dockerProblemBadge;
    private string? _servicesProblemBadge;

    // GetForCurrentThread() throws a WinRT COMException in a non-UI/unpackaged host (test runner). Treat
    // that as "no dispatcher" so the VM stays constructible and runs inline there. [mirrors HistoryVM]
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

    public WorkloadsViewModel(
        IServerWorkloadStore workloadStore,
        IWorkloadRefreshCoordinator refreshCoordinator,
        IServerMetricsStore metricsStore,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<WorkloadsViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        _workloadStore = workloadStore ?? throw new ArgumentNullException(nameof(workloadStore));
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
        ArgumentNullException.ThrowIfNull(navigationService);
        _localization = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        // UI.4 (Boss, Beacon r1 SHOULD-4): the server crumb leads to that server's interim page (Visão geral if gone).
        BackCommand = new RelayCommand(() => navigationService.ReturnToServerDetail(_serverId));
        _refreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing);
        _clearSearchCommand = new RelayCommand(ClearSearch);

        RefreshAutomationName = _localization.GetString("WorkloadRefreshButton");
    }

    // --- UI.3 page-wide search + filter (D-UI3-3) ------------------------------------------------------

    /// <summary>
    /// One search over both lists (container name + image, service name + description), D-UI3-3.
    /// </summary>
    public string SearchText
    {
        get => _searchText ?? string.Empty;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_searchText ?? string.Empty, value, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            ApplyContainerView();
            ApplyServiceView();
        }
    }

    public WorkloadGlobalFilter GlobalFilter
    {
        get => _globalFilter;
        set
        {
            if (SetProperty(ref _globalFilter, value))
            {
                OnPropertyChanged(nameof(GlobalFilterIndex));
                ApplyContainerView();
                ApplyServiceView();
            }
        }
    }

    /// <summary>0/1 → Todos/Com problemas, two-way bound to the segmented filter.</summary>
    public int GlobalFilterIndex
    {
        get => (int)_globalFilter;
        set
        {
            if (value is 0 or 1)
            {
                GlobalFilter = (WorkloadGlobalFilter)value;
            }
        }
    }

    /// <summary>Counts are shown once at least one section has a known list (never "Todos · 0" while loading).</summary>
    public bool HasFilterCounts =>
        DockerState is DockerViewState.Containers or DockerViewState.Empty
        || ServicesState is ServicesViewState.List or ServicesViewState.Empty;

    public int AllCount => _allContainers.Count + _allServices.Count;

    /// <summary>Negative severity only (D-UI3-3).</summary>
    public int ProblemCount => _allContainers.Count(c => c.IsProblem) + _allServices.Count(s => s.IsProblem);

    /// <summary>"Todos · 12", or "Todos" before any list is known.</summary>
    public string FilterAllLabel => HasFilterCounts
        ? Format("WorkloadFilterAllFormat", AllCount)
        : _localization?.GetString("WorkloadFilterAll") ?? string.Empty;

    /// <summary>"Com problemas · 2", or "Com problemas" before any list is known.</summary>
    public string FilterProblemsLabel => HasFilterCounts
        ? Format("WorkloadFilterProblemsFormat", ProblemCount)
        : _localization?.GetString("WorkloadFilterProblems") ?? string.Empty;

    /// <summary>A search text or the "Com problemas" filter is narrowing the lists.</summary>
    public bool HasActiveQuery => SearchText.Trim().Length > 0 || _globalFilter != WorkloadGlobalFilter.All;

    /// <summary>
    /// Page-level "Nenhum resultado": a query is active, at least one section has a list, and every
    /// visible list came back empty. Section-only misses use <see cref="ShowDockerSectionNoResults"/> /
    /// <see cref="ShowServicesSectionNoResults"/>.
    /// </summary>
    public bool ShowGlobalNoResults =>
        HasActiveQuery
        && (ShowDockerContainers || ShowServicesList)
        && (!ShowDockerContainers || Containers.Count == 0)
        && (!ShowServicesList || Services.Count == 0);

    /// <summary>"Sem resultados nesta secção" inside the Containers card (the other card still has rows).</summary>
    public bool ShowDockerSectionNoResults => ShowDockerNoResults && !ShowGlobalNoResults;

    /// <summary>"Sem resultados nesta secção" inside the Services card (the other card still has rows).</summary>
    public bool ShowServicesSectionNoResults => ShowServicesNoResults && !ShowGlobalNoResults;

    /// <summary>"Nenhum resultado para “redis”", or "Nenhum problema encontrado" for the filter alone.</summary>
    public string NoResultsTitle
    {
        get
        {
            var search = SearchText.Trim();
            return search.Length > 0
                ? string.Format(CultureInfo.CurrentUICulture, _localization?.GetString("WorkloadNoResultsTitleFormat") ?? "{0}", search)
                : _localization?.GetString("WorkloadNoProblemsTitle") ?? string.Empty;
        }
    }

    /// <summary>"Limpar pesquisa": clears the search AND resets the filter(s) to Todos.</summary>
    public ICommand ClearSearchCommand => _clearSearchCommand ?? new RelayCommand(ClearSearch);

    // --- UI.3 header context + section summaries --------------------------------------------------------

    /// <summary>
    /// "prod-web-01 · Ubuntu 24.04 LTS · Atualizado há 8 s" (D-UI3-9: seconds/minutes, no timer). The OS is
    /// omitted when unknown; "Falha na última consulta" is appended when the list is stale and replaces the
    /// freshness when nothing could be read; "A consultar serviços e containers…" before the first attempt.
    /// </summary>
    public string ContextDisplay => _contextDisplay ?? string.Empty;

    /// <summary>"Docker · 5 em execução · 1 parado" — by lifecycle, not severity; empty without a list.</summary>
    public string DockerLifecycleSummary => _dockerLifecycleSummary ?? string.Empty;

    /// <summary>"systemd · 5 ativos · 1 falha" — manager from <see cref="ServiceSnapshot.Manager"/>.</summary>
    public string ServicesLifecycleSummary => _servicesLifecycleSummary ?? string.Empty;

    /// <summary>"1 problema" / "N problemas" for the Containers card; null when none.</summary>
    public string? DockerProblemBadge => _dockerProblemBadge;

    public bool HasDockerProblems => _dockerProblemBadge is not null;

    /// <summary>"1 problema" / "N problemas" for the Services card; null when none.</summary>
    public string? ServicesProblemBadge => _servicesProblemBadge;

    public bool HasServicesProblems => _servicesProblemBadge is not null;

    /// <summary>Page panel "Não foi possível obter os serviços": both sections failed to be read.</summary>
    public bool ShowAllUnavailable =>
        DockerState == DockerViewState.Error && ServicesState == ServicesViewState.Error;

    /// <summary>Page-level loading panel (112:16645): no collection attempt has completed for either section yet.</summary>
    public bool ShowPageLoading => DockerState == DockerViewState.Loading && ServicesState == ServicesViewState.Loading;

    /// <summary>The two section cards are shown unless a page-level panel (loading / unavailable / nothing / no results) replaces them.</summary>
    public bool ShowSectionCards => !ShowPageLoading && !ShowAllUnavailable && !ShowNothingToShow && !ShowGlobalNoResults;

    /// <summary>Page panel "Nada para apresentar": both reads succeeded and found nothing.</summary>
    public bool ShowNothingToShow =>
        DockerState == DockerViewState.Empty && ServicesState == ServicesViewState.Empty;

    public ICommand BackCommand { get; }

    public ICommand RefreshCommand => _refreshCommand;

    public string RefreshAutomationName { get; }

    public ObservableCollection<ContainerRowViewModel> Containers { get; } = [];

    public ObservableCollection<ServiceRowViewModel> Services { get; } = [];

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    private bool _isRefreshing;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                _refreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // --- Freshness (§39) ---------------------------------------------------------------------------

    public bool IsStale
    {
        get => _isStale;
        private set
        {
            if (SetProperty(ref _isStale, value))
            {
                OnPropertyChanged(nameof(ShowStaleNotice));
            }
        }
    }

    /// <summary>"Atualizado há …" for the last <b>fresh</b> collection; <c>null</c> before any collection.</summary>
    public string? UpdatedAgoDisplay
    {
        get => _updatedAgoDisplay;
        private set
        {
            if (SetProperty(ref _updatedAgoDisplay, value))
            {
                OnPropertyChanged(nameof(HasFreshness));
                // ShowStaleNotice also depends on HasFreshness, so it must be re-evaluated here — otherwise
                // a stale snapshot whose IsStale is applied before the freshness text leaves the badge hidden.
                OnPropertyChanged(nameof(ShowStaleNotice));
            }
        }
    }

    public bool HasFreshness => _updatedAgoDisplay is not null;

    public bool ShowStaleNotice => IsStale && HasFreshness;

    // --- Docker section (§44) ----------------------------------------------------------------------

    public DockerViewState DockerState
    {
        get => _dockerState;
        private set
        {
            if (SetProperty(ref _dockerState, value))
            {
                RaiseDockerVisibility();
            }
        }
    }

    public bool ShowDockerLoading => DockerState == DockerViewState.Loading;

    public bool ShowDockerContainers => DockerState == DockerViewState.Containers;

    public bool ShowDockerEmpty => DockerState == DockerViewState.Empty;

    public bool ShowDockerNotInstalled => DockerState == DockerViewState.NotInstalled;

    public bool ShowDockerPermissionDenied => DockerState == DockerViewState.PermissionDenied;

    public bool ShowDockerUnavailable => DockerState == DockerViewState.DaemonUnavailable;

    public bool ShowDockerError => DockerState == DockerViewState.Error;

    /// <summary>The container list exists but the current search/filter matched nothing.</summary>
    public bool ShowDockerNoResults => ShowDockerContainers && Containers.Count == 0;

    public bool ShowDockerTruncatedNotice => ShowDockerContainers && _dockerTruncated;

    // --- Services section (§45) --------------------------------------------------------------------

    public ServicesViewState ServicesState
    {
        get => _servicesState;
        private set
        {
            if (SetProperty(ref _servicesState, value))
            {
                RaiseServicesVisibility();
            }
        }
    }

    public bool ShowServicesLoading => ServicesState == ServicesViewState.Loading;

    public bool ShowServicesList => ServicesState == ServicesViewState.List;

    public bool ShowServicesEmpty => ServicesState == ServicesViewState.Empty;

    public bool ShowServicesUnsupported => ServicesState == ServicesViewState.Unsupported;

    public bool ShowServicesUnavailable => ServicesState == ServicesViewState.Unavailable;

    public bool ShowServicesError => ServicesState == ServicesViewState.Error;

    public bool ShowServicesNoResults => ShowServicesList && Services.Count == 0;

    public bool ShowServicesTruncatedNotice => ShowServicesList && _servicesTruncated;

    /// <summary>
    /// Binds the VM to a server and renders its current snapshot. Called on the UI thread. Each call
    /// increments a generation and best-effort-cancels any in-flight refresh (M-02, §76): a refresh started
    /// for a previous server can no longer drive this VM's spinner or overwrite the new server's view — the
    /// generation is the guarantee, the cancellation is only an optimization. Resetting
    /// <see cref="IsRefreshing"/> here is what stops a pending refresh from leaving the new server stuck
    /// with the spinner on, since that stale refresh's finally is now gated out by the generation check.
    /// </summary>
    public void Load(Guid serverId, string serverName)
    {
        Interlocked.Increment(ref _generation);
        _refreshCts?.Cancel();
        IsRefreshing = false;

        _serverId = serverId;
        Title = serverName;

        if (!_subscribed)
        {
            _workloadStore.WorkloadChanged += OnWorkloadChanged;
            _subscribed = true;
        }

        Apply(_workloadStore.Get(serverId));
    }

    private async Task RefreshAsync()
    {
        // Capture the target and generation before awaiting so a Load() to another server mid-refresh can
        // neither be overwritten by this result nor have its spinner controlled by this completion.
        var serverId = _serverId;
        var generation = Volatile.Read(ref _generation);

        _refreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;

        IsRefreshing = true;
        try
        {
            await _refreshCoordinator.RefreshNowAsync(serverId, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer Load(); ignore.
        }
        catch (Exception exception)
        {
            // The coordinator records the outcome in the store (encoded as availability); reflect it below.
            _logger.LogError("Workload refresh failed. Type: {Type}.", exception.GetType().Name);
        }
        finally
        {
            if (!_disposed && generation == Volatile.Read(ref _generation))
            {
                IsRefreshing = false;
                Apply(_workloadStore.Get(serverId));
            }
        }
    }

    private void OnWorkloadChanged(object? sender, Guid serverId)
    {
        if (_disposed || serverId != _serverId)
        {
            return;
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            Apply(_workloadStore.Get(_serverId));
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => Apply(_workloadStore.Get(_serverId)));
        }
    }

    private void Apply(ServerWorkloadSnapshot? snapshot)
    {
        if (_disposed)
        {
            return;
        }

        ApplyFreshness(snapshot);
        ApplyDocker(snapshot);
        ApplyServices(snapshot);
        ApplyUi3Summaries();
        ApplyContext(snapshot);
    }

    private void ApplyContext(ServerWorkloadSnapshot? snapshot)
    {
        string context;
        if (snapshot is null || snapshot.LastAttemptAtUtc is null)
        {
            context = ServerContextPresentation.Join(Title, _localization.GetString("WorkloadContextLoading"));
        }
        else if (ShowAllUnavailable)
        {
            context = ServerContextPresentation.Join(Title, _localization.GetString("WorkloadContextLastQueryFailed"));
        }
        else
        {
            var os = ServerContextPresentation.OperatingSystemDisplay(_metricsStore?.GetLastSnapshot(_serverId));
            context = ServerContextPresentation.Join(
                Title,
                os,
                UpdatedAgoDisplay,
                IsStale ? _localization.GetString("WorkloadContextLastQueryFailed") : null);
        }

        if (!string.Equals(_contextDisplay, context, StringComparison.Ordinal))
        {
            _contextDisplay = context;
            OnPropertyChanged(nameof(ContextDisplay));
        }
    }

    private void ApplyUi3Summaries()
    {
        _dockerLifecycleSummary = DockerState == DockerViewState.Containers
            ? LifecycleSummary(
                _localization.GetString("WorkloadManagerDocker"),
                _allContainers.Select(c => WorkloadPresentation.ContainerLifecycleKey(c.State)),
                WorkloadPresentation.ContainerLifecycleOrder)
            : string.Empty;
        _servicesLifecycleSummary = ServicesState == ServicesViewState.List
            ? LifecycleSummary(
                _localization.GetString($"WorkloadManager{_servicesManager}"),
                _allServices.Select(s => WorkloadPresentation.ServiceLifecycleKey(s.State)),
                WorkloadPresentation.ServiceLifecycleOrder)
            : string.Empty;
        _dockerProblemBadge = ProblemBadge(_allContainers.Count(c => c.IsProblem));
        _servicesProblemBadge = ProblemBadge(_allServices.Count(s => s.IsProblem));

        OnPropertyChanged(nameof(DockerLifecycleSummary));
        OnPropertyChanged(nameof(ServicesLifecycleSummary));
        OnPropertyChanged(nameof(DockerProblemBadge));
        OnPropertyChanged(nameof(HasDockerProblems));
        OnPropertyChanged(nameof(ServicesProblemBadge));
        OnPropertyChanged(nameof(HasServicesProblems));
        OnPropertyChanged(nameof(HasFilterCounts));
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(FilterAllLabel));
        OnPropertyChanged(nameof(FilterProblemsLabel));
        OnPropertyChanged(nameof(ShowAllUnavailable));
        OnPropertyChanged(nameof(ShowNothingToShow));
        RaiseNoResults();
    }

    private string LifecycleSummary(string manager, IEnumerable<string> keys, IReadOnlyList<string> order)
    {
        var counts = keys.GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        var segments = new List<string> { manager };
        foreach (var key in order)
        {
            if (counts.TryGetValue(key, out var count) && count > 0)
            {
                segments.Add(Format(WorkloadPresentation.PluralKey(key, count), count));
            }
        }

        return string.Join(" · ", segments);
    }

    private string? ProblemBadge(int problems) =>
        problems > 0 ? Format(WorkloadPresentation.PluralKey("WorkloadProblemBadge", problems), problems) : null;

    private void ClearSearch()
    {
        SearchText = string.Empty;
        GlobalFilter = WorkloadGlobalFilter.All;
    }

    private void RaiseNoResults()
    {
        OnPropertyChanged(nameof(ShowPageLoading));
        OnPropertyChanged(nameof(ShowSectionCards));
        OnPropertyChanged(nameof(HasActiveQuery));
        OnPropertyChanged(nameof(ShowGlobalNoResults));
        OnPropertyChanged(nameof(ShowDockerSectionNoResults));
        OnPropertyChanged(nameof(ShowServicesSectionNoResults));
        OnPropertyChanged(nameof(NoResultsTitle));
    }

    private void ApplyFreshness(ServerWorkloadSnapshot? snapshot)
    {
        // Freshness is only meaningful once a real collection attempt has happened; the initial placeholder
        // snapshot (LastAttemptAtUtc == null) must not read as "just updated".
        if (snapshot is null || snapshot.LastAttemptAtUtc is null)
        {
            UpdatedAgoDisplay = null;
            IsStale = false;
            return;
        }

        IsStale = snapshot.IsStale;
        UpdatedAgoDisplay = FormatUpdatedAgo(snapshot.CapturedAtUtc);
    }

    private string FormatUpdatedAgo(DateTimeOffset capturedAtUtc)
    {
        var age = _timeProvider.GetUtcNow() - capturedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 1)
        {
            return _localization.GetString("WorkloadUpdatedJustNow");
        }

        // D-UI3-9: whole seconds under a minute ("Atualizado há 8 s", Figma 112:2613). Still no timer: the label is
        // recomputed only when a new snapshot / refresh arrives (Apply), never on a tick.
        if (age.TotalMinutes < 1)
        {
            return Format("WorkloadUpdatedSecondsFormat", (int)age.TotalSeconds);
        }

        if (age.TotalHours < 1)
        {
            return Format("WorkloadUpdatedMinutesFormat", (int)age.TotalMinutes);
        }

        if (age.TotalDays < 1)
        {
            return Format("WorkloadUpdatedHoursFormat", (int)age.TotalHours);
        }

        return Format("WorkloadUpdatedDaysFormat", (int)age.TotalDays);
    }

    private void ApplyDocker(ServerWorkloadSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.LastAttemptAtUtc is null)
        {
            _allContainers.Clear();
            Containers.Clear();
            _dockerTruncated = false;
            DockerState = DockerViewState.Loading;
            RaiseDockerVisibility();
            return;
        }

        var docker = snapshot.Docker;
        _dockerTruncated = docker.Truncated;

        DockerState = docker.Availability switch
        {
            DockerAvailability.Available => docker.Containers.Count > 0
                ? DockerViewState.Containers
                : DockerViewState.Empty,
            DockerAvailability.NotInstalled => DockerViewState.NotInstalled,
            DockerAvailability.PermissionDenied => DockerViewState.PermissionDenied,
            DockerAvailability.Unavailable => DockerViewState.DaemonUnavailable,
            // Unknown after an attempt means the SSH session itself did not complete → treat as an error.
            _ => DockerViewState.Error
        };

        _allContainers.Clear();
        if (DockerState == DockerViewState.Containers)
        {
            // Stable sort (§49): running first, then by name. OrderBy/ThenBy is a stable sort.
            _allContainers.AddRange(docker.Containers
                .OrderBy(c => WorkloadPresentation.ContainerSortRank(c.State))
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new ContainerRowViewModel(c, _localization)));
        }

        ApplyContainerView();
        RaiseDockerVisibility();
    }

    private void ApplyServices(ServerWorkloadSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.LastAttemptAtUtc is null)
        {
            _allServices.Clear();
            Services.Clear();
            _servicesTruncated = false;
            ServicesState = ServicesViewState.Loading;
            RaiseServicesVisibility();
            return;
        }

        var services = snapshot.Services;
        _servicesTruncated = services.Truncated;
        _servicesManager = services.Manager;

        // No supported manager (unknown OS, non-systemd Linux) is "unsupported" regardless of the
        // availability code — there is simply nothing to read here (§45).
        if (services.Manager == ServiceManager.Unsupported
            || services.Availability == WorkloadServiceAvailability.NotInstalled)
        {
            ServicesState = ServicesViewState.Unsupported;
        }
        else
        {
            ServicesState = services.Availability switch
            {
                WorkloadServiceAvailability.Available => services.Services.Count > 0
                    ? ServicesViewState.List
                    : ServicesViewState.Empty,
                WorkloadServiceAvailability.PermissionDenied => ServicesViewState.Unavailable,
                WorkloadServiceAvailability.Unavailable => ServicesViewState.Unavailable,
                _ => ServicesViewState.Error
            };
        }

        _allServices.Clear();
        if (ServicesState == ServicesViewState.List)
        {
            // Stable sort (§49): failed first → running → others, then by name.
            _allServices.AddRange(services.Services
                .OrderBy(s => WorkloadPresentation.ServiceSortRank(s.State))
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(s => new ServiceRowViewModel(s, _localization)));
        }

        ApplyServiceView();
        RaiseServicesVisibility();
    }

    private void ApplyContainerView()
    {
        var search = SearchText.Trim();
        IEnumerable<ContainerRowViewModel> view = _allContainers;

        if (_globalFilter == WorkloadGlobalFilter.Problems)
        {
            view = view.Where(c => c.IsProblem);
        }

        if (search.Length > 0)
        {
            view = view.Where(c => WorkloadPresentation.Matches(search, c.Name, c.Image));
        }

        ReplaceAll(Containers, view);
        OnPropertyChanged(nameof(ShowDockerNoResults));
        RaiseNoResults();
    }

    private void ApplyServiceView()
    {
        var search = SearchText.Trim();
        IEnumerable<ServiceRowViewModel> view = _allServices;

        if (_globalFilter == WorkloadGlobalFilter.Problems)
        {
            view = view.Where(s => s.IsProblem);
        }

        if (search.Length > 0)
        {
            view = view.Where(s => WorkloadPresentation.Matches(search, s.Name, s.Description));
        }

        ReplaceAll(Services, view);
        OnPropertyChanged(nameof(ShowServicesNoResults));
        RaiseNoResults();
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void RaiseDockerVisibility()
    {
        OnPropertyChanged(nameof(ShowDockerLoading));
        OnPropertyChanged(nameof(ShowDockerContainers));
        OnPropertyChanged(nameof(ShowDockerEmpty));
        OnPropertyChanged(nameof(ShowDockerNotInstalled));
        OnPropertyChanged(nameof(ShowDockerPermissionDenied));
        OnPropertyChanged(nameof(ShowDockerUnavailable));
        OnPropertyChanged(nameof(ShowDockerError));
        OnPropertyChanged(nameof(ShowDockerNoResults));
        OnPropertyChanged(nameof(ShowDockerTruncatedNotice));
    }

    private void RaiseServicesVisibility()
    {
        OnPropertyChanged(nameof(ShowServicesLoading));
        OnPropertyChanged(nameof(ShowServicesList));
        OnPropertyChanged(nameof(ShowServicesEmpty));
        OnPropertyChanged(nameof(ShowServicesUnsupported));
        OnPropertyChanged(nameof(ShowServicesUnavailable));
        OnPropertyChanged(nameof(ShowServicesError));
        OnPropertyChanged(nameof(ShowServicesNoResults));
        OnPropertyChanged(nameof(ShowServicesTruncatedNotice));
    }

    private string Format(string key, int value) =>
        string.Format(CultureInfo.CurrentUICulture, _localization?.GetString(key) ?? "{0}", value);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Bump the generation so any in-flight refresh's finally is gated out, and cancel best-effort.
        Interlocked.Increment(ref _generation);
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        if (_subscribed)
        {
            _workloadStore.WorkloadChanged -= OnWorkloadChanged;
            _subscribed = false;
        }
    }
}
