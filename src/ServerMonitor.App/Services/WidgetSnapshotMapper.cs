using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Services;

/// <summary>
/// Pure Core→wire mapping for the widget snapshot. No I/O, fully testable. It reuses the product's
/// per-server health exactly as the engine computed it (§20 — never recomputes thresholds, so the
/// widget can't disagree with the dashboard), derives the fleet overall via the shared precedence
/// (§21), and enforces data minimization by construction: only an opaque id, a sanitized name, health,
/// normalized percentages, and a freshness timestamp cross the boundary (§9). Hidden servers are
/// excluded — only the visible/active fleet appears (§34) — and the result is capped at
/// <see cref="WidgetSchema.MaxServers"/> (§18).
/// <para>
/// UI.9 B1 adds two optional fields, both derived from rules the app ALREADY owns, never a second copy:
/// <c>attentionMetric</c> is the metric of D-UI4-PRIORITY's <see cref="PriorityProblemSelector"/> run over
/// this one server with the engine's own thresholds (D-UI9-2), and <c>staleAfterSeconds</c> is
/// <see cref="StalePolicy"/> over the server's normalized <see cref="RefreshIntervalPolicy"/> interval
/// (D-UI9-3). Only the metric is copied from the selector's result — never its name or severity (V-RC-6).
/// </para>
/// </summary>
public static class WidgetSnapshotMapper
{
    /// <summary>
    /// Builds a snapshot from the current fleet. <paramref name="stateOf"/> and
    /// <paramref name="metricsOf"/> read the live per-server monitoring state and last metrics — the
    /// same sources the dashboard binds to — so the snapshot reflects exactly what the app shows.
    /// <paramref name="thresholds"/> must be the engine's own instance (<c>MonitoringOptions.Thresholds</c>).
    /// </summary>
    public static WidgetStateSnapshot Map(
        IReadOnlyList<Server> servers,
        Func<Guid, ServerMonitoringState> stateOf,
        Func<Guid, ServerMetricsSnapshot?> metricsOf,
        MonitoringThresholds thresholds,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(stateOf);
        ArgumentNullException.ThrowIfNull(metricsOf);
        ArgumentNullException.ThrowIfNull(thresholds);

        var priority = new PriorityProblemSelector(thresholds);

        var included = new List<WidgetServerState>(Math.Min(servers.Count, WidgetSchema.MaxServers));
        foreach (var server in servers)
        {
            if (server.IsHidden)
            {
                // §34: only servers that are part of the active/visible fleet enter the widget.
                continue;
            }

            if (included.Count >= WidgetSchema.MaxServers)
            {
                // §18: hard bound; the (rare) overflow is dropped deterministically in fleet order.
                break;
            }

            var state = stateOf(server.Id);
            var metrics = metricsOf(server.Id);
            var health = MapHealth(state.Health);

            included.Add(new WidgetServerState
            {
                Id = server.Id,
                DisplayName = WidgetDisplayName.Sanitize(server.Name),
                Health = health,
                CpuUsagePercent = Normalize(metrics?.CpuUsagePercent),
                MemoryUsagePercent = Normalize(metrics?.MemoryUsagePercent),
                DiskUsagePercent = Normalize(metrics?.DiskUsagePercent),
                MemoryUsedGb = Gib(metrics?.MemoryUsedBytes),
                MemoryTotalGb = Gib(metrics?.MemoryTotalBytes),
                DiskUsedGb = Gib(metrics?.DiskUsedBytes),
                DiskTotalGb = Gib(metrics?.DiskTotalBytes),
                UptimeSeconds = metrics?.Uptime is { } up && up > TimeSpan.Zero ? (long)up.TotalSeconds : null,
                LastUpdatedUtc = state.LastSuccessAt,
                AttentionMetric = AttentionMetric(priority, server.Id, state.Health, metrics),
                StaleAfterSeconds = StaleAfterSeconds(server.RefreshIntervalSeconds)
            });
        }

        var overall = WidgetHealthPrecedence.Worst(SelectHealth(included));

        return new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = generatedAtUtc,
            OverallHealth = overall,
            Servers = included
        };
    }

    /// <summary>Maps domain health to wire health 1:1 — the single source of truth stays the engine.</summary>
    public static WidgetHealth MapHealth(ServerHealth health) => health switch
    {
        ServerHealth.Healthy => WidgetHealth.Healthy,
        ServerHealth.Warning => WidgetHealth.Warning,
        ServerHealth.Critical => WidgetHealth.Critical,
        ServerHealth.Offline => WidgetHealth.Offline,
        _ => WidgetHealth.Unknown
    };

    /// <summary>
    /// The per-server stale threshold the app itself applies: <see cref="StalePolicy.StaleAfter"/> over the
    /// normalized refresh interval. Always inside the contract bounds (proved by test, V-RC-4).
    /// </summary>
    public static int StaleAfterSeconds(int refreshIntervalSeconds) =>
        (int)StalePolicy.StaleAfter(RefreshIntervalPolicy.ToInterval(refreshIntervalSeconds)).TotalSeconds;

    // D-UI9-2: the priority rule over this single server. The selector already yields nothing unless the
    // ENGINE says Warning/Critical and a snapshot exists (Offline/Unknown never produce a reason). The name
    // is deliberately not passed and only the metric is read back (V-RC-6c).
    private static string? AttentionMetric(
        PriorityProblemSelector priority, Guid serverId, ServerHealth health, ServerMetricsSnapshot? metrics)
    {
        var problem = priority.Select([new PriorityServerInput(serverId, string.Empty, health, metrics)]);
        return problem?.Metric switch
        {
            PriorityMetric.Cpu => WidgetAttentionMetrics.Cpu,
            PriorityMetric.Memory => WidgetAttentionMetrics.Memory,
            PriorityMetric.Disk => WidgetAttentionMetrics.Disk,
            _ => null
        };
    }

    // null stays null (unknown ≠ zero, §19); a present value is clamped into [0, 100], and a non-finite
    // value degrades to unknown rather than emitting NaN/Infinity onto the wire.
    private static double? Normalize(double? value)
    {
        if (value is not { } percent || double.IsNaN(percent) || double.IsInfinity(percent))
        {
            return null;
        }

        return Math.Clamp(percent, 0d, 100d);
    }

    // Bytes → GiB; null/negative stays null (unknown ≠ zero). A benign resource size, not sensitive.
    private static double? Gib(long? bytes) =>
        bytes is { } b && b >= 0 ? b / 1073741824d : null;

    private static IEnumerable<WidgetHealth> SelectHealth(List<WidgetServerState> servers)
    {
        foreach (var server in servers)
        {
            yield return server.Health;
        }
    }
}
