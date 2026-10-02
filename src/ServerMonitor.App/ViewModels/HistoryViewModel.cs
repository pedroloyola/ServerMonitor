using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// Why the History screen is empty (D-UI3-10). <see cref="NeverRecorded"/> = nothing stored for the
/// server within retention ("O histórico começa aqui"); <see cref="Period"/> = readings exist, just not
/// in the selected range ("Sem leituras neste período").
/// </summary>
public enum HistoryEmptyKind
{
    None,
    NeverRecorded,
    Period
}

/// <summary>
/// Presents one server's local history. It never touches SQL or the store directly: it asks
/// <see cref="IServerHistoryQueryService"/> for a downsampled, chart-ready result and binds it
/// (ADR-015 §8). Range switches are race-safe (spec §50/§51/§80): every selection increments a
/// generation and cancels the previous query, and a late response from a superseded range is
/// discarded — a slow 30d reply can never overwrite a newer 1h selection. The "current" value shown
/// on each chart comes from live state, not the last history row (spec §47).
/// </summary>
public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    private static readonly HistoryTimeRange[] Ranges =
    [
        HistoryTimeRange.LastHour,
        HistoryTimeRange.Last6Hours,
        HistoryTimeRange.Last24Hours,
        HistoryTimeRange.Last7Days,
        HistoryTimeRange.Last30Days
    ];

    private static readonly TimeSpan LiveRefreshThrottle = TimeSpan.FromSeconds(20);

    private readonly IServerHistoryQueryService _queryService;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IServerMonitoringStateStore _monitoringStateStore;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherQueue? _dispatcherQueue = TryGetDispatcher();

    // GetForCurrentThread() throws a WinRT COMException in a non-UI/unpackaged host (e.g. the test
    // runner). Treat that as "no dispatcher" so the VM stays constructible and runs inline there,
    // while capturing the real UI dispatcher in the app for live-refresh marshalling. [P-010, L-016]
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

    private int _generation;
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastLiveRefreshUtc = DateTimeOffset.MinValue;
    private bool _subscribed;
    private bool _disposed;

    private Guid _serverId;
    private string _title = string.Empty;
    private int _selectedRangeIndex = 2; // Last24Hours by default.
    private bool _isLoading;
    private bool _isUnavailable;
    private bool _isEmpty;
    private bool _hasOfflinePeriods;
    private HistorySeries? _cpuSeries;
    private HistorySeries? _memorySeries;
    private HistorySeries? _diskSeries;
    private DateTimeOffset _rangeStartUtc;
    private DateTimeOffset _rangeEndUtc;
    private string _cpuCurrentDisplay = "—";
    private string _memoryCurrentDisplay = "—";
    private string _diskCurrentDisplay = "—";
    private string _cpuSummary = string.Empty;
    private string _memorySummary = string.Empty;
    private string _diskSummary = string.Empty;

    // UI.3 additions. All null-safe: a runtime-free test host that skips field initializers must still
    // be able to read every property without a NullReferenceException.
    private readonly IServerService? _serverService;
    private int _serversGeneration;
    private bool _serversRequested;
    private HistoryServerOptionViewModel? _selectedServer;
    private HistoryEmptyKind _emptyKind;
    private string? _cpuCurrentValue;
    private string? _memoryCurrentValue;
    private string? _diskCurrentValue;
    private string? _cpuPeakDisplay;
    private string? _memoryPeakDisplay;
    private string? _diskPeakDisplay;
    private string? _periodFooter;
    private IReadOnlyList<HistoryAxisTick>? _xAxisTicks;
    private IReadOnlyList<HistoryAxisTick>? _xAxisTicksCompact;
    private CultureInfo? _formatCulture;
    private string? _formatCultureKey;

    public HistoryViewModel(
        IServerHistoryQueryService queryService,
        IServerMetricsStore metricsStore,
        IServerMonitoringStateStore monitoringStateStore,
        IServerService serverService,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<HistoryViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
        _monitoringStateStore = monitoringStateStore ?? throw new ArgumentNullException(nameof(monitoringStateStore));
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        ArgumentNullException.ThrowIfNull(navigationService);
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        // UI.4 (Boss, Beacon r1 SHOULD-4): back / "Ver servidor" lead to the interim page of the server shown here
        // (the Visão geral when it is no longer listed). Replaced by the server Detail in UI.5.
        BackCommand = new RelayCommand(() => navigationService.ReturnToServerDetail(_serverId));
        ViewServerCommand = new RelayCommand(() => navigationService.ReturnToServerDetail(_serverId));
        ViewLast30DaysCommand = new RelayCommand(() => SelectedRangeIndex = Ranges.Length - 1);

        CpuTitle = localizationService.GetString("HistoryMetricCpu");
        MemoryTitle = localizationService.GetString("HistoryMetricMemory");
        DiskTitle = localizationService.GetString("HistoryMetricDisk");
        CurrentLabel = localizationService.GetString("HistoryCurrentLabel");
        PercentUnit = localizationService.GetString("HistoryPercentUnit");
        // D-UI3-11: fixed copy, pinned by a test to HistoryStorageOptions' default retention (30 days).
        RetentionNotice = localizationService.GetString("HistoryRetentionNotice");
        YAxisLabels = HistoryPresentation.YAxisLabels(FormatCulture);
    }

    public ICommand BackCommand { get; }

    /// <summary>"Ver servidor" in the never-recorded state (D-UI3-5: Dashboard until UI.5).</summary>
    public ICommand ViewServerCommand { get; }

    /// <summary>"Ver últimos 30 dias" in the empty-period state: selects the 30-day range.</summary>
    public ICommand ViewLast30DaysCommand { get; }

    // --- Server selector (UI.3) ----------------------------------------------------------------------

    /// <summary>Visible (non-hidden) servers in Dashboard order, for the in-page server selector.</summary>
    public ObservableCollection<HistoryServerOptionViewModel> Servers { get; } = [];

    /// <summary>
    /// Two-way bound to the selector. Picking another server reloads this page in place through
    /// <see cref="Load"/>, which is race-safe by generation, so a slow reply for the previous server can
    /// never overwrite the new one.
    /// </summary>
    public HistoryServerOptionViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedServer))
            {
                return;
            }

            var previous = _selectedServer;
            _selectedServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedServerSubtitle));
            UpdateSelectorLoadingLine(previous);
            if (value.Id != _serverId)
            {
                Load(value.Id, value.Name);
            }
        }
    }

    /// <summary>The selector's second line: "A carregar histórico…" while loading, else the server subtitle.</summary>
    public string SelectedServerSubtitle => IsLoading
        ? Text("HistorySelectorLoading")
        : _selectedServer?.Subtitle ?? string.Empty;

    // --- Chart text (UI.3) -----------------------------------------------------------------------------

    /// <summary>"Atual" under each chart's big value.</summary>
    public string CurrentLabel { get; } = string.Empty;

    /// <summary>The "%" unit shown beside a known current value.</summary>
    public string PercentUnit { get; } = string.Empty;

    /// <summary>"Guardado neste dispositivo durante 30 dias." (D-UI3-11).</summary>
    public string RetentionNotice { get; } = string.Empty;

    /// <summary>Big current value without the unit ("24"), or "—" when unknown — never "0".</summary>
    public string CpuCurrentValue => _cpuCurrentValue ?? Unknown;

    public string MemoryCurrentValue => _memoryCurrentValue ?? Unknown;

    public string DiskCurrentValue => _diskCurrentValue ?? Unknown;

    /// <summary>True when the current value is known, i.e. the "%" unit should be shown.</summary>
    public bool HasCpuCurrent => _cpuCurrentValue is not null;

    public bool HasMemoryCurrent => _memoryCurrentValue is not null;

    public bool HasDiskCurrent => _diskCurrentValue is not null;

    /// <summary>"Pico no período 78%" from the raw <see cref="HistorySeries.Maximum"/>; "—" when unknown.</summary>
    public string CpuPeakDisplay => _cpuPeakDisplay ?? string.Empty;

    public string MemoryPeakDisplay => _memoryPeakDisplay ?? string.Empty;

    public string DiskPeakDisplay => _diskPeakDisplay ?? string.Empty;

    /// <summary>"Últimas 24 horas · 28–29 set 2026"; empty until a range has been loaded.</summary>
    public string PeriodFooter => _periodFooter ?? string.Empty;

    /// <summary>Five X-axis labels at the quartiles of the loaded range (D-UI3-4).</summary>
    public IReadOnlyList<string> XAxisLabels => XAxisTicks.Select(tick => tick.Label).ToArray();

    /// <summary>Up to five X marks on round boundaries at their real position (D-UI3-4 revised).</summary>
    public IReadOnlyList<HistoryAxisTick> XAxisTicks => _xAxisTicks ?? [];

    /// <summary>Up to three X marks for narrow layouts (D-UI3-4).</summary>
    public IReadOnlyList<HistoryAxisTick> XAxisTicksCompact => _xAxisTicksCompact ?? [];

    /// <summary>Three X-axis labels (start/middle/end) for narrow layouts (D-UI3-4).</summary>
    public IReadOnlyList<string> XAxisLabelsCompact => XAxisTicksCompact.Select(tick => tick.Label).ToArray();

    /// <summary>Fixed Y axis "100", "50", "0" (top to bottom).</summary>
    public IReadOnlyList<string> YAxisLabels { get; } = [];

    // --- Empty states (D-UI3-10) -----------------------------------------------------------------------

    public HistoryEmptyKind EmptyKind
    {
        get => _emptyKind;
        private set
        {
            if (SetProperty(ref _emptyKind, value))
            {
                RaiseVisibility();
            }
        }
    }

    /// <summary>"O histórico começa aqui": nothing stored for this server within retention.</summary>
    public bool ShowEmptyNeverRecorded => ShowEmpty && EmptyKind == HistoryEmptyKind.NeverRecorded;

    /// <summary>"Sem leituras neste período": readings exist, just not in the selected range.</summary>
    public bool ShowEmptyPeriod => ShowEmpty && EmptyKind == HistoryEmptyKind.Period;

    /// <summary>The "Ver últimos 30 dias" action; hidden when 30 days is already selected.</summary>
    public bool ShowViewLast30Days => ShowEmptyPeriod && _selectedRangeIndex != Ranges.Length - 1;

    public string EmptyTitle => EmptyKind switch
    {
        HistoryEmptyKind.NeverRecorded => Text("HistoryEmptyNeverTitle"),
        HistoryEmptyKind.Period => Text("HistoryEmptyPeriodTitle"),
        _ => string.Empty
    };

    public string EmptyMessage => EmptyKind switch
    {
        HistoryEmptyKind.NeverRecorded => Text("HistoryEmptyNeverMessage"),
        HistoryEmptyKind.Period => Text("HistoryEmptyPeriodMessage"),
        _ => string.Empty
    };

    public string EmptyActionText => EmptyKind switch
    {
        HistoryEmptyKind.NeverRecorded => Text("HistoryEmptyNeverAction"),
        HistoryEmptyKind.Period => Text("HistoryEmptyPeriodAction"),
        _ => string.Empty
    };

    private string Unknown => _localizationService?.GetString("HistoryValueUnknown") ?? "—";

    // Cortex L-3: every UI.3 text reader goes through here, so a runtime-free host (field initializers skipped) never
    // dereferences a null service.
    private string Text(string key) => _localizationService?.GetString(key) ?? string.Empty;

    // Cortex L-4: ONE culture for every History text - numbers, peaks, summaries, axes and footer - the explicit UI
    // language when set (so it matches the resw copy), else the current UI culture. Cached per language.
    private CultureInfo FormatCulture =>
        _formatCulture is { } cached && string.Equals(_formatCultureKey, _localizationService?.CurrentLanguageOverride, StringComparison.Ordinal)
            ? cached
            : CacheFormatCulture();

    private CultureInfo CacheFormatCulture()
    {
        _formatCultureKey = _localizationService?.CurrentLanguageOverride;
        _formatCulture = HistoryPresentation.FormatCulture(_formatCultureKey);
        return _formatCulture;
    }

    public string CpuTitle { get; }

    public string MemoryTitle { get; }

    public string DiskTitle { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>0..4 → 1h/6h/24h/7d/30d. Two-way bound to the range selector; setting it reloads.</summary>
    public int SelectedRangeIndex
    {
        get => _selectedRangeIndex;
        set
        {
            if (value < 0 || value >= Ranges.Length || !SetProperty(ref _selectedRangeIndex, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowViewLast30Days));
            _ = LoadRangeAsync(Ranges[value]);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseVisibility();
                OnPropertyChanged(nameof(SelectedServerSubtitle));
                UpdateSelectorLoadingLine();
            }
        }
    }

    public bool IsUnavailable
    {
        get => _isUnavailable;
        private set
        {
            if (SetProperty(ref _isUnavailable, value))
            {
                RaiseVisibility();
            }
        }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (SetProperty(ref _isEmpty, value))
            {
                RaiseVisibility();
            }
        }
    }

    public bool ShowLoading => IsLoading;

    public bool ShowUnavailable => IsUnavailable && !IsLoading;

    public bool ShowEmpty => !IsUnavailable && IsEmpty && !IsLoading;

    /// <summary>
    /// Exactly one of <see cref="ShowLoading"/> / <see cref="ShowUnavailable"/> / <see cref="ShowEmpty"/> / ShowCharts is
    /// true (Cortex M-1): the solid loading panel (112:16419) replaces the charts, it never hides behind stale ones.
    /// </summary>
    public bool ShowCharts => !IsLoading && !IsUnavailable && !IsEmpty;

    public bool HasOfflinePeriods
    {
        get => _hasOfflinePeriods;
        private set
        {
            if (SetProperty(ref _hasOfflinePeriods, value))
            {
                OnPropertyChanged(nameof(ShowOfflineNotice));
            }
        }
    }

    public bool ShowOfflineNotice => ShowCharts && HasOfflinePeriods;

    public HistorySeries? CpuSeries
    {
        get => _cpuSeries;
        private set => SetProperty(ref _cpuSeries, value);
    }

    public HistorySeries? MemorySeries
    {
        get => _memorySeries;
        private set => SetProperty(ref _memorySeries, value);
    }

    public HistorySeries? DiskSeries
    {
        get => _diskSeries;
        private set => SetProperty(ref _diskSeries, value);
    }

    public DateTimeOffset RangeStartUtc
    {
        get => _rangeStartUtc;
        private set => SetProperty(ref _rangeStartUtc, value);
    }

    public DateTimeOffset RangeEndUtc
    {
        get => _rangeEndUtc;
        private set => SetProperty(ref _rangeEndUtc, value);
    }

    public string CpuCurrentDisplay
    {
        get => _cpuCurrentDisplay;
        private set => SetProperty(ref _cpuCurrentDisplay, value);
    }

    public string MemoryCurrentDisplay
    {
        get => _memoryCurrentDisplay;
        private set => SetProperty(ref _memoryCurrentDisplay, value);
    }

    public string DiskCurrentDisplay
    {
        get => _diskCurrentDisplay;
        private set => SetProperty(ref _diskCurrentDisplay, value);
    }

    public string CpuSummary
    {
        get => _cpuSummary;
        private set => SetProperty(ref _cpuSummary, value);
    }

    public string MemorySummary
    {
        get => _memorySummary;
        private set => SetProperty(ref _memorySummary, value);
    }

    public string DiskSummary
    {
        get => _diskSummary;
        private set => SetProperty(ref _diskSummary, value);
    }

    /// <summary>Binds the VM to a server and starts loading its history. Called on the UI thread.</summary>
    public void Load(Guid serverId, string serverName)
    {
        // Cortex M-1: switching servers must never show server A's series, peaks, summaries or period while B loads.
        if (serverId != _serverId)
        {
            ClearPresentedRange();
        }

        _serverId = serverId;
        Title = serverName;

        if (!_subscribed)
        {
            _monitoringStateStore.StateChanged += OnMonitoringStateChanged;
            _subscribed = true;
        }

        RefreshCurrentValues();
        _ = LoadRangeAsync(Ranges[_selectedRangeIndex]);

        // The selector list is read once per page; later Loads (server switches) only re-select.
        if (!_serversRequested)
        {
            _serversRequested = true;
            _ = LoadServersAsync();
        }
        else
        {
            SyncSelectedServer();
        }
    }

    /// <summary>
    /// Reads the server list from the same service that feeds the Dashboard (visible servers only, same
    /// order). The server being viewed is always selectable even if the list could not be read.
    /// </summary>
    public async Task LoadServersAsync()
    {
        if (_disposed || _serverService is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _serversGeneration);
        IReadOnlyList<Server> servers;
        try
        {
            servers = await _serverService.GetAllAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError("History server list failed. Type: {Type}.", exception.GetType().Name);
            servers = [];
        }

        if (_disposed || generation != Volatile.Read(ref _serversGeneration))
        {
            return;
        }

        Servers.Clear();
        foreach (var server in servers.Where(server => !server.IsHidden).OrderBy(server => server.CreatedAt))
        {
            Servers.Add(BuildServerOption(server.Id, server.Name));
        }

        if (_serverId != Guid.Empty && Servers.All(option => option.Id != _serverId))
        {
            Servers.Insert(0, BuildServerOption(_serverId, Title));
        }

        SyncSelectedServer();
    }

    private HistoryServerOptionViewModel BuildServerOption(Guid serverId, string name)
    {
        var os = ServerContextPresentation.OperatingSystemDisplay(_metricsStore.GetLastSnapshot(serverId));
        string? status = null;
        if (_monitoringStateStore.TryGet(serverId, out var state))
        {
            status = state.Health == ServerHealth.Offline
                ? _localizationService.GetString("HistoryServerStatusOffline")
                : state.HasEverSucceeded
                    ? _localizationService.GetString("HistoryServerStatusConnected")
                    : null;
        }

        return new HistoryServerOptionViewModel(serverId, name, ServerContextPresentation.Join(os, status));
    }

    private void SyncSelectedServer()
    {
        var match = Servers.FirstOrDefault(option => option.Id == _serverId);
        if (match is null || ReferenceEquals(match, _selectedServer))
        {
            return;
        }

        // Set the backing field directly: this mirrors the page's server, it is not a user selection.
        var previous = _selectedServer;
        _selectedServer = match;
        OnPropertyChanged(nameof(SelectedServer));
        OnPropertyChanged(nameof(SelectedServerSubtitle));
        UpdateSelectorLoadingLine(previous);
    }

    /// <summary>
    /// The closed selector renders the selected item, so the loading line ("A carregar histórico…", 112:16322) is
    /// shown by giving that item a transient subtitle while loading; a previously selected item is restored.
    /// </summary>
    private void UpdateSelectorLoadingLine(HistoryServerOptionViewModel? previous = null)
    {
        if (previous is not null && !ReferenceEquals(previous, _selectedServer))
        {
            previous.SetTransientSubtitle(null);
        }

        _selectedServer?.SetTransientSubtitle(IsLoading ? _localizationService?.GetString("HistorySelectorLoading") : null);
    }

    /// <summary>
    /// Loads one range. Race-safe: increments the generation and cancels the previous query; a
    /// response is applied only if its generation is still current, so a superseded (older-range)
    /// reply is discarded even if it arrives later.
    /// </summary>
    public async Task LoadRangeAsync(HistoryTimeRange range)
    {
        if (_disposed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        if (!_queryService.IsAvailable)
        {
            IsUnavailable = true;
            IsLoading = false;
            IsEmpty = false;
            EmptyKind = HistoryEmptyKind.None;
            ClearSeries();
            return;
        }

        IsUnavailable = false;
        IsLoading = true;

        try
        {
            var result = await _queryService.GetHistoryAsync(_serverId, range, cts.Token).ConfigureAwait(true);
            if (_disposed || generation != Volatile.Read(ref _generation))
            {
                return; // A newer selection superseded this query.
            }

            var emptyKind = HistoryEmptyKind.None;
            if (result.IsEmpty)
            {
                emptyKind = await ResolveEmptyKindAsync(range, cts.Token).ConfigureAwait(true);
                if (_disposed || generation != Volatile.Read(ref _generation))
                {
                    return;
                }
            }

            ApplyResult(result, emptyKind);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection; ignore.
        }
        catch (Exception exception)
        {
            if (!_disposed && generation == Volatile.Read(ref _generation))
            {
                _logger.LogError("History query failed. Type: {Type}.", exception.GetType().Name);
                IsUnavailable = true;
            }
        }
        finally
        {
            if (!_disposed && generation == Volatile.Read(ref _generation))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// D-UI3-10: one extra 30-day query, only on the empty path. Retention is 30 days, so an empty
    /// 30-day window means nothing is stored for the server at all. If the probe itself fails we make no
    /// claim about the whole history and fall back to the period wording.
    /// </summary>
    private async Task<HistoryEmptyKind> ResolveEmptyKindAsync(HistoryTimeRange range, CancellationToken cancellationToken)
    {
        if (range == HistoryTimeRange.Last30Days)
        {
            return HistoryEmptyKind.NeverRecorded;
        }

        try
        {
            var probe = await _queryService
                .GetHistoryAsync(_serverId, HistoryTimeRange.Last30Days, cancellationToken)
                .ConfigureAwait(true);
            return probe.IsEmpty ? HistoryEmptyKind.NeverRecorded : HistoryEmptyKind.Period;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError("History retention probe failed. Type: {Type}.", exception.GetType().Name);
            return HistoryEmptyKind.Period;
        }
    }

    private void ApplyResult(ServerHistoryResult result, HistoryEmptyKind emptyKind)
    {
        RangeStartUtc = result.StartUtc;
        RangeEndUtc = result.EndUtc;
        CpuSeries = result.Cpu;
        MemorySeries = result.Memory;
        DiskSeries = result.Disk;
        HasOfflinePeriods = result.ContainsOfflineSamples;
        EmptyKind = result.IsEmpty ? emptyKind : HistoryEmptyKind.None;
        IsEmpty = result.IsEmpty;
        RefreshCurrentValues();
        RebuildSummaries(result.Range);
        RebuildRangeText(result.Range);
    }

    /// <summary>Forgets everything derived from the previously loaded range (series, peaks, summaries, axes, footer).</summary>
    private void ClearPresentedRange()
    {
        ClearSeries();
        IsEmpty = false;
        EmptyKind = HistoryEmptyKind.None;
        RangeStartUtc = default;
        RangeEndUtc = default;
        CpuSummary = string.Empty;
        MemorySummary = string.Empty;
        DiskSummary = string.Empty;
        SetText(ref _cpuPeakDisplay, string.Empty, nameof(CpuPeakDisplay));
        SetText(ref _memoryPeakDisplay, string.Empty, nameof(MemoryPeakDisplay));
        SetText(ref _diskPeakDisplay, string.Empty, nameof(DiskPeakDisplay));
        SetText(ref _periodFooter, string.Empty, nameof(PeriodFooter));
        _xAxisTicks = null;
        _xAxisTicksCompact = null;
        RaiseAxis();
    }

    private void ClearSeries()
    {
        CpuSeries = null;
        MemorySeries = null;
        DiskSeries = null;
        HasOfflinePeriods = false;
    }

    private void RefreshCurrentValues()
    {
        var snapshot = _metricsStore.GetLastSnapshot(_serverId);
        CpuCurrentDisplay = FormatCurrent(snapshot?.CpuUsagePercent);
        MemoryCurrentDisplay = FormatCurrent(snapshot?.MemoryUsagePercent);
        DiskCurrentDisplay = FormatCurrent(snapshot?.DiskUsagePercent);

        SetCurrentValue(ref _cpuCurrentValue, snapshot?.CpuUsagePercent, nameof(CpuCurrentValue), nameof(HasCpuCurrent));
        SetCurrentValue(ref _memoryCurrentValue, snapshot?.MemoryUsagePercent, nameof(MemoryCurrentValue), nameof(HasMemoryCurrent));
        SetCurrentValue(ref _diskCurrentValue, snapshot?.DiskUsagePercent, nameof(DiskCurrentValue), nameof(HasDiskCurrent));
    }

    private void SetCurrentValue(ref string? field, double? value, string valueProperty, string hasProperty)
    {
        var text = value is { } percent
            ? string.Format(FormatCulture, "{0:0}", percent)
            : null;
        if (string.Equals(field, text, StringComparison.Ordinal))
        {
            return;
        }

        field = text;
        OnPropertyChanged(valueProperty);
        OnPropertyChanged(hasProperty);
    }

    private void RebuildSummaries(HistoryTimeRange range)
    {
        var rangeLabel = _localizationService.GetString($"HistoryRangeName{range}");
        CpuSummary = BuildSummary(CpuTitle, rangeLabel, CpuCurrentDisplay, CpuSeries?.Maximum);
        MemorySummary = BuildSummary(MemoryTitle, rangeLabel, MemoryCurrentDisplay, MemorySeries?.Maximum);
        DiskSummary = BuildSummary(DiskTitle, rangeLabel, DiskCurrentDisplay, DiskSeries?.Maximum);

        SetText(ref _cpuPeakDisplay, FormatPeak(CpuSeries?.Maximum), nameof(CpuPeakDisplay));
        SetText(ref _memoryPeakDisplay, FormatPeak(MemorySeries?.Maximum), nameof(MemoryPeakDisplay));
        SetText(ref _diskPeakDisplay, FormatPeak(DiskSeries?.Maximum), nameof(DiskPeakDisplay));
    }

    /// <summary>Footer period and axis labels for the loaded range, in local time and the UI culture.</summary>
    private void RebuildRangeText(HistoryTimeRange range)
    {
        var culture = FormatCulture;
        var timeZone = _timeProvider.LocalTimeZone;
        var timeFormat = _localizationService.GetString("HistoryAxisTimeFormat");
        var dayFormat = _localizationService.GetString("HistoryAxisDayFormat");

        var dates = HistoryPresentation.FormatPeriod(
            RangeStartUtc,
            RangeEndUtc,
            timeZone,
            culture,
            _localizationService.GetString("HistoryPeriodSameDayFormat"),
            _localizationService.GetString("HistoryPeriodSameMonthFormat"),
            _localizationService.GetString("HistoryPeriodSameYearFormat"),
            _localizationService.GetString("HistoryPeriodCrossYearFormat"));
        SetText(
            ref _periodFooter,
            string.Format(
                culture,
                _localizationService.GetString("HistoryPeriodFooterFormat"),
                _localizationService.GetString($"HistoryRangeName{range}"),
                dates),
            nameof(PeriodFooter));

        _xAxisTicks = HistoryPresentation.RoundTicks(
            RangeStartUtc, RangeEndUtc, range, 5, timeZone, culture, timeFormat, dayFormat);
        _xAxisTicksCompact = HistoryPresentation.RoundTicks(
            RangeStartUtc, RangeEndUtc, range, 3, timeZone, culture, timeFormat, dayFormat);
        RaiseAxis();
    }

    private void RaiseAxis()
    {
        OnPropertyChanged(nameof(XAxisTicks));
        OnPropertyChanged(nameof(XAxisTicksCompact));
        OnPropertyChanged(nameof(XAxisLabels));
        OnPropertyChanged(nameof(XAxisLabelsCompact));
    }

    private string FormatPeak(double? maximum) => string.Format(
        FormatCulture,
        _localizationService.GetString("HistoryPeakFormat"),
        maximum is { } max
            ? string.Format(FormatCulture, "{0:0}%", max)
            : Unknown);

    private void SetText(ref string? field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private string BuildSummary(string metric, string rangeLabel, string current, double? maximum)
    {
        var unknownDisplay = _localizationService.GetString("HistoryValueUnknown");
        var unknownAccessible = _localizationService.GetString("HistoryValueUnknownAccessible");
        var currentText = string.Equals(current, unknownDisplay, StringComparison.Ordinal)
            ? unknownAccessible
            : current;
        var maxText = maximum is { } max
            ? string.Format(FormatCulture, "{0:0}%", max)
            : unknownAccessible;

        var summary = string.Format(
            FormatCulture,
            _localizationService.GetString("HistoryChartSummaryFormat"),
            metric,
            rangeLabel,
            currentText,
            maxText);

        return HasOfflinePeriods
            ? summary + _localizationService.GetString("HistoryChartSummaryOfflineSuffix")
            : summary;
    }

    private string FormatCurrent(double? value) => value is { } percent
        ? string.Format(FormatCulture, "{0:0}%", percent)
        : _localizationService.GetString("HistoryValueUnknown");

    private void OnMonitoringStateChanged(object? sender, Guid serverId)
    {
        if (_disposed || serverId != _serverId)
        {
            return;
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            LiveRefresh();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(LiveRefresh);
        }
    }

    private void LiveRefresh()
    {
        if (_disposed)
        {
            return;
        }

        // Current value is cheap (in-memory) — always refresh it. Re-querying the chart is throttled
        // so a 10s polling cadence never drives a query storm (spec §52).
        RefreshCurrentValues();
        RebuildSummaries(Ranges[_selectedRangeIndex]);

        var now = _timeProvider.GetUtcNow();
        if (now - _lastLiveRefreshUtc < LiveRefreshThrottle)
        {
            return;
        }

        _lastLiveRefreshUtc = now;
        _ = LoadRangeAsync(Ranges[_selectedRangeIndex]);
    }

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowUnavailable));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowCharts));
        OnPropertyChanged(nameof(ShowOfflineNotice));
        OnPropertyChanged(nameof(ShowEmptyNeverRecorded));
        OnPropertyChanged(nameof(ShowEmptyPeriod));
        OnPropertyChanged(nameof(ShowViewLast30Days));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EmptyActionText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _serversGeneration);
        IsLoading = false;
        if (_subscribed)
        {
            _monitoringStateStore.StateChanged -= OnMonitoringStateChanged;
            _subscribed = false;
        }

        _cts?.Cancel();
        _cts?.Dispose();
    }
}
