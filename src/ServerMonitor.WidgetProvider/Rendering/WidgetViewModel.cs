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
}
