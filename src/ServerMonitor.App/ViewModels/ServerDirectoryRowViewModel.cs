using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.4: one server as a row of the overview's summary list and of the Servidores table. A thin, read-only view over
/// the SAME <see cref="ServerCardViewModel"/> the engine updates (health, metrics), so the row never computes a state of
/// its own. A metric without a known percentage is shown as "—", never 0% (offline / no data / partial snapshot).
/// </summary>
public sealed class ServerDirectoryRowViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly MonitoringThresholds _thresholds;
    private bool _disposed;

    public ServerDirectoryRowViewModel(
        ServerCardViewModel card,
        ILocalizationService localization,
        Action<ServerCardViewModel> openDetail,
        MonitoringThresholds? thresholds = null)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _thresholds = thresholds ?? MonitoringThresholds.Default;
        ArgumentNullException.ThrowIfNull(openDetail);
        OpenDetailCommand = new RelayCommand(() => openDetail(Card));
        Card.PropertyChanged += OnCardPropertyChanged;
    }

    public ServerCardViewModel Card { get; }

    public Guid ServerId => Card.Server.Id;

    public string Name => Card.Name;

    /// <summary>The real configured endpoint: host, plus ":port" when it is not 22.</summary>
    public string Address => OverviewPresentation.Address(Card.Host, Card.Port);

    /// <summary>The configured system ("Linux" / "macOS"), the same localized value the card shows.</summary>
    public string OperatingSystemDisplay => Card.OperatingSystemDisplayName;

    /// <summary>Mid / stacked table (Prism r1): the system joins the address line, "192.168.1.10 · Linux".</summary>
    public string AddressAndSystemDisplay => ServerContextPresentation.Join(Address, OperatingSystemDisplay);

    public ServerHealth Health => Card.Health;

    /// <summary>UI.5: the shared status copy (<see cref="ServerStatusPresentation"/>), the same function the Detail page reads.</summary>
    public string StatusDisplay => ServerStatusPresentation.StatusText(Health, _localization);

    public string CpuDisplay => Percent(HasCpuPercent, Card.CpuUsageValue);

    public string MemoryDisplay => Percent(HasMemoryPercent, Card.MemoryUsageValue);

    public string DiskDisplay => Percent(HasDiskPercent, Card.DiskUsageValue);

    // An Offline server shows "—", not its retained (stale) snapshot — the table has no staleness cue, so an old value
    // would read as current. The Detail page shows that reading marked stale instead (H-UI5-2); both rules live in
    // ServerStatusPresentation.
    public bool HasCpuPercent => Card.HasCpuPercent && !IsOffline;

    public bool HasMemoryPercent => Card.HasMemoryPercent && !IsOffline;

    public bool HasDiskPercent => Card.HasDiskPercent && !IsOffline;

    public double CpuValue => HasCpuPercent ? Card.CpuUsageValue : 0;

    public double MemoryValue => HasMemoryPercent ? Card.MemoryUsageValue : 0;

    public double DiskValue => HasDiskPercent ? Card.DiskUsageValue : 0;

    private bool IsOffline => ServerStatusPresentation.RowHidesRetainedMetrics(Card.Health);

    // Figma 112:1468: a value above the engine's attention limit is drawn in the attention (or critical) text colour,
    // always next to its number (colour is never the only signal). Same inclusive limits as the engine.
    public ServerHealth CpuSeverity => HasCpuPercent
        ? OverviewPresentation.MetricSeverity(Card.CpuUsageValue, _thresholds.CpuWarning, _thresholds.CpuCritical)
        : ServerHealth.Healthy;

    public ServerHealth MemorySeverity => HasMemoryPercent
        ? OverviewPresentation.MetricSeverity(Card.MemoryUsageValue, _thresholds.MemoryWarning, _thresholds.MemoryCritical)
        : ServerHealth.Healthy;

    public ServerHealth DiskSeverity => HasDiskPercent
        ? OverviewPresentation.MetricSeverity(Card.DiskUsageValue, _thresholds.DiskWarning, _thresholds.DiskCritical)
        : ServerHealth.Healthy;

    /// <summary>The summary-list row (112:1057) read as "prod-web-01, Saudável".</summary>
    public string ListAutomationName => string.Join(", ", Name, StatusDisplay);

    /// <summary>"Ver detalhe de &lt;servidor&gt;" — the action's accessible name carries the server.</summary>
    public string DetailAutomationName => Format("ServerDetailOpenFor", Name);

    /// <summary>The whole row read as one sentence ("prod-web-01, Saudável, CPU 22%, RAM 41%, Disco 52%").</summary>
    public string RowAutomationName => Format(
        "ServerRowAutomationFormat",
        Name,
        StatusDisplay,
        AccessiblePercent(HasCpuPercent, Card.CpuUsageValue),
        AccessiblePercent(HasMemoryPercent, Card.MemoryUsageValue),
        AccessiblePercent(HasDiskPercent, Card.DiskUsageValue),
        Address,
        OperatingSystemDisplay);

    public ICommand OpenDetailCommand { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Card.PropertyChanged -= OnCardPropertyChanged;
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ServerCardViewModel.Health):
                // Offline also hides the metrics, so every metric presentation follows the health.
                OnPropertyChanged(string.Empty);
                break;
            case nameof(ServerCardViewModel.HasCpuPercent):
            case nameof(ServerCardViewModel.CpuUsageValue):
                OnPropertyChanged(nameof(CpuDisplay));
                OnPropertyChanged(nameof(CpuSeverity));
                OnPropertyChanged(nameof(HasCpuPercent));
                OnPropertyChanged(nameof(CpuValue));
                OnPropertyChanged(nameof(RowAutomationName));
                break;
            case nameof(ServerCardViewModel.HasMemoryPercent):
            case nameof(ServerCardViewModel.MemoryUsageValue):
                OnPropertyChanged(nameof(MemoryDisplay));
                OnPropertyChanged(nameof(MemorySeverity));
                OnPropertyChanged(nameof(HasMemoryPercent));
                OnPropertyChanged(nameof(MemoryValue));
                OnPropertyChanged(nameof(RowAutomationName));
                break;
            case nameof(ServerCardViewModel.HasDiskPercent):
            case nameof(ServerCardViewModel.DiskUsageValue):
                OnPropertyChanged(nameof(DiskDisplay));
                OnPropertyChanged(nameof(DiskSeverity));
                OnPropertyChanged(nameof(HasDiskPercent));
                OnPropertyChanged(nameof(DiskValue));
                OnPropertyChanged(nameof(RowAutomationName));
                break;
        }
    }

    private string Percent(bool known, double value) => known
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", value)
        : _localization.GetString("ServerMetricUnavailable");

    private string AccessiblePercent(bool known, double value) => known
        ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", value)
        : _localization.GetString("ServerMetricUnavailableAccessible");

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, _localization.GetString(key), args);
}
