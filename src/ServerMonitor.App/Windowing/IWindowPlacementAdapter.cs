namespace ServerMonitor.App.Windowing;

/// <summary>
/// The single boundary between the mode coordinator and the native window
/// (AppWindow / OverlappedPresenter / DisplayArea / Win32). Keeping every platform call behind this
/// fakeable interface lets the coordinator's transition logic be unit-tested without a live window,
/// and keeps P/Invoke out of the ViewModels. All members must be called on the UI thread.
/// </summary>
public interface IWindowPlacementAdapter
{
    bool IsAttached { get; }

    /// <summary>
    /// Current physical bounds and monitor DPI, or null when they cannot be trusted (no window yet,
    /// or the window is minimized — a minimized window reports meaningless geometry).
    /// </summary>
    WindowPlacement? GetPlacement();

    /// <summary>Work areas and scale factors of every connected display; the primary is first.</summary>
    IReadOnlyList<DisplayWorkArea> GetDisplays();

    /// <summary>Moves and resizes the window to the given physical rectangle in one operation.</summary>
    void ApplyBounds(WindowBounds bounds);

    /// <summary>Applies the presenter capabilities for a mode (resizable/maximizable + min size).</summary>
    void ConfigurePresenter(WindowMode mode, WindowSizeConstraints constraints);

    /// <summary>Pins or unpins the window above others using the presenter's supported flag (no polling).</summary>
    void SetAlwaysOnTop(bool enabled);
}
