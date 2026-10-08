namespace ServerMonitor.App.Windowing;

/// <summary>
/// The non-client part of the window - outer size minus client size - in physical pixels, measured at
/// <see cref="DpiScalePercent"/>. AppWindow geometry (and every persisted bound) is the OUTER rectangle, while a
/// DIP-based size envelope describes the CLIENT area the XAML content gets (UI.8 RC-1/R-1: the Compact client is
/// 432×704 DIP at 100 %). Measured at runtime, never assumed: borders differ by DPI, theme and resizability.
/// </summary>
public readonly record struct WindowFrame(int Width, int Height, int DpiScalePercent)
{
    /// <summary>No frame (a test default, or a window that cannot be measured yet).</summary>
    public static WindowFrame None { get; } = new(0, 0, 100);

    /// <summary>The same frame at another scale factor (borders scale with the monitor).</summary>
    public (int Width, int Height) At(int dpiScalePercent)
    {
        var measuredAt = WindowPlacementResolver.IsValidDpi(DpiScalePercent) ? DpiScalePercent : 100;
        var target = WindowPlacementResolver.IsValidDpi(dpiScalePercent) ? dpiScalePercent : 100;
        return (
            (int)Math.Round(Math.Max(0, Width) * (double)target / measuredAt, MidpointRounding.AwayFromZero),
            (int)Math.Round(Math.Max(0, Height) * (double)target / measuredAt, MidpointRounding.AwayFromZero));
    }
}
