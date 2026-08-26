namespace ServerMonitor.App.Windowing;

/// <summary>
/// A window's physical bounds together with the scale factor of the monitor it was measured on.
/// The DPI is stored so a later restore onto a different-DPI monitor can preserve logical size
/// rather than blindly reusing raw physical pixels.
/// </summary>
public sealed record WindowPlacement(WindowBounds Bounds, int DpiScalePercent);
