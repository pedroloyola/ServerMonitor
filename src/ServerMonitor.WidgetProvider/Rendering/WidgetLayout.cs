using ServerMonitor.WidgetProvider.Hosting;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>
/// The layout knobs that the real Widgets board (C0-online) decides, in ONE reviewed place (Cortex N-2):
/// rows per size, meter segments and glyphs, and the meter fill colour. Everything else in the card is
/// either copy (<see cref="WidgetStrings"/>) or structure (<see cref="WidgetCardRenderer"/>).
/// </summary>
public static class WidgetLayout
{
    /// <summary>
    /// Max servers rendered per size. Small shows a summary only.
    /// <para>
    /// These are HOST-CAPACITY limits that must be MEASURED on the real board, not chosen: the host gives
    /// each size a fixed card height and silently clips whatever does not fit (M13-QA-4/QA-5, P-017).
    /// C0-online (Prism C0 debrief §2) measured 4 complete V3-geometry rows on Medium without heading/footer.
    /// So Medium = 3 (Figma) with heading + footer, and Large = 3.
    /// </para>
    /// <para>
    /// The V3 board check (V-2) uses the footer "N of M servers" as the control signal: if it is not fully
    /// visible, Medium goes back to 2 HERE. That changes no template byte; "N of M" and the
    /// truthful-degradation tests follow.
    /// </para>
    /// </summary>
    public static int MaxRowsFor(WidgetSizeHint size) => size switch
    {
        WidgetSizeHint.Large => 3,
        WidgetSizeHint.Medium => 3,
        _ => 0
    };

    /// <summary>How a metric is drawn under its "CPU 41%" text.</summary>
    public enum MeterStyle
    {
        /// <summary>Continuous data-URI colour bars (Prism C0 debrief §1, FINAL).</summary>
        Bars,

        /// <summary>The ▰/▱ foreground glyph meter: the FALLBACK if the board shows the bars invisible or cut.</summary>
        Glyphs
    }

    /// <summary>The seam: switching to <see cref="MeterStyle.Glyphs"/> here is the whole fallback.</summary>
    public const MeterStyle Meter = MeterStyle.Bars;

    /// <summary>Bar height per size (Figma 112:10278 Medium 4 px / 112:10387 Large 6 px).</summary>
    public static string BarHeight(bool large) => large ? "6px" : "4px";

    /// <summary>
    /// The fill column WEIGHT of a bar for a DISPLAYED (rounded) percentage (Prism C0 debrief §1 `w` rule):
    /// 0 → no fill (track only), 100 → fill only, 1–2 → 3 (a visible sliver), 98–99 → 97 (a visible
    /// track), else the percentage itself. Full only at 100 %, never empty above 0 %. The track weight is
    /// <c>100 − w</c>.
    /// </summary>
    public static int BarFillWeight(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        return p switch
        {
            0 or 100 => p,
            <= 2 => 3,
            >= 98 => 97,
            _ => p
        };
    }

    /// <summary>Glyph meter segments. Pitch MEASURED on the board for the legacy ▮ (~13 px at Default);
    /// a V3 metric column is ~76–80 px, so 5 fit. C0 re-measures ▰/▱.</summary>
    public const int MeterSegments = 5;

    /// <summary>Filled and empty meter glyphs — told apart by SHAPE, not colour (R-2 / P-RC-1).</summary>
    public const string MeterFill = "▰";
    public const string MeterTrack = "▱";

    /// <summary>Fresh meter fill colour: the neutral host accent, never a health colour (P-RC-7). A colour
    /// per metric needs an image strategy proven at ≥ 3:1 on the board first (R-2).</summary>
    public const string MeterFillColor = "accent";

    /// <summary>
    /// Filled segments for a DISPLAYED (rounded) percentage — Prism P-C1-5, replacing DV-10:
    /// 0 only at 0 %, full only at 100 %, otherwise <c>clamp(round(pct / 20), 1, 4)</c>. So 5 % never reads
    /// as empty and 92 % never reads as full; the exact % is always printed beside the meter.
    /// </summary>
    public static int FilledSegments(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        if (p == 0)
        {
            return 0;
        }

        if (p == 100)
        {
            return MeterSegments;
        }

        var rounded = (int)Math.Round(p * MeterSegments / 100d, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, MeterSegments - 1);
    }
}
