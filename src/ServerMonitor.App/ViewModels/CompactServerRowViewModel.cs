using System.ComponentModel;
using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.8: one server as a row of the Compact window. A read-only view over the SAME <see cref="ServerCardViewModel"/> the
/// engine updates - it holds no store, engine, clock or timer and computes no state of its own: status copy from
/// <see cref="ServerStatusPresentation"/>, metric rules from <see cref="ServerMetricPresentation"/> (shared with the UI.4
/// rows). RC-5: the card raises ~31 unconditional notifications per sample, so this row keeps the PRESENTED values and
/// raises <see cref="INotifyPropertyChanged.PropertyChanged"/> only for a value that actually changed - an identical
/// sample raises nothing. Disposing it detaches it from the card.
/// </summary>
public sealed class CompactServerRowViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> RelevantCardProperties = new(StringComparer.Ordinal)
    {
        nameof(ServerCardViewModel.Health),
        nameof(ServerCardViewModel.IsStale),
        nameof(ServerCardViewModel.HasMetrics),
        nameof(ServerCardViewModel.StaleAgeDisplay),
        nameof(ServerCardViewModel.HasCpuPercent),
        nameof(ServerCardViewModel.CpuUsageValue),
        nameof(ServerCardViewModel.HasMemoryPercent),
        nameof(ServerCardViewModel.MemoryUsageValue),
        nameof(ServerCardViewModel.HasDiskPercent),
        nameof(ServerCardViewModel.DiskUsageValue)
    };

    private readonly ILocalizationService _localization;
    private readonly MonitoringThresholds _thresholds;
    private bool _disposed;
    private ServerHealth _health;
    private string _statusText = string.Empty;
    private ServerMetricReading _cpu;
    private ServerMetricReading _memory;
    private ServerMetricReading _disk;
    private string _cpuText = string.Empty;
    private string _memoryText = string.Empty;
    private string _diskText = string.Empty;
    private bool _showsStaleCue;
    private string? _staleAgeText;
    private string _accessibleName = string.Empty;

    public CompactServerRowViewModel(ServerCardViewModel card, ILocalizationService localization, MonitoringThresholds thresholds)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
        Refresh(notify: false);
        Card.PropertyChanged += OnCardPropertyChanged;
    }

    /// <summary>The engine-updated card this row presents (the same instance as in <c>VisibleServers</c>).</summary>
    public ServerCardViewModel Card { get; }

    public Guid ServerId => Card.Server.Id;

    public string Name => Card.Name;

    /// <summary>Drives the status dot (the same vocabulary as the UI.4 rows); never <c>HealthDisplayName</c>.</summary>
    public ServerHealth Health => _health;

    /// <summary>"Saudável" / "Atenção" / "Crítico" / "Sem ligação" / "Sem dados" (<see cref="ServerStatusPresentation.StatusKey"/>).</summary>
    public string StatusText => _statusText;

    public string CpuText => _cpuText;

    public bool IsCpuKnown => _cpu.IsKnown;

    /// <summary>0–100 for the bar; 0 only when unknown, and an unknown bar draws no fill.</summary>
    public double CpuBarValue => _cpu.Value;

    public ServerHealth CpuSeverity => _cpu.Severity;

    public string MemoryText => _memoryText;

    public bool IsMemoryKnown => _memory.IsKnown;

    public double MemoryBarValue => _memory.Value;

    public ServerHealth MemorySeverity => _memory.Severity;

    public string DiskText => _diskText;

    public bool IsDiskKnown => _disk.IsKnown;

    public double DiskBarValue => _disk.Value;

    public ServerHealth DiskSeverity => _disk.Severity;

    /// <summary>
    /// D-UI8-4 / R-7: a stale, NOT offline, row keeps its retained values and carries a staleness cue
    /// (<see cref="ServerStatusPresentation.ShowsRetainedReadingAsStale"/>); an Offline row hides them instead.
    /// </summary>
    public bool ShowsStaleCue => _showsStaleCue;

    /// <summary>"Última atualização há N min" from the engine's own timestamps (no clock); null without the cue.</summary>
    public string? StaleAgeText => _staleAgeText;

    /// <summary>G-14: name, status, and each metric with its non-chromatic attention/critical cue.</summary>
    public string AccessibleName => _accessibleName;

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
        if (e.PropertyName is { Length: > 0 } name && !RelevantCardProperties.Contains(name))
        {
            return;
        }

        Refresh(notify: true);
    }

    // Recomputes every presented value from the card and announces only the ones that changed.
    private void Refresh(bool notify)
    {
        var health = Card.Health;
        var cpu = ServerMetricPresentation.Read(Card, ServerMetricKind.Cpu, _thresholds);
        var memory = ServerMetricPresentation.Read(Card, ServerMetricKind.Memory, _thresholds);
        var disk = ServerMetricPresentation.Read(Card, ServerMetricKind.Disk, _thresholds);
        var stale = !ServerStatusPresentation.RowHidesRetainedMetrics(health)
            && ServerStatusPresentation.ShowsRetainedReadingAsStale(health, Card.IsStale, Card.HasMetrics);
        var statusText = ServerStatusPresentation.StatusText(health, _localization);

        Set(ref _health, health, nameof(Health), notify);
        Set(ref _statusText, statusText, nameof(StatusText), notify);
        SetMetric(ref _cpu, ref _cpuText, cpu, nameof(CpuText), nameof(IsCpuKnown), nameof(CpuBarValue), nameof(CpuSeverity), notify);
        SetMetric(ref _memory, ref _memoryText, memory, nameof(MemoryText), nameof(IsMemoryKnown), nameof(MemoryBarValue), nameof(MemorySeverity), notify);
        SetMetric(ref _disk, ref _diskText, disk, nameof(DiskText), nameof(IsDiskKnown), nameof(DiskBarValue), nameof(DiskSeverity), notify);
        Set(ref _showsStaleCue, stale, nameof(ShowsStaleCue), notify);
        Set(ref _staleAgeText, stale ? Card.StaleAgeDisplay : null, nameof(StaleAgeText), notify);
        Set(
            ref _accessibleName,
            Format("CompactRowAccessibleFormat", Name, statusText, AccessibleMetric(cpu), AccessibleMetric(memory), AccessibleMetric(disk)),
            nameof(AccessibleName),
            notify);
    }

    private void SetMetric(
        ref ServerMetricReading field,
        ref string text,
        ServerMetricReading value,
        string textName,
        string knownName,
        string barName,
        string severityName,
        bool notify)
    {
        var previous = field;
        field = value;
        Set(ref text, ServerMetricPresentation.Text(value, _localization), textName, notify);
        if (!notify)
        {
            return;
        }

        if (previous.IsKnown != value.IsKnown)
        {
            OnPropertyChanged(knownName);
        }

        if (!previous.Value.Equals(value.Value))
        {
            OnPropertyChanged(barName);
        }

        if (previous.Severity != value.Severity)
        {
            OnPropertyChanged(severityName);
        }
    }

    private void Set<T>(ref T field, T value, string propertyName, bool notify)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        if (notify)
        {
            OnPropertyChanged(propertyName);
        }
    }

    // "92% em atenção" / "97% crítico" / "22%" / "sem dados": the cue is text, never colour alone (G-14, R-5) - the shared
    // rule (UI.10 F24: the Servidores rows read the same).
    private string AccessibleMetric(ServerMetricReading reading) =>
        ServerMetricPresentation.AccessibleWithSeverity(reading, _localization);

    private string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, _localization.GetString(key), args);
}
