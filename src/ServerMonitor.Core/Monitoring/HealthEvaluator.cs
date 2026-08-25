using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// Derives a metrics-based health from a snapshot. The overall severity is the maximum
/// across the available metrics (Critical &gt; Warning &gt; Healthy). An unknown metric is
/// ignored, never treated as zero; if every relevant metric is unknown the result is
/// <see cref="ServerHealth.Unknown"/>. This evaluator never returns
/// <see cref="ServerHealth.Offline"/> — reachability is decided by the engine, not by values.
/// </summary>
public static class HealthEvaluator
{
    public static ServerHealth EvaluateFromMetrics(
        ServerMetricsSnapshot? snapshot,
        MonitoringThresholds? thresholds = null)
    {
        if (snapshot is null)
        {
            return ServerHealth.Unknown;
        }

        var policy = thresholds ?? MonitoringThresholds.Default;
        var worst = ServerHealth.Unknown;

        worst = Escalate(worst, Severity(snapshot.CpuUsagePercent, policy.CpuWarning, policy.CpuCritical));
        worst = Escalate(worst, Severity(snapshot.MemoryUsagePercent, policy.MemoryWarning, policy.MemoryCritical));
        worst = Escalate(worst, Severity(snapshot.DiskUsagePercent, policy.DiskWarning, policy.DiskCritical));

        return worst;
    }

    private static ServerHealth? Severity(double? value, double warning, double critical)
    {
        if (value is not { } percent || double.IsNaN(percent))
        {
            return null;
        }

        if (percent >= critical)
        {
            return ServerHealth.Critical;
        }

        return percent >= warning ? ServerHealth.Warning : ServerHealth.Healthy;
    }

    // Unknown is the "no data" floor; any concrete severity replaces it, and higher
    // severities win. Rank keeps the comparison independent of enum ordering intent.
    private static ServerHealth Escalate(ServerHealth current, ServerHealth? candidate) =>
        candidate is { } value && Rank(value) > Rank(current) ? value : current;

    private static int Rank(ServerHealth health) => health switch
    {
        ServerHealth.Healthy => 1,
        ServerHealth.Warning => 2,
        ServerHealth.Critical => 3,
        _ => 0
    };
}
