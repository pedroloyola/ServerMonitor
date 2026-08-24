using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class ServerCardViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IServerConnectionStateStore _connectionStateStore;
    private ServerConnectionState _connectionState;
    private string _connectionStateDisplayName;
    private ServerMetricsSnapshot? _metrics;
    private bool _isRefreshingMetrics;
    private string? _metricsErrorDisplay;

    public ServerCardViewModel(
        Server server,
        SshConnectionResult? connectionResult,
        ILocalizationService localizationService,
        IServerMetricsStore metricsStore,
        IServerConnectionStateStore connectionStateStore,
        Func<Task> edit,
        Func<Task> hide,
        Func<Task> remove)
    {
        Server = server;
        _localizationService = localizationService;
        _metricsStore = metricsStore;
        _connectionStateStore = connectionStateStore;

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

        EditCommand = new AsyncRelayCommand(edit);
        HideCommand = new AsyncRelayCommand(hide);
        RemoveCommand = new AsyncRelayCommand(remove);
        RefreshMetricsCommand = new AsyncRelayCommand(RefreshMetricsAsync, () => !IsRefreshingMetrics);
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

    public ICommand RefreshMetricsCommand { get; }

    /// <summary>Only Linux servers are ever collected; other configurations have no metrics story yet.</summary>
    public bool SupportsMetrics => Server.OperatingSystem == ServerOperatingSystem.Linux;

    public bool HasMetrics => _metrics is not null;

    public bool IsMetricsPending =>
        SupportsMetrics && !HasMetrics && !IsRefreshingMetrics && !HasMetricsError;

    public string? MetricsErrorDisplay => _metricsErrorDisplay;

    public bool HasMetricsError => !string.IsNullOrWhiteSpace(_metricsErrorDisplay);

    public bool IsRefreshingMetrics
    {
        get => _isRefreshingMetrics;
        private set => SetProperty(ref _isRefreshingMetrics, value);
    }

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

    private async Task RefreshMetricsAsync()
    {
        _metricsErrorDisplay = null;
        IsRefreshingMetrics = true;
        NotifyMetricsPresentationChanged();
        try
        {
            var result = await _metricsStore.RefreshAsync(Server).ConfigureAwait(true);
            ApplyMetricsResult(result);
        }
        finally
        {
            IsRefreshingMetrics = false;
            NotifyMetricsPresentationChanged();
        }
    }

    private void ApplyMetricsResult(ServerMetricsCollectionResult result)
    {
        if (result.Snapshot is not null)
        {
            _metrics = result.Snapshot;
            _metricsErrorDisplay = null;
            NotifyMetricsPresentationChanged();
        }
        else if (result.ErrorCode != MetricsCollectionErrorCode.None)
        {
            _metricsErrorDisplay = _localizationService.GetString("ServerMetricsUpdateFailed");
            NotifyMetricsPresentationChanged();
        }

        if (result.ConnectionResult is not null)
        {
            UpdateConnectionState(result.ConnectionResult);
            _connectionStateStore.Set(Server.Id, result.ConnectionResult);
        }
    }

    private void NotifyMetricsPresentationChanged()
    {
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
