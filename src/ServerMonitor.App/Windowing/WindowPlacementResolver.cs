namespace ServerMonitor.App.Windowing;

/// <summary>
/// Turns an untrusted, possibly stale saved placement into a safe on-screen rectangle for the
/// current display topology. This is the single home for every robustness rule the milestone
/// requires — missing monitors, fully off-screen bounds, negative coordinates, DPI changes and
/// absurd/corrupt sizes — and it is deliberately WinUI-free so all of them can be unit-tested.
/// </summary>
public static class WindowPlacementResolver
{
    // Guards against corrupt persisted input: no real monitor layout produces coordinates or
    // dimensions beyond these, so anything outside is treated as malformed and discarded.
    internal const int MaxDimension = 20000;
    internal const int MaxCoordinate = 200000;
    private const int MinDpiScalePercent = 50;
    private const int MaxDpiScalePercent = 400;

    private static readonly DisplayWorkArea FallbackDisplay = new(0, 0, 1024, 768, 100);

    /// <summary>
    /// Resolves the rectangle to place the window at. When <paramref name="saved"/> is null,
    /// malformed, or lands entirely off every current display, the window is centered on the
    /// primary display at its default size. Otherwise the saved rectangle is rescaled for the
    /// target monitor's DPI, clamped to the mode's size envelope, and nudged fully on-screen.
    /// </summary>
    public static WindowBounds Resolve(
        WindowBounds? saved,
        int savedDpiScalePercent,
        IReadOnlyList<DisplayWorkArea> displays,
        WindowSizeConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(constraints);

        var primary = displays.Count > 0 ? displays[0] : FallbackDisplay;

        if (saved is not { } bounds || !IsSane(bounds))
        {
            return CenterDefault(primary, constraints);
        }

        // Pick the display the saved rectangle overlaps most. No overlap means the window would be
        // fully off-screen (e.g. its monitor was unplugged) — recover to the primary display.
        var target = SelectTargetDisplay(bounds, displays);
        if (target is not { } display)
        {
            return CenterDefault(primary, constraints);
        }

        var width = bounds.Width;
        var height = bounds.Height;

        // Preserve logical size across a DPI change between the saved monitor and the target one.
        if (IsValidDpi(savedDpiScalePercent)
            && IsValidDpi(display.DpiScalePercent)
            && savedDpiScalePercent != display.DpiScalePercent)
        {
            width = (int)Math.Round(width * (double)display.DpiScalePercent / savedDpiScalePercent);
            height = (int)Math.Round(height * (double)display.DpiScalePercent / savedDpiScalePercent);
        }

        width = ClampSize(width, constraints.MinWidth, constraints.MaxWidth, display.Width);
        height = ClampSize(height, constraints.MinHeight, constraints.MaxHeight, display.Height);

        var x = ClampPosition(bounds.X, display.X, display.Right - width);
        var y = ClampPosition(bounds.Y, display.Y, display.Bottom - height);

        return new WindowBounds(x, y, width, height);
    }

    /// <summary>Default rectangle centered on the primary display, used when nothing valid is saved.</summary>
    public static WindowBounds CenterDefault(DisplayWorkArea display, WindowSizeConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        var width = ClampSize(constraints.DefaultWidth, constraints.MinWidth, constraints.MaxWidth, display.Width);
        var height = ClampSize(constraints.DefaultHeight, constraints.MinHeight, constraints.MaxHeight, display.Height);
        var x = display.X + Math.Max(0, (display.Width - width) / 2);
        var y = display.Y + Math.Max(0, (display.Height - height) / 2);
        return new WindowBounds(x, y, width, height);
    }

    /// <summary>Whether a persisted rectangle is within sane bounds; corrupt/absurd values fail closed.</summary>
    public static bool IsSane(WindowBounds bounds) =>
        bounds.Width > 0
        && bounds.Height > 0
        && bounds.Width <= MaxDimension
        && bounds.Height <= MaxDimension
        && Math.Abs(bounds.X) <= MaxCoordinate
        && Math.Abs(bounds.Y) <= MaxCoordinate;

    public static bool IsValidDpi(int dpiScalePercent) =>
        dpiScalePercent is >= MinDpiScalePercent and <= MaxDpiScalePercent;

    private static DisplayWorkArea? SelectTargetDisplay(
        WindowBounds bounds,
        IReadOnlyList<DisplayWorkArea> displays)
    {
        DisplayWorkArea? best = null;
        long bestArea = 0;
        foreach (var display in displays)
        {
            var area = display.IntersectionArea(bounds);
            if (area > bestArea)
            {
                bestArea = area;
                best = display;
            }
        }

        return best;
    }

    private static int ClampSize(int value, int min, int max, int workAreaExtent)
    {
        // Never larger than the target display; never below the mode minimum. The display cap wins
        // over the minimum only when the monitor is genuinely smaller than the minimum window size.
        var upper = Math.Min(max, workAreaExtent);
        if (upper < min)
        {
            return upper;
        }

        return Math.Clamp(value, min, upper);
    }

    private static int ClampPosition(int value, int min, int max)
    {
        // When the window is wider/taller than the work area, max < min; pin to the top-left origin.
        if (max < min)
        {
            return min;
        }

        return Math.Clamp(value, min, max);
    }
}
