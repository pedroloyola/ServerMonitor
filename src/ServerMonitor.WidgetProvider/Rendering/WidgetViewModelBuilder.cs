using System.Globalization;
using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>
/// Builds the render-ready <see cref="WidgetViewModel"/> from a snapshot read (§24). Pure. It applies:
/// ordering (§10), per-size capping with an always-stated "N of M" (§8/§9), integer metric formatting
/// with "—" for unknown (§19/§20, never 0-for-unknown), freshness against the provider's own clock and
/// the snapshot's derived threshold (D-UI9-4/5), the V3 card and row states (SPEC §3), and Prism's
/// binding copy (§B). It never recomputes health (§11). Only sanitized display names and normalized
/// metrics reach the model (§15).
/// <para>
/// Precedence: Offline/Critical/Warning keep their label when stale, so staleness never hides attention.
/// Only Healthy, and Unknown with an old reading, become "Not updated". Nothing says "All healthy" and
/// nothing counts a server as healthy unless it is Healthy AND fresh. The raw <c>attentionMetric</c>
/// never reaches the model: it is parsed to the allowlisted enum, only <see cref="WidgetStrings"/> text
/// is emitted, and severity comes from the engine's health (V-RC-6).
/// </para>
/// </summary>
public static class WidgetViewModelBuilder
{
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
        var freshness = WidgetFreshness.Evaluate(read, nowUtc, threshold);
        var stale = freshness == WidgetFreshnessState.Stale;
        var footer = FooterText(snapshot.GeneratedAtUtc, nowUtc, freshness, strings);

        if (snapshot.Servers.Count == 0)
        {
            return Empty(size, strings, freshness, footer);
        }

        bool IsFresh(WidgetServerState server) =>
            WidgetFreshness.IsServerFresh(server, freshness, threshold, nowUtc);

        var counts = Count(snapshot.Servers, IsFresh);
        var ordered = WidgetOrdering.ForDisplay(snapshot.Servers);
        var maxRows = WidgetLayout.MaxRowsFor(size);
        var shown = ordered.Take(maxRows).Select(s => ToRow(s, strings, IsFresh(s))).ToArray();
        var total = snapshot.Servers.Count;

        // TRUTHFUL DEGRADATION INVARIANT (M13-QA-4/QA-5): on a size that renders rows, visible + overflow
        // == total, and "N of M" states it on every list card. Small renders no rows, so its overflow is
        // zero by construction and its fraction states the whole fleet.
        var overflow = maxRows == 0 ? 0 : total - shown.Length;

        var cardState = stale ? WidgetCardState.Stale
            : counts.Problems > 0 ? WidgetCardState.Attention
            : counts.NoCurrentData > 0 ? WidgetCardState.NoCurrentData
            : WidgetCardState.Healthy;

        // "Last state" quotes the ENGINE's healthy count at write time, labelled as the past; every "now"
        // claim (fraction, ring alt, healthy count) uses healthy AND fresh only (P-RC-2, Cortex N-4).
        var lastState = Format(strings.StaleLastStateFormat, counts.EngineHealthy, total);

