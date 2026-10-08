using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>UI.9 V3 card state (SPEC §3 + DV-3). Exactly one per card; never inferred from colour.</summary>
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
/// One metric of a row. <see cref="Percent"/> is the rounded integer percentage, or <c>null</c> when the
/// value is unknown OR hidden (Offline/Unknown rows) — then <see cref="ValueText"/> is "—" and no meter is
/// drawn, never a 0 % track (§19, Manual 107:707).
/// </summary>
public sealed record WidgetMetric(string Label, string ValueText, int? Percent, string Detail);

/// <summary>
/// A string that came from the user or a server (today: only the display name), NOT from
/// <see cref="WidgetStrings"/>. It is a distinct type so it cannot be passed where localized copy is
/// expected, and the renderer has exactly one way to emit it: as a <c>TextRun</c> inside a
/// <c>RichTextBlock</c>, which the host never parses as markdown (R-1 / M-3, Cortex §7.1). There is no
/// implicit conversion to <see cref="string"/>.
/// </summary>
public readonly record struct UntrustedText(string Value)
{
    /// <summary>
    /// Deliberately NOT the value (Vigil C1 L-1 / Cortex N-1): interpolation or concatenation must never
    /// turn the name back into a plain string unnoticed. The one reader is the renderer's <c>name</c> data
    /// key, via <see cref="Value"/>, which a grep can audit.
    /// </summary>
    public override string ToString() => "[untrusted]";

    /// <summary>
    /// The ONLY form in which an untrusted string may be emitted onto a card (Vigil C0 debrief §1, M-3).
    /// The board proved that the TextRun keeps markdown literal, but its date/time pre-processor still
    /// rewrites <c>{{DATE(…)}}</c> / <c>{{TIME(…)}}</c>. So a ZERO WIDTH SPACE (U+200B) is inserted between
    /// every two consecutive <c>{</c>, and the emitted text never contains <c>{{</c>.
    /// <list type="bullet">
    /// <item>It applies LAST, after sanitisation and truncation. The value here is already truncated, so a
    /// surrogate pair is never split by it.</item>
    /// <item>It is invisible on the card. A name without <c>{{</c> comes back byte-identical, with no
    /// gratuitous U+200B.</item>
    /// <item>It lives in the type, so any future emitter inherits it.</item>
    /// </list>
    /// </summary>
    public string ForCard()
    {
        if (!Value.Contains("{{", StringComparison.Ordinal))
        {
            return Value;
        }

        var builder = new System.Text.StringBuilder(Value.Length + 8);
        for (var i = 0; i < Value.Length; i++)
        {
            if (i > 0 && Value[i] == '{' && Value[i - 1] == '{')
            {
                builder.Append('\u200B');
            }

            builder.Append(Value[i]);
        }

        return builder.ToString();
    }
}

/// <summary>
/// One server's render-ready row (Medium/Large). <see cref="ServerId"/> is used ONLY as the deep-link target
/// in the row's action data (§13) — never as visible text. <see cref="DisplayName"/> is the one
/// user-derived string on the card; the renderer routes it through a TextRun only (R-1).
/// </summary>
public sealed record WidgetServerRow
{
    public required Guid ServerId { get; init; }
    public required UntrustedText DisplayName { get; init; }

    /// <summary>The row's V3 state after freshness precedence (D-UI9-5).</summary>
    public required WidgetRowState State { get; init; }

    /// <summary>Localized status text: reason ("High disk"/"Critical disk") or status word. Never empty.</summary>
    public required string StatusText { get; init; }

    /// <summary>Adaptive Card foreground colour for the status — the text always carries the meaning too.</summary>
    public required string StatusColor { get; init; }

    /// <summary>The server's own reading is not fresh (or the snapshot is stale): meters render muted.</summary>
    public required bool IsStale { get; init; }

    /// <summary>False for Offline/Unknown: values render "—" and no meters.</summary>
    public required bool ShowsMetrics { get; init; }

    public required WidgetMetric Cpu { get; init; }
    public required WidgetMetric Memory { get; init; }
    public required WidgetMetric Disk { get; init; }
}

/// <summary>
/// The render-ready projection of the snapshot — the single place ordering, capping, metric formatting,
/// freshness and localized copy are decided, so the renderer only binds strings (§24). Pure data; no JSON,
/// no I/O. Every string here comes from <see cref="WidgetStrings"/> or from formatting a number, except
/// <see cref="WidgetServerRow.DisplayName"/>.
/// </summary>
public sealed record WidgetViewModel
{
    public required WidgetCardState CardState { get; init; }
    public required WidgetSizeHint Size { get; init; }
    public required WidgetFreshnessState Freshness { get; init; }

    /// <summary>Small title / Medium-Large empty or unavailable title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Small subtitle ("3 servers connected" / "Last state: 2 of 3 healthy").</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Medium/Large summary slot: the counts line, or "No recent data" when stale.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Foreground colour of the summary and the Small fraction (state colour; text says it too).</summary>
    public string StateColor { get; init; } = "default";

    /// <summary>Empty/Unavailable body copy.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Empty-state button label (opens the dashboard, D2=(a)); empty for every other state.</summary>
    public string CtaText { get; init; } = string.Empty;

    /// <summary>Minute-granularity footer: "Updated 4 min ago" / "Last reading 12 min ago" (Prism RC-5).</summary>
    public string FooterText { get; init; } = string.Empty;

    /// <summary>"Servers" heading of Medium/Large.</summary>
    public string ServersHeading { get; init; } = string.Empty;

    /// <summary>"N of M servers" for Medium/Large whenever rows are shown — also "3 of 3" (SPEC §3).</summary>
    public string RowsOfTotalText { get; init; } = string.Empty;

    /// <summary>Small ring fallback (P-RC-2): "healthy-and-fresh/total", e.g. "3/3"; when the snapshot is
    /// stale, the LAST state "engine-healthy/total" shown in the default colour (P-C1-4).</summary>
    public string FractionText { get; init; } = string.Empty;

    /// <summary>Ring/fraction alt sentence ("2 of 3 healthy"), for the C0 ring image.</summary>
    public string RingAltText { get; init; } = string.Empty;

    public int TotalServers { get; init; }

    /// <summary>Servers that are Healthy AND fresh — the only ones that count as healthy now.</summary>
    public int HealthyFreshCount { get; init; }

    /// <summary>Servers to render for this size (already ordered + capped). Empty for Small.</summary>
    public IReadOnlyList<WidgetServerRow> Rows { get; init; } = Array.Empty<WidgetServerRow>();

    /// <summary>How many servers were not shown (visible + overflow == total on list sizes).</summary>
    public int OverflowCount { get; init; }
}
