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
