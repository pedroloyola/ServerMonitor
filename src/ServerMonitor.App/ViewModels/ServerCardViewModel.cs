using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// Presents one server. Metric values come from the transient <see cref="IServerMetricsStore"/>;
/// health, staleness, the refresh indicator and error state come from the engine-owned
/// <see cref="ServerMonitoringState"/> and are pushed in through <see cref="ApplyMonitoringState"/>
/// as the engine reschedules — the ViewModel never runs a timer. A manual refresh is delegated to
/// <see cref="IMonitoringEngine.RefreshNowAsync"/> so it shares the scheduler's single-flight and
/// restarts that server's interval.
/// </summary>
public sealed class ServerCardViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IServerConnectionStateStore _connectionStateStore;
    private readonly IServerMonitoringStateStore _monitoringStateStore;
    private readonly IMonitoringEngine _monitoringEngine;
    private readonly AsyncRelayCommand _refreshMetricsCommand;
    private ServerConnectionState _connectionState;
    private string _connectionStateDisplayName;
    private ServerMonitoringState _monitoringState;
    private ServerMetricsSnapshot? _metrics;

    public ServerCardViewModel(
        Server server,
        SshConnectionResult? connectionResult,
        ILocalizationService localizationService,
        IServerMetricsStore metricsStore,
        IServerConnectionStateStore connectionStateStore,
        IServerMonitoringStateStore monitoringStateStore,
        IMonitoringEngine monitoringEngine,
        Func<Task> edit,
        Func<Task> hide,
        Func<Task> remove,
        Func<Task>? viewHistory = null)
    {
        Server = server;
        _localizationService = localizationService;
        _metricsStore = metricsStore;
        _connectionStateStore = connectionStateStore;
        _monitoringStateStore = monitoringStateStore;
        _monitoringEngine = monitoringEngine;

        OperatingSystemDisplayName = localizationService.GetString(
            $"OperatingSystem{server.OperatingSystem}");
        _connectionState = connectionResult?.State ?? ServerConnectionState.NeverConnected;
        _connectionStateDisplayName = localizationService.GetString($"ConnectionState{_connectionState}");
        MoreOptionsAutomationName = string.Format(
            CultureInfo.CurrentUICulture,
            localizationService.GetString("ServerCardMoreOptionsFor"),
            server.Name);
        RefreshMetricsAutomationName = string.Format(
            CultureInfo.CurrentUICulture,
            localizationService.GetString("ServerMetricsRefreshFor"),
            server.Name);
        AutomationSummary = string.Format(
            CultureInfo.CurrentUICulture,
            localizationService.GetString("ServerCardAutomationSummary"),
            server.Name,
            OperatingSystemDisplayName,
            Endpoint,
            ConnectionStateDisplayName);

        _metrics = metricsStore.GetLastSnapshot(server.Id);
        _monitoringState = monitoringStateStore.Get(server.Id);

        EditCommand = new AsyncRelayCommand(edit);
        HideCommand = new AsyncRelayCommand(hide);
        RemoveCommand = new AsyncRelayCommand(remove);
        ViewHistoryCommand = new AsyncRelayCommand(viewHistory ?? (() => Task.CompletedTask));
        _refreshMetricsCommand = new AsyncRelayCommand(RefreshMetricsAsync, () => !IsRefreshingMetrics);
    }

    public Server Server { get; }

    public string Name => Server.Name;

    public string Host => Server.Host;

    public int Port => Server.Port;

    public string Endpoint => $"{Host}:{Port}";

    public string OperatingSystemDisplayName { get; }

    public string ConnectionStateDisplayName
    {
        get => _connectionStateDisplayName;
        private set => SetProperty(ref _connectionStateDisplayName, value);
    }

    public ServerConnectionState ConnectionState
    {
        get => _connectionState;
        private set => SetProperty(ref _connectionState, value);
    }

    public string MoreOptionsAutomationName { get; }

    public string RefreshMetricsAutomationName { get; }

    public string AutomationSummary { get; }

    public ICommand EditCommand { get; }

    public ICommand HideCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ViewHistoryCommand { get; }

    public ICommand RefreshMetricsCommand => _refreshMetricsCommand;

    /// <summary>Linux and macOS servers are collected through the same pipeline; other configurations have no metrics story yet.</summary>
    public bool SupportsMetrics =>
        Server.OperatingSystem is ServerOperatingSystem.Linux or ServerOperatingSystem.MacOS;

    // --- Monitoring state (engine-owned, pushed via ApplyMonitoringState) ----

    public ServerHealth Health => _monitoringState.Health;

    public string HealthDisplayName => _localizationService.GetString($"ServerHealth{Health}");

    public bool IsRefreshingMetrics => _monitoringState.IsRefreshing;

    public bool IsStale => _monitoringState.IsStale;

    public int ConsecutiveFailures => _monitoringState.ConsecutiveFailures;

    public MetricsCollectionErrorCode? LastError => _monitoringState.LastError;

    public DateTimeOffset? LastAttemptAt => _monitoringState.LastAttemptAt;

    public DateTimeOffset? LastSuccessAt => _monitoringState.LastSuccessAt;

    /// <summary>
    /// Discreet "last updated N ago" shown only while stale and we still hold a prior snapshot.
    /// The age is measured between the last success and the most recent attempt, both engine
    /// timestamps, so it does not depend on a live clock in the ViewModel.
    /// </summary>
    public string? StaleAgeDisplay
    {
        get
        {
            if (!IsStale
                || _monitoringState.LastSuccessAt is not { } success
                || _monitoringState.LastAttemptAt is not { } attempt)
            {
                return null;
            }

            var age = attempt - success;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            if (age.TotalDays >= 1)
            {
                return Format("ServerMetricsStaleDaysFormat", (int)age.TotalDays);
            }

            if (age.TotalHours >= 1)
            {
                return Format("ServerMetricsStaleHoursFormat", (int)age.TotalHours);
            }

            return Format("ServerMetricsStaleMinutesFormat", Math.Max(1, (int)age.TotalMinutes));
        }
    }

    public bool HasStaleIndicator => IsStale && HasMetrics && StaleAgeDisplay is not null;

    public bool HasMetrics => _metrics is not null;

    public bool IsMetricsPending =>
        SupportsMetrics && !HasMetrics && !IsRefreshingMetrics && !HasMetricsError;

    /// <summary>An error is surfaced only when there is no snapshot to fall back on; a failed
    /// cycle with an existing snapshot keeps the metrics visible and shows the stale indicator.</summary>
    public bool HasMetricsError =>
        SupportsMetrics && !HasMetrics && !IsRefreshingMetrics && _monitoringState.LastError is not null;

    public string? MetricsErrorDisplay =>
        HasMetricsError ? _localizationService.GetString("ServerMetricsUpdateFailed") : null;

    public string? CpuUsageDisplay => FormatPercent(_metrics?.CpuUsagePercent);

    public bool HasCpuUsage => CpuUsageDisplay is not null;

    public double CpuUsageValue => _metrics?.CpuUsagePercent ?? 0;

    public bool HasCpuPercent => _metrics?.CpuUsagePercent is not null;

    public string? MemoryUsageDisplay =>
        FormatPercent(_metrics?.MemoryUsagePercent) ??
        FormatBytesUsage(_metrics?.MemoryUsedBytes, _metrics?.MemoryTotalBytes);

    public bool HasMemoryUsage => MemoryUsageDisplay is not null;

    public double MemoryUsageValue => _metrics?.MemoryUsagePercent ?? 0;

    public bool HasMemoryPercent => _metrics?.MemoryUsagePercent is not null;

    public string? DiskUsageDisplay =>
        FormatPercent(_metrics?.DiskUsagePercent) ??
        FormatBytesUsage(_metrics?.DiskUsedBytes, _metrics?.DiskTotalBytes);

    public bool HasDiskUsage => DiskUsageDisplay is not null;

    public double DiskUsageValue => _metrics?.DiskUsagePercent ?? 0;

    public bool HasDiskPercent => _metrics?.DiskUsagePercent is not null;

    public string? UptimeDisplay => _metrics?.Uptime is { } uptime ? FormatUptime(uptime) : null;

    public bool HasUptime => UptimeDisplay is not null;

    public string? DetectedOperatingSystemDisplay =>
        string.IsNullOrWhiteSpace(_metrics?.OperatingSystemName)
            ? null
            : string.Format(
                CultureInfo.CurrentUICulture,
                _localizationService.GetString("ServerMetricsDetectedOperatingSystemFormat"),
                string.IsNullOrWhiteSpace(_metrics.OperatingSystemVersion)
                    ? _metrics.OperatingSystemName
                    : $"{_metrics.OperatingSystemName} {_metrics.OperatingSystemVersion}");

    public bool HasDetectedOperatingSystem => DetectedOperatingSystemDisplay is not null;

    public string? MetricsTimestampDisplay => _metrics is null
        ? null
        : string.Format(
            CultureInfo.CurrentUICulture,
            _localizationService.GetString("ServerMetricsUpdatedAtFormat"),
            _metrics.CollectedAt.ToLocalTime().ToString("t", CultureInfo.CurrentUICulture));

    public void UpdateConnectionState(SshConnectionResult? result)
    {
        var newState = result?.State ?? ServerConnectionState.NeverConnected;
        if (newState == ConnectionState)
        {
            return;
        }

        ConnectionState = newState;
        ConnectionStateDisplayName = _localizationService.GetString($"ConnectionState{newState}");
    }

    /// <summary>
    /// Applies the latest engine-published monitoring state and re-reads the current snapshot.
    /// Called on the UI thread by the dashboard as the engine reschedules, and after a manual
    /// refresh. The snapshot re-read here is how automatic cycles surface fresh values.
    /// </summary>
    public void ApplyMonitoringState(ServerMonitoringState state)
    {
        var wasRefreshing = IsRefreshingMetrics;
        _monitoringState = state;
        _metrics = _metricsStore.GetLastSnapshot(Server.Id);
        NotifyPresentationChanged();
        if (wasRefreshing != IsRefreshingMetrics)
        {
            _refreshMetricsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RefreshMetricsAsync()
    {
        try
        {
            var result = await _monitoringEngine.RefreshNowAsync(Server.Id).ConfigureAwait(true);
            if (result.ConnectionResult is not null)
            {
                UpdateConnectionState(result.ConnectionResult);
                _connectionStateStore.Set(Server.Id, result.ConnectionResult);
            }
        }
        catch (Exception)
        {
            // The engine records the outcome in the monitoring-state store; reflect it below.
        }
        finally
        {
            ApplyMonitoringState(_monitoringStateStore.Get(Server.Id));
        }
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(HealthDisplayName));
        OnPropertyChanged(nameof(IsRefreshingMetrics));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(ConsecutiveFailures));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(LastAttemptAt));
        OnPropertyChanged(nameof(LastSuccessAt));
        OnPropertyChanged(nameof(StaleAgeDisplay));
        OnPropertyChanged(nameof(HasStaleIndicator));
        OnPropertyChanged(nameof(HasMetrics));
        OnPropertyChanged(nameof(IsMetricsPending));
        OnPropertyChanged(nameof(MetricsErrorDisplay));
        OnPropertyChanged(nameof(HasMetricsError));
        OnPropertyChanged(nameof(CpuUsageDisplay));
        OnPropertyChanged(nameof(HasCpuUsage));
        OnPropertyChanged(nameof(CpuUsageValue));
        OnPropertyChanged(nameof(HasCpuPercent));
        OnPropertyChanged(nameof(MemoryUsageDisplay));
        OnPropertyChanged(nameof(HasMemoryUsage));
        OnPropertyChanged(nameof(MemoryUsageValue));
        OnPropertyChanged(nameof(HasMemoryPercent));
        OnPropertyChanged(nameof(DiskUsageDisplay));
        OnPropertyChanged(nameof(HasDiskUsage));
        OnPropertyChanged(nameof(DiskUsageValue));
        OnPropertyChanged(nameof(HasDiskPercent));
        OnPropertyChanged(nameof(UptimeDisplay));
        OnPropertyChanged(nameof(HasUptime));
        OnPropertyChanged(nameof(DetectedOperatingSystemDisplay));
        OnPropertyChanged(nameof(HasDetectedOperatingSystem));
        OnPropertyChanged(nameof(MetricsTimestampDisplay));
    }

    private string Format(string key, int value) =>
        string.Format(CultureInfo.CurrentUICulture, _localizationService.GetString(key), value);

    private string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                _localizationService.GetString("ServerMetricsUptimeDaysHoursFormat"),
                (int)uptime.TotalDays,
                uptime.Hours);
        }

        if (uptime.TotalHours >= 1)
        {
            return string.Format(
                CultureInfo.CurrentUICulture,
                _localizationService.GetString("ServerMetricsUptimeHoursMinutesFormat"),
                (int)uptime.TotalHours,
                uptime.Minutes);
        }

        return string.Format(
            CultureInfo.CurrentUICulture,
            _localizationService.GetString("ServerMetricsUptimeMinutesFormat"),
            Math.Max(1, (int)uptime.TotalMinutes));
    }

    private static string? FormatPercent(double? value) => value is { } percent
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", percent)
        : null;

    private static string? FormatBytesUsage(long? used, long? total)
    {
        if (used is not { } usedBytes || total is not { } totalBytes || totalBytes <= 0)
        {
            return null;
        }

        return string.Format(
            CultureInfo.CurrentUICulture,
            "{0} / {1}",
            FormatBytes(usedBytes),
            FormatBytes(totalBytes));
    }

    private static string FormatBytes(long bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        const double mib = 1024d * 1024;
        return bytes >= gib
            ? string.Format(CultureInfo.CurrentUICulture, "{0:0.0} GB", bytes / gib)
            : string.Format(CultureInfo.CurrentUICulture, "{0:0} MB", bytes / mib);
    }
}
