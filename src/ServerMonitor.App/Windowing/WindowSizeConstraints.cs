namespace ServerMonitor.App.Windowing;

/// <summary>
/// Size envelope for a window mode: the minimum the window may shrink to, the maximum it may grow
/// to, and the default used when there is no valid saved size. Standard keeps the M8 minimum of
/// 560×640 in physical pixels of the outer window; Compact (UI.8 D-UI8-1 / RC-1) is a CLIENT-area envelope in
/// device-independent pixels, converted for the target monitor by <see cref="ScaledTo"/>.
/// </summary>
public sealed record WindowSizeConstraints(
    int MinWidth,
    int MinHeight,
    int DefaultWidth,
    int DefaultHeight,
    int MaxWidth,
    int MaxHeight,
    bool IsDipBased = false)
{
    /// <summary>
    /// The standard application window. Minimum matches the M8 desktop QA gate (560×640). Physical pixels of the outer
    /// window, unchanged by UI.8 (backlog WINDOW-STANDARD-DIP: scaling it would change Standard at high DPI).
    /// </summary>
    public static readonly WindowSizeConstraints Standard =
        new(MinWidth: 560, MinHeight: 640, DefaultWidth: 780, DefaultHeight: 760, MaxWidth: 20000, MaxHeight: 20000);

    /// <summary>
    /// UI.8 D-UI8-1: the compact window's client area, in DIP - default and maximum 432×704 (the Figma frame), minimum
    /// 320×340. Resizable inside the envelope, never maximizable; past its height the server list scrolls internally.
    /// </summary>
    public static readonly WindowSizeConstraints Compact =
        new(MinWidth: 320, MinHeight: 340, DefaultWidth: 432, DefaultHeight: 704, MaxWidth: 432, MaxHeight: 704, IsDipBased: true);

    public static WindowSizeConstraints For(WindowMode mode) =>
        mode == WindowMode.Compact ? Compact : Standard;

    /// <summary>
    /// The envelope in physical pixels of the OUTER window on a monitor at <paramref name="dpiScalePercent"/>. A DIP-based
    /// (client) envelope is scaled (<c>round(dip × scale / 100)</c>, half away from zero) and grown by the measured
    /// non-client <paramref name="frame"/> at that scale; a pixel envelope (Standard) is returned unchanged. An invalid
    /// scale counts as 100 %. Pure.
    /// </summary>
    public WindowSizeConstraints ScaledTo(int dpiScalePercent, WindowFrame frame = default)
    {
        if (!IsDipBased)
        {
            return this;
        }

        var scale = WindowPlacementResolver.IsValidDpi(dpiScalePercent) ? dpiScalePercent : 100;
        var (frameWidth, frameHeight) = frame.At(scale);
        int Width(int dip) => Px(dip, scale) + frameWidth;
        int Height(int dip) => Px(dip, scale) + frameHeight;
        return new WindowSizeConstraints(
            Width(MinWidth), Height(MinHeight), Width(DefaultWidth), Height(DefaultHeight), Width(MaxWidth), Height(MaxHeight));
    }

    /// <summary>A size held inside this envelope (min wins over max only for a malformed envelope).</summary>
    public (int Width, int Height) Clamp(int width, int height) =>
        (Math.Max(MinWidth, Math.Min(width, MaxWidth)), Math.Max(MinHeight, Math.Min(height, MaxHeight)));

    private static int Px(int dip, int scale) =>
        (int)Math.Round(dip * (double)scale / 100, MidpointRounding.AwayFromZero);
}
