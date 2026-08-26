namespace ServerMonitor.App.Windowing;

/// <summary>
/// Owns the Standard ⇄ Compact transition for the one application window: presenter capabilities,
/// bounds (with off-screen/DPI recovery), always-on-top and placement persistence. The window's
/// code-behind only reacts to <see cref="ModeChanged"/> to swap the visible presentation; all of
/// the sequencing lives here. Every method must be invoked on the UI thread.
/// </summary>
public interface IWindowModeCoordinator
{
    WindowMode CurrentMode { get; }

    bool CompactAlwaysOnTop { get; }

    /// <summary>True while the coordinator is itself applying bounds, so external
    /// size/position change handlers can ignore the resulting events and avoid feedback loops.</summary>
    bool IsApplyingBounds { get; }

    /// <summary>Raised after the mode has been applied (including the initial application).</summary>
    event EventHandler<WindowMode>? ModeChanged;

    /// <summary>Applies the persisted mode and geometry once the window and its displays are ready.</summary>
    void Initialize();

    void SwitchTo(WindowMode mode);

    void Toggle();

    void SetCompactAlwaysOnTop(bool enabled);

    /// <summary>
    /// Records the window's current bounds into the in-memory preference for the active mode
    /// without touching disk. Cheap enough to call on every move/resize event; the debounced
    /// disk write is <see cref="PersistCurrentBounds"/>. No-op while minimized/detached.
    /// </summary>
    void CaptureCurrentBounds();

    /// <summary>Captures (best-effort) then writes the current mode's bounds to disk
    /// (on the debounced move, on minimize and on close).</summary>
    void PersistCurrentBounds();
}
