namespace ServerMonitor.App.Windowing;

/// <summary>
/// A window rectangle in physical (device) pixels within the virtual desktop. Coordinates may be
/// negative because monitors can sit to the left of or above the primary display. This type is
/// intentionally free of any WinUI dependency so the placement logic can be unit-tested.
/// </summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public int CenterX => X + (Width / 2);

    public int CenterY => Y + (Height / 2);
}
