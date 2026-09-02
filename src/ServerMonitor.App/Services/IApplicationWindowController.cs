using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

/// <summary>Controls the one authoritative main window without ever creating another one.</summary>
public interface IApplicationWindowController
{
    bool IsAttached { get; }

    void Attach(Window window);

    void HideForMinimize();

    /// <summary>
    /// Hides the Dashboard for the BACKGROUND state (M13 S2). Same window operation as
    /// <see cref="HideForMinimize"/>, separate name because the two callers mean different things: one is
    /// the minimize button, the other is the close button under background monitoring, and only the
    /// latter may show the first-close notice. Tolerates there being no window at all (headless).
    /// </summary>
    void HideToBackground();

    /// <summary>
    /// Registers how to create the main window on demand. Headless launches start with no window at all,
    /// so a later legitimate activation has to materialize one before it can show anything.
    /// </summary>
    void AttachWindowFactory(Func<Window> factory);

    /// <summary>True once a window exists (materialized or created at launch).</summary>
    bool IsMaterialized { get; }

    void RestoreAndActivate();

    void OpenSettings();

    /// <summary>
    /// Surfaces the app directly on Settings → Background, materializing the window first if needed.
    /// The navigation happens BEFORE the window is shown, so the Dashboard is never displayed on the way
    /// there — the activation of the background notice must not undo the hide the user just asked for.
    /// </summary>
    void OpenBackgroundSettings();

    /// <summary>Restores/activates the window and toggles between Standard and Compact presentation.</summary>
    void ToggleCompactMode();

    void RequestClose();

    void BeginShutdown();
}
