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
/// <para>
/// UI.9 B2 adds the V3 states (SPEC §3) at view-model level: per-server freshness against the provider's
/// own clock and the snapshot's derived threshold (D-UI9-4/5), the card state, and Prism's binding copy.
/// Precedence: Offline/Critical/Warning keep their label when stale (staleness never hides attention);
/// only Healthy — and Unknown with an old reading — become "Not updated". Nothing says "All healthy" or
/// counts a server as healthy unless it is Healthy AND fresh. The raw <c>attentionMetric</c> string never
/// reaches the model: it is parsed to the allowlisted enum and only <see cref="WidgetStrings"/> text is
/// emitted, with severity from the engine's health (V-RC-6). Until C1 rewrites the renderer, a stale
/// snapshot also overrides the legacy hero/row labels so the current card never claims current health.
/// </para>
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

        // D-UI9-4: one effective threshold for the snapshot AND the per-server fallback.
        var threshold = staleThreshold ?? WidgetFreshness.DeriveSnapshotThreshold(snapshot);
        var freshnessState = WidgetFreshness.Evaluate(read, nowUtc, threshold);
        var freshnessText = FreshnessText(snapshot.GeneratedAtUtc, nowUtc, strings);
        var footerText = FooterText(snapshot.GeneratedAtUtc, nowUtc, freshnessState, strings);

        if (snapshot.Servers.Count == 0)
        {
            return Empty(size, strings, freshnessState, freshnessText, footerText);
        }

        bool IsFresh(WidgetServerState server) =>
            WidgetFreshness.IsServerFresh(server, freshnessState, threshold, nowUtc);

        var counts = CountByHealth(snapshot.Servers);
        var v3 = CountV3(snapshot.Servers, IsFresh);
        var stale = freshnessState == WidgetFreshnessState.Stale;
        var ordered = WidgetOrdering.ForDisplay(snapshot.Servers);
        var maxRows = MaxRowsFor(size);
        var shown = ordered.Take(maxRows).Select(s => ToRow(s, strings, IsFresh(s), stale)).ToArray();
        // TRUTHFUL DEGRADATION INVARIANT (M13-QA-4 / QA-5): for a size that renders rows, visible + overflow
        // == total, and every server not rendered is announced. A server can never vanish from the card
        // without the user being told.
        //
        // Small is exempt by construction rather than by omission: it renders no rows at all, so it never
        // implies a list, and its hero already states the full fleet as "healthy/total" with one gauge tick
        // per server. Its overflow is therefore zero, not "everything" - carrying a phantom "+N" that
        // SmallBody never draws would be a trap for the next person to touch it (Prism L3).
        var overflow = maxRows == 0 ? 0 : Math.Max(0, snapshot.Servers.Count - maxRows);
        var cardState = stale ? WidgetCardState.Stale
            : v3.Problems > 0 ? WidgetCardState.Attention
            : v3.NoCurrentData > 0 ? WidgetCardState.NoCurrentData
            : WidgetCardState.Healthy;
        var total = snapshot.Servers.Count;
        var lastState = Format(strings.StaleLastStateFormat, counts.Healthy, total);

        return new WidgetViewModel
        {
            DisplayState = WidgetDisplayState.Available,
            Size = size,
            BrandName = strings.BrandName,
            OverallHealth = snapshot.OverallHealth,
            // Legacy renderer fields: a stale snapshot never reads as current health (D-UI9-5, until C1).
            OverallHealthLabel = stale ? strings.StaleTitle : strings.HealthLabel(snapshot.OverallHealth),
            OverallHealthColor = stale ? HealthColor(WidgetHealth.Unknown) : HealthColor(snapshot.OverallHealth),
            HeroValue = $"{counts.Healthy}/{counts.Total}",
            HeroLabel = stale ? strings.StaleTitle
                : counts.Healthy == counts.Total
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
            PrimarySummary = stale ? strings.StaleTitle : PrimarySummary(snapshot.OverallHealth, counts, strings),
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
                : string.Empty,
            CardState = cardState,
            Title = cardState switch
            {
                WidgetCardState.Stale => strings.StaleTitle,
                WidgetCardState.Attention => WidgetStrings.Plural(v3.Problems, strings.FleetProblemsOne, strings.FleetProblemsOther),
                WidgetCardState.NoCurrentData => Format(strings.FleetUnknownOnly, v3.NoCurrentData),
                _ => strings.FleetAllHealthy
            },
            Subtitle = stale
                ? lastState
                : WidgetStrings.Plural(counts.Healthy + counts.Warning + counts.Critical,
                    strings.FleetConnectedOne, strings.FleetConnectedOther),
            Summary = stale ? strings.StaleTitle : CountsSummaryV3(v3, strings),
            FooterText = footerText,
            RowsOfTotalText = maxRows == 0 ? string.Empty : Format(strings.OverflowFormat, shown.Length, total),
            HealthyFreshCount = v3.HealthyFresh,
            RingAltText = stale ? lastState : Format(strings.RingAltFormat, v3.HealthyFresh, total)
        };
    }

    /// <summary>
    /// V3 row state (D-UI9-5): Offline/Critical/Warning keep their label whatever the freshness; Healthy
    /// that is not fresh becomes NotUpdated; Unknown becomes NotUpdated only if it HAD a reading (a server
    /// never read is "No data", which is the truer sentence).
    /// </summary>
    public static WidgetRowState RowState(WidgetServerState server, bool fresh) => server.Health switch
    {
        WidgetHealth.Offline => WidgetRowState.Offline,
        WidgetHealth.Critical => WidgetRowState.Critical,
        WidgetHealth.Warning => WidgetRowState.Warning,
        WidgetHealth.Healthy => fresh ? WidgetRowState.Healthy : WidgetRowState.NotUpdated,
        _ => fresh || server.LastUpdatedUtc is null ? WidgetRowState.Unknown : WidgetRowState.NotUpdated
    };

    private static string RowStatusText(WidgetRowState state, WidgetServerState server, WidgetStrings strings)
    {
        // V-RC-6: severity from the engine's health only; the metric only picks the noun.
        var metric = state is WidgetRowState.Warning or WidgetRowState.Critical
            ? WidgetAttentionMetrics.TryParse(server.AttentionMetric)
            : null;
        var critical = state == WidgetRowState.Critical;

        return (state, metric) switch
        {
            (WidgetRowState.Healthy, _) => strings.StatusHealthy,
            (WidgetRowState.Offline, _) => strings.StatusOffline,
            (WidgetRowState.NotUpdated, _) => strings.StatusRowStale,
            (_, WidgetAttentionMetric.Cpu) => critical ? strings.ReasonCpuCritical : strings.ReasonCpuWarning,
            (_, WidgetAttentionMetric.Memory) => critical ? strings.ReasonMemoryCritical : strings.ReasonMemoryWarning,
            (_, WidgetAttentionMetric.Disk) => critical ? strings.ReasonDiskCritical : strings.ReasonDiskWarning,
            (WidgetRowState.Critical, _) => strings.StatusCritical,
            (WidgetRowState.Warning, _) => strings.StatusWarning,
            _ => strings.StatusUnknown
        };
    }

    private static string RowStatusColor(WidgetRowState state) => state switch
    {
        WidgetRowState.Healthy => "good",
        WidgetRowState.Warning => "warning",
        WidgetRowState.Critical or WidgetRowState.Offline => "attention",
        _ => "default"
    };

    private static string Format(string format, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);

    // Minute granularity (Prism RC-5): the card only repaints on a commit, a debounce or the backstop,
    // so a seconds counter would freeze and lie. Stale switches to "Last reading …".
    private static string FooterText(
        DateTimeOffset generatedAt, DateTimeOffset nowUtc, WidgetFreshnessState freshness, WidgetStrings strings)
    {
        var age = nowUtc - generatedAt;
        var stale = freshness == WidgetFreshnessState.Stale;
        if (age < TimeSpan.FromMinutes(1) && !stale)
        {
            return strings.UpdatedJustNow;
        }

        if (age < TimeSpan.FromHours(1))
        {
            // A stale snapshot is at least 90 s old, so "0 min" cannot occur; clamp anyway.
            var minutes = Math.Max(1, (int)age.TotalMinutes);
            return Format(stale ? strings.LastReadingMinutesAgo : strings.UpdatedMinutesAgo, minutes);
        }

        return Format(stale ? strings.LastReadingHoursAgo : strings.UpdatedHoursAgo, (int)age.TotalHours);
    }

    // Unknown and not-fresh readings are their own bucket — never folded into healthy (§21, D-UI9-5).
    private static string CountsSummaryV3(V3Counts counts, WidgetStrings strings)
    {
        var parts = new List<string>(5);
        if (counts.HealthyFresh > 0)
        {
            parts.Add(WidgetStrings.Plural(counts.HealthyFresh, strings.CountHealthyOne, strings.CountHealthyOther));
        }

        if (counts.Warning > 0)
        {
            parts.Add(Format(strings.CountWarning, counts.Warning));
        }

        if (counts.Critical > 0)
        {
            parts.Add(WidgetStrings.Plural(counts.Critical, strings.CountCriticalOne, strings.CountCriticalOther));
        }

        if (counts.Offline > 0)
        {
            parts.Add(Format(strings.CountOffline, counts.Offline));
        }

        if (counts.NoCurrentData > 0)
        {
            parts.Add(Format(strings.CountUnknown, counts.NoCurrentData));
        }

        return string.Join(strings.CountSeparator, parts);
    }

    private static V3Counts CountV3(IReadOnlyList<WidgetServerState> servers, Func<WidgetServerState, bool> isFresh)
    {
        var counts = new V3Counts();
        foreach (var server in servers)
        {
            switch (RowState(server, isFresh(server)))
            {
                case WidgetRowState.Healthy: counts.HealthyFresh++; break;
                case WidgetRowState.Warning: counts.Warning++; break;
                case WidgetRowState.Critical: counts.Critical++; break;
                case WidgetRowState.Offline: counts.Offline++; break;
                default: counts.NoCurrentData++; break;
            }
        }

        return counts;
    }

    private sealed class V3Counts
    {
        public int HealthyFresh;
        public int Warning;
        public int Critical;
        public int Offline;
        public int NoCurrentData;

        public int Problems => Warning + Critical + Offline;
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

    private static WidgetServerRow ToRow(WidgetServerState server, WidgetStrings strings, bool fresh, bool snapshotStale)
    {
        var state = RowState(server, fresh);
        var statusText = RowStatusText(state, server, strings);
        var statusColor = RowStatusColor(state);

        var cpu = FormatPercent(server.CpuUsagePercent, strings);
        var mem = FormatPercent(server.MemoryUsagePercent, strings);
        var disk = FormatPercent(server.DiskUsagePercent, strings);

        // Localized metric line (§16): "CPU 12% · Memória 34% · Disco 56%".
        var metrics = $"{strings.Cpu} {cpu} · {strings.Memory} {mem} · {strings.Disk} {disk}";

        return new WidgetServerRow(
            ServerId: server.Id,
            DisplayName: TruncateName(server.DisplayName, strings),
            Health: server.Health,
            // Legacy renderer label: switches to the V3 status only when the whole snapshot is stale (until C1).
            HealthLabel: snapshotStale ? statusText : strings.HealthLabel(server.Health),
            HealthColor: snapshotStale ? statusColor : HealthColor(server.Health),
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
            DiskDetail = FormatGb(server.DiskUsedGb, server.DiskTotalGb),
            State = state,
            StatusText = statusText,
            StatusColor = statusColor,
            IsStale = !fresh,
            ShowsMetrics = server.Health is not (WidgetHealth.Offline or WidgetHealth.Unknown)
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
        WidgetSizeHint size,
        WidgetStrings strings,
        WidgetFreshnessState freshness,
        string freshnessText,
        string footerText) => new()
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
        NoServersText = strings.NoServers,
        CardState = WidgetCardState.Empty,
        Title = size == WidgetSizeHint.Small ? strings.EmptyTitleSmall : strings.EmptyTitle,
        Body = strings.EmptyBody,
        CtaText = strings.EmptyCta,
        FooterText = footerText
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
        NoDataBody = strings.NoDataBody,
        // Neutral, never "healthy", and NO add-server CTA (SPEC §3); the card itself opens the dashboard.
        CardState = WidgetCardState.Unavailable,
        Title = strings.UnavailableTitle,
        Body = strings.UnavailableBody
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
