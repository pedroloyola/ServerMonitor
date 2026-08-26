namespace ServerMonitor.App.Windowing;

/// <summary>
/// Size envelope for a window mode: the minimum the window may shrink to, the maximum it may grow
/// to, and the default used when there is no valid saved size. Standard keeps the M8 minimum of
/// 560×640; Compact is a narrow, height-bounded utility strip that scrolls internally past its max.
/// </summary>
public sealed record WindowSizeConstraints(
    int MinWidth,
    int MinHeight,
    int DefaultWidth,
    int DefaultHeight,
    int MaxWidth,
    int MaxHeight)
{
    /// <summary>The standard application window. Minimum matches the M8 desktop QA gate (560×640).</summary>
    public static readonly WindowSizeConstraints Standard =
        new(MinWidth: 560, MinHeight: 640, DefaultWidth: 780, DefaultHeight: 760, MaxWidth: 20000, MaxHeight: 20000);

    /// <summary>
    /// The compact widget. Width is deliberately narrow and tightly bounded (320–400) so the
    /// layout stays predictable; height is dynamic between a small minimum and a bounded maximum,
    /// past which the server list scrolls internally instead of growing the window off-screen.
    /// </summary>
    public static readonly WindowSizeConstraints Compact =
        new(MinWidth: 320, MinHeight: 168, DefaultWidth: 348, DefaultHeight: 420, MaxWidth: 400, MaxHeight: 560);

    public static WindowSizeConstraints For(WindowMode mode) =>
        mode == WindowMode.Compact ? Compact : Standard;
}