        return new WidgetViewModel
        {
            CardState = cardState,
            Size = size,
            Freshness = freshness,
            Title = cardState switch
            {
                WidgetCardState.Stale => strings.StaleTitle,
                WidgetCardState.Attention => WidgetStrings.Plural(counts.Problems, strings.FleetProblemsOne, strings.FleetProblemsOther),
                WidgetCardState.NoCurrentData => Format(strings.FleetUnknownOnly, counts.NoCurrentData),
                _ => strings.FleetAllHealthy
            },
            // Cortex N-2: "N servers connected" only vouches for servers with a FRESH reading; omitted at 0.
            Subtitle = stale
                ? lastState
                : counts.ConnectedFresh == 0
                    ? string.Empty
                    : WidgetStrings.Plural(counts.ConnectedFresh, strings.FleetConnectedOne, strings.FleetConnectedOther),
            Summary = stale ? strings.StaleTitle : CountsSummary(counts, strings),
            StateColor = cardState switch
            {
                WidgetCardState.Healthy => "good",
                WidgetCardState.Attention => counts.CriticalOrOffline > 0 ? "attention" : "warning",
                _ => "default"
            },
            FooterText = footer,
            ServersHeading = strings.ServersHeading,
            RowsOfTotalText = maxRows == 0 ? string.Empty : Format(strings.OverflowFormat, shown.Length, total),
            // Prism P-C1-4: a stale card quotes the LAST state (engine healthy/total) in the default colour —
            // "0/3" would read as "none healthy". A fresh card counts healthy AND fresh only (P-RC-2).
            FractionText = string.Create(CultureInfo.InvariantCulture, $"{(stale ? counts.EngineHealthy : counts.HealthyFresh)}/{total}"),
            RingAltText = stale ? lastState : Format(strings.RingAltFormat, counts.HealthyFresh, total),
            TotalServers = total,
            HealthyFreshCount = counts.HealthyFresh,
            Rows = shown,
            OverflowCount = overflow
        };
    }

    /// <summary>
    /// V3 row state (D-UI9-5): Offline/Critical/Warning keep their label whatever the freshness; Healthy
    /// that is not fresh becomes NotUpdated; Unknown becomes NotUpdated only if it HAD a reading (a server
    /// never read is "No data", which is the truer sentence — DV-2).
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

    // Warning = warning colour, Critical/Offline = attention (Prism RC-3/RC-4); never the only signal.
    private static string RowStatusColor(WidgetRowState state) => state switch
    {
        WidgetRowState.Healthy => "good",
        WidgetRowState.Warning => "warning",
        WidgetRowState.Critical or WidgetRowState.Offline => "attention",
        _ => "default"
    };

    private static WidgetServerRow ToRow(WidgetServerState server, WidgetStrings strings, bool fresh)
    {
        var state = RowState(server, fresh);

        // Cortex N-3 (deliberate divergence, DV-8): the App hides retained metrics only for Offline
        // (ServerStatusPresentation.RowHidesRetainedMetrics). The widget also hides them for Unknown,
        // because engine Unknown covers non-transient errors (e.g. auth) where old values would read as
        // current; SPEC §3 Unknown row = "Sem dados", "—".
        var showsMetrics = server.Health is not (WidgetHealth.Offline or WidgetHealth.Unknown);

        return new WidgetServerRow
        {
            ServerId = server.Id,
            DisplayName = new UntrustedText(TruncateName(server.DisplayName, strings)),
            State = state,
            StatusText = RowStatusText(state, server, strings),
            StatusColor = RowStatusColor(state),
            IsStale = !fresh,
            ShowsMetrics = showsMetrics,
            Cpu = Metric(strings.Cpu, server.CpuUsagePercent, showsMetrics,
                FormatUptime(server.UptimeSeconds, strings), strings),
            Memory = Metric(strings.Memory, server.MemoryUsagePercent, showsMetrics,
                FormatGb(server.MemoryUsedGb, server.MemoryTotalGb, strings), strings),
            Disk = Metric(strings.Disk, server.DiskUsagePercent, showsMetrics,
                FormatGb(server.DiskUsedGb, server.DiskTotalGb, strings), strings)
        };
    }

    // null, or a hidden row, stays "—" with no percent (so no meter) — never 0 % (§19).
    private static WidgetMetric Metric(string label, double? value, bool shows, string detail, WidgetStrings strings)
    {
        if (!shows || value is not { } percent)
        {
            return new WidgetMetric(label, strings.MetricUnknown, null, shows ? detail : strings.MetricUnknown);
        }

        var rounded = (int)Math.Round(Math.Clamp(percent, 0d, 100d), MidpointRounding.AwayFromZero);
        return new WidgetMetric(label, rounded.ToString(CultureInfo.InvariantCulture) + "%", rounded, detail);
    }

    // RC-10: "0,6/11,6 GB" in the card's culture, no spaces around the slash; "—" when unknown.
    private static string FormatGb(double? used, double? total, WidgetStrings strings) =>
        used is { } u && total is { } t && t > 0
            ? string.Format(strings.Culture, "{0:0.#}/{1:0.#} GB", u, t)
            : strings.MetricUnknown;

    // RC-10: "Ativo 43d 18h" / "Up 18h 30m" / "Up 45m" — two most-significant units; "—" when unknown.
    private static string FormatUptime(long? seconds, WidgetStrings strings)
    {
        if (seconds is not { } s || s <= 0)
        {
            return strings.MetricUnknown;
        }

        var t = TimeSpan.FromSeconds(s);
        var compact = t.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalDays}d {t.Hours}h")
            : t.TotalHours >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes}m")
                : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}m");
        return Format(strings.UptimeDetailFormat, compact);
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

    // Prism P-C1-2: "N healthy" only when EVERY server is healthy and fresh; otherwise only the counts that
    // need the user — attention, critical, no connection, no recent data — so the colour of the line and its
    // words agree and no problem count is pushed out by healthy ones. Unknown and not-fresh readings are
    // their own bucket, never folded into healthy (§21, D-UI9-5), worded like the title (P-C1-7).
    private static string CountsSummary(Counts counts, WidgetStrings strings)
    {
        if (counts.Problems == 0 && counts.NoCurrentData == 0)
        {
            return WidgetStrings.Plural(counts.HealthyFresh, strings.CountHealthyOne, strings.CountHealthyOther);
        }

        var parts = new List<string>(4);

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
            parts.Add(Format(strings.FleetUnknownOnly, counts.NoCurrentData));
        }

        return string.Join(strings.CountSeparator, parts);
    }

    private static Counts Count(IReadOnlyList<WidgetServerState> servers, Func<WidgetServerState, bool> isFresh)
    {
        var counts = new Counts();
        foreach (var server in servers)
        {
            var fresh = isFresh(server);
            if (server.Health == WidgetHealth.Healthy)
            {
                counts.EngineHealthy++;
            }

            if (fresh && server.Health is WidgetHealth.Healthy or WidgetHealth.Warning or WidgetHealth.Critical)
            {
                counts.ConnectedFresh++;
            }

            switch (RowState(server, fresh))
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

    private sealed class Counts
    {
        public int HealthyFresh;
        public int Warning;
        public int Critical;
        public int Offline;
        public int NoCurrentData;
        public int ConnectedFresh;
        public int EngineHealthy;

        public int CriticalOrOffline => Critical + Offline;
        public int Problems => Warning + Critical + Offline;
    }

    // An empty fleet. Vigil V-B2 (DV-9): if that empty snapshot is itself STALE, the card must not claim
    // "No servers yet" as current truth — it uses the stale wording and asks to open the app to refresh.
    // The CTA (open the dashboard) stays: it promises nothing about adding.
    private static WidgetViewModel Empty(
        WidgetSizeHint size, WidgetStrings strings, WidgetFreshnessState freshness, string footer)
    {
        var stale = freshness == WidgetFreshnessState.Stale;
        return new WidgetViewModel
        {
            CardState = WidgetCardState.Empty,
            Size = size,
            Freshness = freshness,
            Title = stale ? strings.StaleTitle : size == WidgetSizeHint.Small ? strings.EmptyTitleSmall : strings.EmptyTitle,
            Body = stale ? strings.UnavailableBody : strings.EmptyBody,
            CtaText = strings.EmptyCta,
            FooterText = footer
        };
    }

    // Neutral, never "healthy", and NO CTA (SPEC §3); the card itself still opens the dashboard.
    private static WidgetViewModel Unavailable(WidgetSizeHint size, WidgetStrings strings) => new()
    {
        CardState = WidgetCardState.Unavailable,
        Size = size,
        Freshness = WidgetFreshnessState.Unavailable,
        Title = strings.UnavailableTitle,
        Body = strings.UnavailableBody
    };
}
