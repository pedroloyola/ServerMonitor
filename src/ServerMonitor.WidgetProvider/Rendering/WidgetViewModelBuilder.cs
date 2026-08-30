using System.Globalization;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>
/// Builds the render-ready <see cref="WidgetViewModel"/> from a snapshot read (§24). Pure: it applies
/// ordering (§10), per-size capping and the "+N more" overflow (§8/§9), integer metric formatting with a
/// neutral placeholder for unknown (§19/§20 — never 0-for-unknown), relative freshness text (§23), and
/// localized copy (§16). Uses the snapshot's already-computed OverallHealth (never recomputes, §11) and
/// keeps privacy: only sanitized display names + normalized metrics reach the model (§15).
/// </summary>
public static class WidgetViewModelBuilder
{
    /// <summary>Max servers rendered per size. Small shows a summary only.</summary>
    public static int MaxRowsFor(WidgetSizeHint size) => size switch
    {
        WidgetSizeHint.Large => 6,
        WidgetSizeHint.Medium => 3,
        _ => 0
    };

    private const int MaxNameLength = 22;

    public static WidgetViewModel Build(
        WidgetReadResult read,
        WidgetSizeHint size,
        DateTimeOffset nowUtc,
        WidgetStrings strings,
        TimeSpan? staleThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(strings);

        if (!read.IsAvailable || read.Snapshot is not { } snapshot)
        {
            return Unavailable(size, strings);
        }

        var freshnessState = WidgetFreshness.Evaluate(read, nowUtc, staleThreshold);
        var freshnessText = FreshnessText(snapshot.GeneratedAtUtc, nowUtc, strings);

        if (snapshot.Servers.Count == 0)
        {
            return Empty(size, strings, freshnessState, freshnessText);
        }

        var counts = CountByHealth(snapshot.Servers);
        var ordered = WidgetOrdering.ForDisplay(snapshot.Servers);
        var maxRows = MaxRowsFor(size);
        var shown = ordered.Take(maxRows).Select(s => ToRow(s, strings)).ToArray();
        var overflow = Math.Max(0, snapshot.Servers.Count - maxRows);

        return new WidgetViewModel
        {
            DisplayState = WidgetDisplayState.Available,
            Size = size,
            BrandName = strings.BrandName,
            OverallHealth = snapshot.OverallHealth,
            OverallHealthLabel = strings.HealthLabel(snapshot.OverallHealth),
            OverallHealthColor = HealthColor(snapshot.OverallHealth),
            PrimarySummary = PrimarySummary(snapshot.OverallHealth, counts, strings),
            CountsSummary = CountsSummary(counts, strings),
            Freshness = freshnessState,
            FreshnessText = freshnessText,
            TotalServers = snapshot.Servers.Count,
            HealthyCount = counts.Healthy,
            WarningCount = counts.Warning,
            CriticalCount = counts.Critical,
            OfflineCount = counts.Offline,
            UnknownCount = counts.Unknown,
            Rows = shown,
            OverflowCount = overflow,
            OverflowText = overflow > 0
                ? string.Format(CultureInfo.InvariantCulture, strings.MoreCount, overflow)
                : string.Empty
        };
    }

    /// <summary>Adaptive Card colour for a health — text always carries the label too (§18).</summary>
    public static string HealthColor(WidgetHealth health) => health switch
    {
        WidgetHealth.Healthy => "good",
        WidgetHealth.Warning => "warning",
        WidgetHealth.Critical => "attention",
        WidgetHealth.Offline => "attention",
        _ => "default"
    };

    private static WidgetServerRow ToRow(WidgetServerState server, WidgetStrings strings)
    {
        var cpu = FormatPercent(server.CpuUsagePercent, strings);
        var mem = FormatPercent(server.MemoryUsagePercent, strings);
        var disk = FormatPercent(server.DiskUsagePercent, strings);

        // Localized metric line (§16): "CPU 12% · Memória 34% · Disco 56%".
        var metrics = $"{strings.Cpu} {cpu} · {strings.Memory} {mem} · {strings.Disk} {disk}";

        return new WidgetServerRow(
            ServerId: server.Id,
            DisplayName: TruncateName(server.DisplayName, strings),
            Health: server.Health,
            HealthLabel: strings.HealthLabel(server.Health),
            HealthColor: HealthColor(server.Health),
            CpuText: cpu,
            MemoryText: mem,
            DiskText: disk,
            MetricsText: metrics);
    }

    private static string TruncateName(string name, WidgetStrings strings)
    {
        if (string.IsNullOrEmpty(name))
        {
            return strings.NeutralServerName; // never fall back to IP/host (§15)
        }

        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        // Trim on a rune boundary so a surrogate pair is never split.
        var slice = name.AsSpan(0, MaxNameLength - 1);
        if (char.IsHighSurrogate(slice[^1]))
        {
            slice = slice[..^1];
        }

        return string.Concat(slice, "…");
    }

