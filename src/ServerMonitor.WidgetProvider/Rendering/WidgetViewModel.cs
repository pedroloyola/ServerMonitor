using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>Which top-level state the widget shows.</summary>
public enum WidgetDisplayState
{
    /// <summary>A valid snapshot with at least one server.</summary>
    Available,

    /// <summary>A valid snapshot with zero servers — distinct from unavailable (§14).</summary>
    Empty,

    /// <summary>No usable snapshot (missing/corrupt/oversized/unsupported/IO) — the neutral state (§13).</summary>
    Unavailable
}

/// <summary>UI.9 V3 card state (SPEC §3). Exactly one per card; never inferred from colour.</summary>
public enum WidgetCardState
{
    /// <summary>Every server Healthy AND fresh — the only state that may say "All healthy".</summary>
    Healthy,

    /// <summary>At least one Warning/Critical/Offline (Offline counts as an issue, SPEC §3).</summary>
    Attention,

    /// <summary>No issues, but at least one server without current data (Unknown, or a stale reading).</summary>
    NoCurrentData,

    /// <summary>The whole snapshot is older than its derived threshold (D-UI9-4/5).</summary>
    Stale,

    /// <summary>A valid snapshot with zero visible servers.</summary>
    Empty,

    /// <summary>No usable snapshot (missing/oversized/corrupt/invalid/IO).</summary>
    Unavailable
}

/// <summary>UI.9 V3 row state (SPEC §3, D-UI9-5 precedence: staleness never hides attention).</summary>
public enum WidgetRowState
{
    Healthy,
    Warning,
    Critical,
    Offline,
    Unknown,

    /// <summary>Healthy, or Unknown with an old reading, whose reading is not fresh: "Not updated".</summary>
    NotUpdated
}

/// <summary>
/// One server's render-ready row (Medium/Large). Carries presentation strings + colours, plus the opaque
/// <see cref="ServerId"/> used ONLY as the deep-link target in the row's action data (§13) — never
/// rendered as visible text.
/// </summary>
public sealed record WidgetServerRow(
    Guid ServerId,
    string DisplayName,
    WidgetHealth Health,
    string HealthLabel,
    string HealthColor,
    string CpuText,
    string MemoryText,
    string DiskText,
    string MetricsText)
{
    /// <summary>Meter fill fraction [0,1] for CPU/Memory/Disk; <c>-1</c> = unknown (render neutral/empty,
    /// never a full or 0% bar). Powers the redesigned segmented meters; the %-text stays authoritative (§19).</summary>
    public double CpuFraction { get; init; } = -1;
    public double MemoryFraction { get; init; } = -1;
    public double DiskFraction { get; init; } = -1;

    /// <summary>Large-widget detail lines (empty when unknown): uptime under CPU, "used / total GB" under
    /// Memory and Disk.</summary>
    public string CpuDetail { get; init; } = string.Empty;
    public string MemoryDetail { get; init; } = string.Empty;
    public string DiskDetail { get; init; } = string.Empty;

    // ---- UI.9 V3 (view-model level; the visual rewrite is C1) ----------------------------------------

    /// <summary>The row's V3 state after freshness precedence (D-UI9-5).</summary>
    public WidgetRowState State { get; init; }

    /// <summary>Localized status text: reason ("High disk"/"Critical disk") or status word. Never empty.</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>Adaptive Card colour for the status — the text always carries the meaning too.</summary>
    public string StatusColor { get; init; } = "default";

    /// <summary>The server's own reading is not fresh (or the snapshot is stale): bars render muted.</summary>
    public bool IsStale { get; init; }

    /// <summary>False for Offline/Unknown: values render "—" and no bars (never a 0 % track).</summary>
    public bool ShowsMetrics { get; init; }
}

/// <summary>
/// The render-ready projection of the snapshot — the single place ordering, capping, metric formatting,
/// freshness text, and localized copy are decided, so the Adaptive Card renderers only place strings
/// (§24). Pure data; no JSON, no I/O.
/// </summary>
public sealed record WidgetViewModel
{
    public required WidgetDisplayState DisplayState { get; init; }
    public required WidgetSizeHint Size { get; init; }
    public required string BrandName { get; init; }

