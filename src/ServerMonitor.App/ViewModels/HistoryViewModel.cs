using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.ViewModels;

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

    public HistoryViewModel(
        IServerHistoryQueryService queryService,
        IServerMetricsStore metricsStore,
        IServerMonitoringStateStore monitoringStateStore,
        INavigationService navigationService,
        ILocalizationService localizationService,
        ILogger<HistoryViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
        _monitoringStateStore = monitoringStateStore ?? throw new ArgumentNullException(nameof(monitoringStateStore));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        BackCommand = new RelayCommand(navigationService.GoToDashboard);

        CpuTitle = localizationService.GetString("HistoryMetricCpu");
        MemoryTitle = localizationService.GetString("HistoryMetricMemory");
        DiskTitle = localizationService.GetString("HistoryMetricDisk");
    }

    public ICommand BackCommand { get; }

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

    public bool ShowCharts => !IsUnavailable && !IsEmpty;

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
        _serverId = serverId;
        Title = serverName;

        if (!_subscribed)
        {
            _monitoringStateStore.StateChanged += OnMonitoringStateChanged;
            _subscribed = true;
        }

        RefreshCurrentValues();
        _ = LoadRangeAsync(Ranges[_selectedRangeIndex]);
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

            ApplyResult(result);
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

    private void ApplyResult(ServerHistoryResult result)
    {
        RangeStartUtc = result.StartUtc;
        RangeEndUtc = result.EndUtc;
        CpuSeries = result.Cpu;
        MemorySeries = result.Memory;
        DiskSeries = result.Disk;
        HasOfflinePeriods = result.ContainsOfflineSamples;
        IsEmpty = result.IsEmpty;
        RefreshCurrentValues();
        RebuildSummaries(result.Range);
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
    }

    private void RebuildSummaries(HistoryTimeRange range)
    {
        var rangeLabel = _localizationService.GetString($"HistoryRangeName{range}");
        CpuSummary = BuildSummary(CpuTitle, rangeLabel, CpuCurrentDisplay, CpuSeries?.Maximum);
        MemorySummary = BuildSummary(MemoryTitle, rangeLabel, MemoryCurrentDisplay, MemorySeries?.Maximum);
        DiskSummary = BuildSummary(DiskTitle, rangeLabel, DiskCurrentDisplay, DiskSeries?.Maximum);
    }

    private string BuildSummary(string metric, string rangeLabel, string current, double? maximum)
    {
        var unknownDisplay = _localizationService.GetString("HistoryValueUnknown");
        var unknownAccessible = _localizationService.GetString("HistoryValueUnknownAccessible");
        var currentText = string.Equals(current, unknownDisplay, StringComparison.Ordinal)
            ? unknownAccessible
            : current;
        var maxText = maximum is { } max
            ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", max)
            : unknownAccessible;

        var summary = string.Format(
            CultureInfo.CurrentUICulture,
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
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", percent)
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
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
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