    // null stays a neutral placeholder — never 0% (§19). A present value is a rounded integer percent.
    private static string FormatPercent(double? value, WidgetStrings strings)
    {
        if (value is not { } percent)
        {
            return strings.MetricUnknown;
        }

        var rounded = (int)Math.Round(Math.Clamp(percent, 0d, 100d), MidpointRounding.AwayFromZero);
        return rounded.ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string FreshnessText(DateTimeOffset generatedAt, DateTimeOffset nowUtc, WidgetStrings strings)
    {
        var age = nowUtc - generatedAt;
        if (age < TimeSpan.FromMinutes(1))
        {
            return strings.UpdatedJustNow;
        }

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = (int)age.TotalMinutes;
            return string.Format(CultureInfo.InvariantCulture, strings.UpdatedMinutesAgo, minutes);
        }

        var hours = (int)age.TotalHours;
        return string.Format(CultureInfo.InvariantCulture, strings.UpdatedHoursAgo, hours);
    }

    private static string PrimarySummary(WidgetHealth overall, HealthCounts counts, WidgetStrings strings)
    {
        // "All servers healthy" only when literally nothing else is present (§21/§22).
        if (counts.Healthy == counts.Total && counts.Total > 0)
        {
            return strings.AllHealthy;
        }

        return strings.HealthLabel(overall);
    }

    private static string CountsSummary(HealthCounts counts, WidgetStrings strings)
    {
        var parts = new List<string>(3);
        if (counts.Healthy > 0)
        {
            parts.Add(WidgetStrings.Plural(counts.Healthy, strings.HealthyCountLabelOne, strings.HealthyCountLabel));
        }

        var needAttention = counts.Warning + counts.Critical + counts.Offline;
        if (needAttention > 0)
        {
            parts.Add(WidgetStrings.Plural(needAttention, strings.NeedAttentionLabelOne, strings.NeedAttentionLabel));
        }

        // Unknown is always surfaced separately — never folded into healthy or attention (§21).
        if (counts.Unknown > 0)
        {
            parts.Add(WidgetStrings.Plural(counts.Unknown, strings.UnknownCountLabelOne, strings.UnknownCountLabel));
        }

        return string.Join(" · ", parts);
    }

    private static HealthCounts CountByHealth(IReadOnlyList<WidgetServerState> servers)
    {
        var counts = new HealthCounts();
        foreach (var server in servers)
        {
            switch (server.Health)
            {
                case WidgetHealth.Healthy: counts.Healthy++; break;
                case WidgetHealth.Warning: counts.Warning++; break;
                case WidgetHealth.Critical: counts.Critical++; break;
                case WidgetHealth.Offline: counts.Offline++; break;
                default: counts.Unknown++; break;
            }
        }

        return counts;
    }

    private static WidgetViewModel Empty(
        WidgetSizeHint size, WidgetStrings strings, WidgetFreshnessState freshness, string freshnessText) => new()
    {
        DisplayState = WidgetDisplayState.Empty,
        Size = size,
        BrandName = strings.BrandName,
        OverallHealth = WidgetHealth.Unknown,
        OverallHealthLabel = strings.Unknown,
        OverallHealthColor = HealthColor(WidgetHealth.Unknown),
        PrimarySummary = strings.NoServers,
        CountsSummary = string.Empty,
        Freshness = freshness,
        FreshnessText = freshnessText,
        Rows = Array.Empty<WidgetServerRow>(),
        NoServersText = strings.NoServers
    };

    private static WidgetViewModel Unavailable(WidgetSizeHint size, WidgetStrings strings) => new()
    {
        DisplayState = WidgetDisplayState.Unavailable,
        Size = size,
        BrandName = strings.BrandName,
        OverallHealth = WidgetHealth.Unknown,
        OverallHealthLabel = strings.Unknown,
        OverallHealthColor = HealthColor(WidgetHealth.Unknown),
        PrimarySummary = strings.NoDataTitle,
        CountsSummary = string.Empty,
        Freshness = WidgetFreshnessState.Unavailable,
        FreshnessText = string.Empty,
        Rows = Array.Empty<WidgetServerRow>(),
        NoDataTitle = strings.NoDataTitle,
        NoDataBody = strings.NoDataBody
    };

    private sealed class HealthCounts
    {
        public int Healthy;
        public int Warning;
        public int Critical;
        public int Offline;
        public int Unknown;

        public int Total => Healthy + Warning + Critical + Offline + Unknown;
    }
}