    public required WidgetHealth OverallHealth { get; init; }
    public required string OverallHealthLabel { get; init; }
    public required string OverallHealthColor { get; init; }

    /// <summary>Redesign hero (metric-first): big fraction "healthy/total" (e.g. "2/2") + a short label
    /// (e.g. "Saudáveis" when all healthy, else the overall health word). Colored by <see cref="OverallHealthColor"/>.</summary>
    public string HeroValue { get; init; } = string.Empty;
    public string HeroLabel { get; init; } = string.Empty;
    public string FleetKicker { get; init; } = string.Empty;

    // Localized category labels for the Large fleet-summary footer (plural "healthy", plus each severity).
    public string HealthyLabel { get; init; } = string.Empty;
    public string WarningLabel { get; init; } = string.Empty;
    public string CriticalLabel { get; init; } = string.Empty;
    public string OfflineLabel { get; init; } = string.Empty;

    /// <summary>Localized metric labels for the redesigned meters (e.g. "CPU" / "Memória" / "Disco").</summary>
    public string CpuLabel { get; init; } = string.Empty;
    public string MemoryLabel { get; init; } = string.Empty;
    public string DiskLabel { get; init; } = string.Empty;

    /// <summary>Short glanceable summary line (e.g. "All servers healthy").</summary>
    public required string PrimarySummary { get; init; }

    /// <summary>Counts line that never hides Unknown (e.g. "3 healthy · 1 need attention · 1 unknown").</summary>
    public required string CountsSummary { get; init; }

    public required WidgetFreshnessState Freshness { get; init; }

    /// <summary>Relative freshness text ("Updated just now" / "Updated 4 min ago"), or empty when unavailable.</summary>
    public required string FreshnessText { get; init; }

    public int TotalServers { get; init; }
    public int HealthyCount { get; init; }
    public int WarningCount { get; init; }
    public int CriticalCount { get; init; }
    public int OfflineCount { get; init; }
    public int UnknownCount { get; init; }

    /// <summary>Servers to render for this size (already ordered + capped). Empty for Small.</summary>
    public required IReadOnlyList<WidgetServerRow> Rows { get; init; }

    /// <summary>How many visible servers were not shown (the "+N more" affordance).</summary>
    public int OverflowCount { get; init; }

    /// <summary>Localized "+N more" text, or empty when nothing overflowed.</summary>
    public string OverflowText { get; init; } = string.Empty;

    // Empty / unavailable copy.
    public string NoServersText { get; init; } = string.Empty;
    public string NoDataTitle { get; init; } = string.Empty;
    public string NoDataBody { get; init; } = string.Empty;

    // ---- UI.9 V3 (SPEC §3/§4, Prism §B copy) — view-model level; the visual rewrite is C1 ----------

    public WidgetCardState CardState { get; init; }

    /// <summary>Small title / Medium-Large empty or unavailable title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Small subtitle ("3 servers connected" / "Last state: 2 of 3 healthy").</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Medium/Large summary slot: the counts line, or "No recent data" when stale.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Empty/Unavailable body copy.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Empty-state button label (opens the dashboard, D2=(a)); empty for every other state.</summary>
    public string CtaText { get; init; } = string.Empty;

    /// <summary>Minute-granularity footer: "Updated 4 min ago" / "Last reading 12 min ago" (Prism RC-5).</summary>
    public string FooterText { get; init; } = string.Empty;

    /// <summary>"N of M servers" for Medium/Large whenever rows are shown — also "3 of 3" (SPEC §3).</summary>
    public string RowsOfTotalText { get; init; } = string.Empty;

    /// <summary>Servers that are Healthy AND fresh — the only ones that count as healthy now.</summary>
    public int HealthyFreshCount { get; init; }

    /// <summary>Ring/fraction alt text ("2 of 3 healthy").</summary>
    public string RingAltText { get; init; } = string.Empty;
}
