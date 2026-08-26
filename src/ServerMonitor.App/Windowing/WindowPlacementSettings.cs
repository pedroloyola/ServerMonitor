namespace ServerMonitor.App.Windowing;

/// <summary>
/// The persisted window-placement preference: which mode the app was last in, the last good bounds
/// for each mode (with the DPI they were captured at), and whether the compact widget is pinned
/// always-on-top. Bounds are stored per mode so switching modes never overwrites the other mode's
/// remembered geometry. A pre-M9 install has no file and falls back to <see cref="Default"/>.
/// </summary>
public sealed record WindowPlacementSettings
{
    public const int DefaultDpiScalePercent = 100;

    public WindowMode Mode { get; init; } = WindowMode.Standard;

    public WindowBounds? StandardBounds { get; init; }

    public int StandardDpiScalePercent { get; init; } = DefaultDpiScalePercent;

    public WindowBounds? CompactBounds { get; init; }

    public int CompactDpiScalePercent { get; init; } = DefaultDpiScalePercent;

    /// <summary>Always-on-top is a property of the compact experience only; Standard never forces it.</summary>
    public bool CompactAlwaysOnTop { get; init; }

    public static WindowPlacementSettings Default => new();
}
