namespace ServerMonitor.App.Windowing;

/// <summary>UI.11 F16: the end of a switch announced by ModeChanging (Succeeded = every window step completed).</summary>
public readonly record struct WindowModeChangeEnded(WindowMode Target, bool Succeeded);

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

    /// <summary>
    /// UI.11 F16 (Cortex D-B3 C-1): raised synchronously by a SWITCH to Compact, before the first window step (the Restore
    /// of a maximized Standard, then presenter and bounds), so the Standard content can be hidden before the resize shows
    /// it clipped. Never from Initialize, a same-mode switch or HoldCompactRestored; never for Compact->Standard.
    /// Subscribers must not touch the window (Opacity only) and must not defer anything.
    /// </summary>
    event EventHandler<WindowMode>? ModeChanging;

    /// <summary>
    /// UI.11 F16 (Cortex D-B3 C-2): raised from the apply's finally after every <see cref="ModeChanging"/>, also when a
    /// window step threw (Succeeded = false; then no ModeChanged follows, as before), so nothing hidden stays hidden.
    /// </summary>
    event EventHandler<WindowModeChangeEnded>? ModeChangeEnded;

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

    /// <summary>
    /// UI.8 RC-2: the ACTIVE mode's size envelope in physical pixels of the outer window, at the window's current DPI
    /// and measured frame - what the window enforces on every user resize. Standard is its pixel envelope (no maximum
    /// in practice); leaving Compact therefore never leaves the Compact limits behind.
    /// </summary>
    WindowSizeConstraints CurrentSizeLimits();

    /// <summary>
    /// UI.8 c2 B-2: Compact is never maximized. Called when the presenter state changes; a maximized Compact (caption
    /// button, title double-click, Win+Up - the shell can bypass <c>IsMaximizable=false</c>) is put back to Restored and
    /// true is returned, so the caller records nothing for that change. False otherwise (Standard may maximize).
    /// </summary>
    bool HoldCompactRestored();
}
