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

    /// <summary>Figma 112:1018: the bar is 213 wide, segments h12 radius 6, gap 5; a discrete segment is at most 30 wide.</summary>
    public const double HealthBarWidth = 213;

    public const double HealthBarGap = 5;

    public const double HealthSegmentMaxWidth = 30;

    /// <summary>A segment never gets narrower than its height (12), so the radius-6 pill stays a pill.</summary>
    public const double HealthSegmentMinWidth = 12;

    /// <summary>
    /// The rendered bar: each segment's width inside the fixed 213-wide bar (minus the gaps), proportional to its weight
    /// and capped at 30 — 6 servers give the Figma's 6 × 30; 12 share the width; above the cap the per-state segments are
    /// proportional. Pure; the View only binds the widths.
    /// </summary>
    public static IReadOnlyList<HealthBarSegment> BarSegments(HealthSummary summary)
    {
        var segments = Segments(summary);
        if (segments.Count == 0)
        {
            return [];
        }

        var available = HealthBarWidth - (HealthBarGap * (segments.Count - 1));
        if (summary.Total <= MaxDiscreteHealthSegments)
        {
            // Discrete (one per server): equal widths, at most 30 (6 servers = the Figma's 6 × 30; 12 ≈ 13 each).
            var width = Math.Round(Math.Min(HealthSegmentMaxWidth, available / segments.Count), 2);
            return segments.Select(segment => new HealthBarSegment(segment.Health, width, segment.Weight)).ToList();
        }

        // Aggregated (one per present state), Prism r1 (a): reserve the minimum for EVERY present state first, then share
        // the rest by count — so the bar always sums to exactly 213 and no present state can vanish.
        var total = segments.Sum(segment => segment.Weight);
        var spare = available - (HealthSegmentMinWidth * segments.Count);
        return segments
            .Select(segment => new HealthBarSegment(
                segment.Health,
                Math.Round(HealthSegmentMinWidth + (spare * segment.Weight / total), 2),
                segment.Weight))
            .ToList();
    }

    /// <summary>The severity of one metric under the engine's limits (inclusive); Healthy when below or unknown.</summary>
    public static ServerHealth MetricSeverity(double? percent, double warning, double critical) =>
        percent is not { } value || double.IsNaN(value) || value < warning
            ? ServerHealth.Healthy
            : value >= critical ? ServerHealth.Critical : ServerHealth.Warning;

    /// <summary>
    /// "host" for the default SSH port, "host:port" otherwise — the real configured endpoint, never invented. IPv6
    /// literals are bracketed when a port is shown.
    /// </summary>
    public static string Address(string host, int port)
    {
        // Cortex B1 N-2: one IPv6-bracket rule - the Endpoint format whenever a port is shown.
        return port == 22 ? host : Endpoint(host, port);
    }

    /// <summary>
    /// UI.5 A-13: the Detail page's address — the current <c>Endpoint</c> format, always "host:port" with no spaces, an
    /// IPv6 literal bracketed (<c>[::1]:22</c>). The real configured endpoint, never invented.
    /// </summary>
    public static string Endpoint(string host, int port)
    {
        var value = host ?? string.Empty;
        return value.Contains(':', StringComparison.Ordinal) && !value.StartsWith('[') ? $"[{value}]:{port}" : $"{value}:{port}";
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
                // Cortex r3 NIT-1: the ONE inclusive comparison of the App (parity with the Core HealthEvaluator is tested).
                var severity = OverviewPresentation.MetricSeverity(value, warning, critical);
                if (severity == ServerHealth.Healthy)
                {
                    continue;
                }

                var candidate = new PriorityProblem(server.ServerId, server.Name, metric, severity, value!.Value);
                if (best is null || IsMoreUrgent(candidate, best))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    // Strictly more urgent only: on a full tie the earlier candidate (lower server index, then earlier metric) is kept.
    private static bool IsMoreUrgent(PriorityProblem candidate, PriorityProblem best)
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

        // Atlas r1 NIT-2: the list order is the loop order (indices only grow), so a full tie keeps the earlier
        // candidate by returning false; comparing the indices here could never be true.
        return false;
    }

    private static int Rank(ServerHealth health) => health == ServerHealth.Critical ? 2 : 1;
}

/// <summary>
/// One rendered segment of the health bar: its state, its width in the 213-wide bar and the number of servers it stands
/// for (1 when discrete). An aggregated segment carries its count as a tooltip ("496 saudáveis", Prism r1 a).
/// </summary>
public sealed record HealthBarSegment(ServerHealth Health, double Width, int Count, string? ToolTip = null)
{
    public bool HasToolTip => ToolTip is not null;
}

/// <summary>One exception chip of the health card ("1 atenção"): the state, its count and the localized text.</summary>
public sealed record HealthChip(ServerHealth Health, int Count, string Text);

/// <summary>Where keyboard focus returns on the Visão geral (Beacon r1 SHOULD-1).</summary>
public enum OverviewReturnTarget
{
    None,
    ServerRow,
    Priority,
    DirectoryLink
}
