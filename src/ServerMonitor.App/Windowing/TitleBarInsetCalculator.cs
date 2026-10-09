namespace ServerMonitor.App.Windowing;

/// <summary>
/// Converts the system-reserved title-bar caption inset (reported by AppWindow in physical pixels)
/// into the layout width, in effective/DIP pixels, that a custom title bar must keep clear so its
/// own controls never sit under the native minimize/maximize/close buttons. Keeping this as a pure
/// function — no hardcoded per-machine pixel constants — lets the rule be unit-tested across DPI
/// scales and caption configurations, which is exactly what the compact widget needs.
/// </summary>
public static class TitleBarInsetCalculator
{
    // No real caption region is wider than this; guards against absurd/uninitialized inset values.
    internal const double MaxReserveDips = 400;

    /// <summary>UI.10 H07: the gap between the native caption band and the compact title strip's 40 line.</summary>
    public const double StripGapBelowCaption = 2;

    /// <summary>UI.10 H07: a 36 button centred on the 40 strip line sits 2 below the line's top.</summary>
    public const double ButtonInsetOnStripLine = 2;

    /// <summary>
    /// UI.10 H07: the top of the compact title strip's 40 line, in DIPs: the native caption height converted like the inset
    /// (rounded up, never a 1 px overlap) + <see cref="StripGapBelowCaption"/>. The "Expandir" button then starts at that
    /// + <see cref="ButtonInsetOnStripLine"/>, so its -3 focus ring top = caption height + 1: always below the captions.
    /// Returns 0 when the caption height is not reported (the caller keeps its provisional layout).
    /// </summary>
    public static double ToTitleStripTopDips(int captionHeightPhysicalPixels, double rasterizationScale)
    {
        var caption = ToReservedDips(captionHeightPhysicalPixels, rasterizationScale);
        return caption > 0 ? caption + StripGapBelowCaption : 0;
    }

    /// <summary>
    /// Reserved width in DIPs for the native caption buttons. Returns 0 when nothing is reserved
    /// (e.g. the system title bar is not extended). A non-positive rasterization scale is treated
    /// as 1.0. The result is rounded up so a fractional physical inset never leaves a 1px overlap.
    /// </summary>
    public static double ToReservedDips(int rightInsetPhysicalPixels, double rasterizationScale)
    {
        if (rightInsetPhysicalPixels <= 0)
        {
            return 0;
        }

        var scale = rasterizationScale > 0 ? rasterizationScale : 1.0;
        var dips = Math.Ceiling(rightInsetPhysicalPixels / scale);
        return Math.Min(dips, MaxReserveDips);
    }
}
