using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.4 fleet-health aggregate. Counts come ONLY from the engine-owned <see cref="ServerHealth"/> of each visible server
/// (<see cref="ServerCardViewModel.Health"/>); no new definition of health exists in the presentation layer.
/// </summary>
public sealed record HealthSummary(int Total, int Healthy, int Warning, int Critical, int Offline, int Unknown)
{
    public static HealthSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public static HealthSummary From(IEnumerable<ServerHealth> healths)
    {
        ArgumentNullException.ThrowIfNull(healths);
        int total = 0, healthy = 0, warning = 0, critical = 0, offline = 0, unknown = 0;
        foreach (var health in healths)
        {
            total++;
            switch (health)
            {
                case ServerHealth.Healthy: healthy++; break;
                case ServerHealth.Warning: warning++; break;
                case ServerHealth.Critical: critical++; break;
                case ServerHealth.Offline: offline++; break;
                default: unknown++; break;
            }
        }

        return new HealthSummary(total, healthy, warning, critical, offline, unknown);
    }

    public int CountOf(ServerHealth health) => health switch
    {
        ServerHealth.Healthy => Healthy,
        ServerHealth.Warning => Warning,
        ServerHealth.Critical => Critical,
        ServerHealth.Offline => Offline,
        _ => Unknown
    };
}

/// <summary>One segment of the fleet-health bar: a state and its relative weight (1 per server, or a count when aggregated).</summary>
public sealed record HealthSegment(ServerHealth Health, int Weight);

/// <summary>Pure rules behind the overview's health bar and counts.</summary>
public static class OverviewPresentation
{
    /// <summary>
    /// DERIVED (pending Prism): up to this many servers the bar shows one segment per server (Figma 112:1018 shows 6);
    /// above it, one proportional segment per present state, so the bar never grows without limit.
    /// </summary>
    public const int MaxDiscreteHealthSegments = 12;

    /// <summary>
    /// DERIVED (pending Prism): the overview's summary list shows at most this many rows; the rest is reached through
    /// "Ver todos" (the Servidores page, which virtualizes). Figma 112:1056 shows 6.
    /// </summary>
    public const int OverviewListLimit = 8;

    /// <summary>Bar order: healthy first, then increasing severity, then no data (Figma: green… amber, red).</summary>
    public static IReadOnlyList<ServerHealth> SegmentOrder { get; } =
        [ServerHealth.Healthy, ServerHealth.Warning, ServerHealth.Critical, ServerHealth.Offline, ServerHealth.Unknown];

    /// <summary>The states that get an exception chip — only when their count is &gt; 0.</summary>
    public static IReadOnlyList<ServerHealth> ChipOrder { get; } =
        [ServerHealth.Warning, ServerHealth.Critical, ServerHealth.Offline, ServerHealth.Unknown];

    public static IReadOnlyList<HealthSegment> Segments(HealthSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var segments = new List<HealthSegment>();
        var discrete = summary.Total <= MaxDiscreteHealthSegments;
        foreach (var health in SegmentOrder)
        {
            var count = summary.CountOf(health);
            if (count == 0)
            {
                continue;
            }

            if (discrete)
            {
                for (var i = 0; i < count; i++)
                {
                    segments.Add(new HealthSegment(health, 1));
                }
            }
            else
            {
                segments.Add(new HealthSegment(health, count));
            }
        }

        return segments;
    }

    /// <summary>
    /// "host" for the default SSH port, "host:port" otherwise — the real configured endpoint, never invented. IPv6
    /// literals are bracketed when a port is shown.
    /// </summary>
    public static string Address(string host, int port)
    {
        if (port == 22)
        {
            return host;
        }

        return host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

    /// <summary>
    /// The servers directory search: name or address (host, and host:port), partial, case-insensitive
    /// (OrdinalIgnoreCase, as the UI.3 workloads search). A blank query matches everything.
    /// </summary>
    public static bool MatchesSearch(string name, string host, int port, string? query)
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            return true;
        }

        return (name ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
            || (host ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
            || Address(host ?? string.Empty, port).Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The metric a priority problem is about.</summary>
public enum PriorityMetric
{
    Cpu,
    Memory,
    Disk
}

/// <summary>One server as the priority rule sees it, in the overview list's order.</summary>
public sealed record PriorityServerInput(Guid ServerId, string Name, ServerHealth Health, ServerMetricsSnapshot? Snapshot);

/// <summary>The single most urgent metric problem (D-UI4-PRIORITY).</summary>
public sealed record PriorityProblem(
    Guid ServerId,
    string ServerName,
    PriorityMetric Metric,
    ServerHealth Severity,
    double Percent);

/// <summary>
/// D-UI4-PRIORITY, as a pure class. Only metrics above the limits the engine ALREADY uses (the injected
/// <see cref="MonitoringThresholds"/>, inclusive, same semantics as <see cref="HealthEvaluator"/>). Candidates: servers
/// with a snapshot whose engine health is Warning or Critical, one per metric with a known percentage ≥ its warning
/// limit. Deterministic order: severity (Critical &gt; Warning) → higher percentage → the server's position in the list →
/// metric order (CPU, memory, disk). Offline/Unknown servers and servers without a snapshot never produce a candidate:
/// nothing is fabricated.
/// </summary>
public sealed class PriorityProblemSelector(MonitoringThresholds thresholds)
{
    private readonly MonitoringThresholds _thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));

    public MonitoringThresholds Thresholds => _thresholds;

    public PriorityProblem? Select(IReadOnlyList<PriorityServerInput> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        PriorityProblem? best = null;
        var bestIndex = int.MaxValue;
        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            if (server.Snapshot is not { } snapshot
                || server.Health is not (ServerHealth.Warning or ServerHealth.Critical))
            {
                continue;
            }

            foreach (var (metric, value, warning, critical) in new[]
                     {
                         (PriorityMetric.Cpu, snapshot.CpuUsagePercent, _thresholds.CpuWarning, _thresholds.CpuCritical),
                         (PriorityMetric.Memory, snapshot.MemoryUsagePercent, _thresholds.MemoryWarning, _thresholds.MemoryCritical),
                         (PriorityMetric.Disk, snapshot.DiskUsagePercent, _thresholds.DiskWarning, _thresholds.DiskCritical)
                     })
            {
                if (value is not { } percent || double.IsNaN(percent) || percent < warning)
                {
                    continue;
                }

                var severity = percent >= critical ? ServerHealth.Critical : ServerHealth.Warning;
                var candidate = new PriorityProblem(server.ServerId, server.Name, metric, severity, percent);
                if (best is null || IsMoreUrgent(candidate, index, best, bestIndex))
                {
                    best = candidate;
                    bestIndex = index;
                }
            }
        }

        return best;
    }

    // Strictly more urgent only: on a full tie the earlier candidate (lower server index, then earlier metric) is kept.
    private static bool IsMoreUrgent(PriorityProblem candidate, int candidateIndex, PriorityProblem best, int bestIndex)
    {
        var bySeverity = Rank(candidate.Severity).CompareTo(Rank(best.Severity));
        if (bySeverity != 0)
        {
            return bySeverity > 0;
        }

        var byPercent = candidate.Percent.CompareTo(best.Percent);
        if (byPercent != 0)
        {
            return byPercent > 0;
        }

        return candidateIndex < bestIndex;
    }

    private static int Rank(ServerHealth health) => health == ServerHealth.Critical ? 2 : 1;
}

/// <summary>One exception chip of the health card ("1 atenção"): the state, its count and the localized text.</summary>
public sealed record HealthChip(ServerHealth Health, int Count, string Text);
