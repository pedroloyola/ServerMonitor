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
/// </summary>
public static class WidgetSnapshotMapper
{
    /// <summary>
    /// Builds a snapshot from the current fleet. <paramref name="stateOf"/> and
    /// <paramref name="metricsOf"/> read the live per-server monitoring state and last metrics — the
    /// same sources the dashboard binds to — so the snapshot reflects exactly what the app shows.
    /// </summary>
    public static WidgetStateSnapshot Map(
        IReadOnlyList<Server> servers,
        Func<Guid, ServerMonitoringState> stateOf,
        Func<Guid, ServerMetricsSnapshot?> metricsOf,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(stateOf);
        ArgumentNullException.ThrowIfNull(metricsOf);

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

            included.Add(new WidgetServerState
            {
                Id = server.Id,
                DisplayName = WidgetDisplayName.Sanitize(server.Name),
                Health = MapHealth(state.Health),
                CpuUsagePercent = Normalize(metrics?.CpuUsagePercent),
                MemoryUsagePercent = Normalize(metrics?.MemoryUsagePercent),
                DiskUsagePercent = Normalize(metrics?.DiskUsagePercent),
                MemoryUsedGb = Gib(metrics?.MemoryUsedBytes),
                MemoryTotalGb = Gib(metrics?.MemoryTotalBytes),
                DiskUsedGb = Gib(metrics?.DiskUsedBytes),
                DiskTotalGb = Gib(metrics?.DiskTotalBytes),
                UptimeSeconds = metrics?.Uptime is { } up && up > TimeSpan.Zero ? (long)up.TotalSeconds : null,
                LastUpdatedUtc = state.LastSuccessAt
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
