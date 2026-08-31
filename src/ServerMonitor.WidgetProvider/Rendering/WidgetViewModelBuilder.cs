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
    /// <summary>
    /// Max servers rendered per size. Small shows a summary only.
    /// <para>
    /// These are HOST-CAPACITY limits measured on the real Windows Widgets board, not arbitrary caps: the
    /// host gives each size a FIXED card height and silently clips whatever does not fit. Both values were
    /// wrong before and had to be measured (M13-QA-4 Medium, M13-QA-5 Large / P-017):
    /// </para>
    /// <list type="bullet">
    /// <item>Medium held 2 instrument-panel blocks, not 3. The third was clipped, and the "+N more" line
    /// that follows the blocks was clipped with it.</item>
    /// <item>Large held 3 blocks plus the fleet-summary footer, not 6. With 4-6 servers the old cap made
    /// <c>overflow</c> zero, so no affordance was emitted at all and the extra servers AND the footer
    /// disappeared with no indication whatsoever.</item>
    /// </list>
    /// <para>
    /// Both failures were the same bug: a cap validated against the view model instead of against what the
    /// host actually renders. Any change here MUST be re-verified on the real board with a fleet LARGER
    /// than the cap; a green view-model test only proves the arithmetic, never that the content fits.
    /// </para>
    /// </summary>
    public static int MaxRowsFor(WidgetSizeHint size) => size switch
    {
        WidgetSizeHint.Large => 3,
        WidgetSizeHint.Medium => 2,
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
        // TRUTHFUL DEGRADATION INVARIANT (M13-QA-4 / QA-5): visible + overflow == total, always, and any
        // size that renders rows must announce every server it does not render. A server can never vanish
        // from the card without the user being told. Small is exempt by construction: it renders no rows at
        // all and is honest about being a fleet verdict (N/N + gauge), so it hides nothing it implied.
        var overflow = Math.Max(0, snapshot.Servers.Count - maxRows);

        return new WidgetViewModel
        {
            DisplayState = WidgetDisplayState.Available,
            Size = size,
            BrandName = strings.BrandName,
            OverallHealth = snapshot.OverallHealth,
            OverallHealthLabel = strings.HealthLabel(snapshot.OverallHealth),
            OverallHealthColor = HealthColor(snapshot.OverallHealth),
            HeroValue = $"{counts.Healthy}/{counts.Total}",
            HeroLabel = counts.Healthy == counts.Total
                ? strings.HealthyPlural
                : strings.HealthLabel(snapshot.OverallHealth),
            CpuLabel = strings.Cpu,
            MemoryLabel = strings.Memory,
            DiskLabel = strings.Disk,
            FleetKicker = strings.FleetKicker,
            HealthyLabel = strings.HealthyPlural,
            WarningLabel = strings.Warning,
            CriticalLabel = strings.Critical,
            OfflineLabel = strings.Offline,
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
            MetricsText: metrics)
        {
            CpuFraction = Fraction(server.CpuUsagePercent),
            MemoryFraction = Fraction(server.MemoryUsagePercent),
            DiskFraction = Fraction(server.DiskUsagePercent),
            CpuDetail = FormatUptime(server.UptimeSeconds),
            MemoryDetail = FormatGb(server.MemoryUsedGb, server.MemoryTotalGb),
            DiskDetail = FormatGb(server.DiskUsedGb, server.DiskTotalGb)
        };
    }

    // "3.1 / 8 GB" using the UI culture's number format; empty when either value is unknown.
    private static string FormatGb(double? used, double? total) =>
        used is { } u && total is { } t && t > 0
            ? string.Format(CultureInfo.CurrentUICulture, "{0:0.#} / {1:0.#} GB", u, t)
            : string.Empty;

    // Compact uptime "43d 18h" / "18h 30m" / "45m"; empty when unknown. Two most-significant units only.
    private static string FormatUptime(long? seconds)
    {
        if (seconds is not { } s || s <= 0)
        {
            return string.Empty;
        }

        var t = TimeSpan.FromSeconds(s);
        if (t.TotalDays >= 1)
        {
            return $"{(int)t.TotalDays}d {t.Hours}h";
        }

        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    // Meter fill fraction [0,1]; null stays -1 (unknown → neutral/empty meter, never full or 0%, §19).
    private static double Fraction(double? value) =>
        value is { } v ? Math.Clamp(v, 0d, 100d) / 100d : -1d;

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
