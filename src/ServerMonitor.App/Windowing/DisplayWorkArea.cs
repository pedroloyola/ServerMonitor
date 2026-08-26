namespace ServerMonitor.App.Windowing;

/// <summary>
/// The work area of one connected display (excluding the taskbar), in physical pixels within the
/// virtual desktop, plus that monitor's scale factor as a percentage (100 = 100%, 150 = 150%).
/// The scale lets the placement resolver preserve a window's <em>logical</em> size when it is
/// restored onto a monitor whose DPI differs from the one it was saved on.
/// </summary>
public readonly record struct DisplayWorkArea(int X, int Y, int Width, int Height, int DpiScalePercent)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>Area, in pixels², of the overlap between this display and <paramref name="bounds"/>.</summary>
    public long IntersectionArea(WindowBounds bounds)
    {
        var overlapWidth = Math.Min(Right, bounds.Right) - Math.Max(X, bounds.X);
        var overlapHeight = Math.Min(Bottom, bounds.Bottom) - Math.Max(Y, bounds.Y);
        if (overlapWidth <= 0 || overlapHeight <= 0)
        {
            return 0;
        }

        return (long)overlapWidth * overlapHeight;
    }

    public bool ContainsPoint(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}
