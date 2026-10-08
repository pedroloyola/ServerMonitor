using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>The three row metrics.</summary>
public enum ServerMetricKind
{
    Cpu,
    Memory,
    Disk
}

/// <summary>
/// One metric as a server ROW presents it: whether its percentage is known (never for an Offline server, whose retained
/// reading the rows hide), the value a bar may draw (0 only when unknown - the bar then draws nothing), and its
/// severity against the engine's own thresholds.
/// </summary>
public readonly record struct ServerMetricReading(bool IsKnown, double Value, ServerHealth Severity);

/// <summary>
/// UI.8 RC-6: the ONE implementation of the row metric rules, shared by the UI.4 overview / Servidores row
/// (<see cref="ServerDirectoryRowViewModel"/>) and the Compact row (<see cref="CompactServerRowViewModel"/>): unknown or
/// Offline → "—" and no bar ("—", never 0%), severity from <see cref="OverviewPresentation.MetricSeverity"/> with the
/// SAME <see cref="MonitoringThresholds"/> instance the engine is composed with. Pure: no state, no clock.
/// </summary>
public static class ServerMetricPresentation
{
    public static ServerMetricReading Read(ServerCardViewModel card, ServerMetricKind kind, MonitoringThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(thresholds);
        var (hasPercent, value, warning, critical) = kind switch
        {
            ServerMetricKind.Cpu => (card.HasCpuPercent, card.CpuUsageValue, thresholds.CpuWarning, thresholds.CpuCritical),
            ServerMetricKind.Memory => (card.HasMemoryPercent, card.MemoryUsageValue, thresholds.MemoryWarning, thresholds.MemoryCritical),
            ServerMetricKind.Disk => (card.HasDiskPercent, card.DiskUsageValue, thresholds.DiskWarning, thresholds.DiskCritical),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        // An Offline server shows "—", not its retained (stale) snapshot (ServerStatusPresentation.RowHidesRetainedMetrics).
        var known = hasPercent && !ServerStatusPresentation.RowHidesRetainedMetrics(card.Health);
        return known
            ? new ServerMetricReading(true, value, OverviewPresentation.MetricSeverity(value, warning, critical))
            : new ServerMetricReading(false, 0, ServerHealth.Healthy);
    }

    /// <summary>"22%", or the "—" text when the percentage is unknown - never 0.</summary>
    public static string Text(ServerMetricReading reading, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return reading.IsKnown
            ? string.Format(CultureInfo.CurrentUICulture, "{0:0}%", reading.Value)
            : localization.GetString("ServerMetricUnavailable");
    }

    /// <summary>The accessible value ("22%", or "sem dados"), the one <see cref="ServerStatusPresentation.AccessiblePercent"/> spells.</summary>
    public static string Accessible(ServerMetricReading reading, ILocalizationService localization) =>
        ServerStatusPresentation.AccessiblePercent(reading.IsKnown, reading.Value, localization);
}
