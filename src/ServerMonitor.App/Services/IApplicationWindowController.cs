using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

/// <summary>Controls the one authoritative main window without ever creating another one.</summary>
public interface IApplicationWindowController
{
    bool IsAttached { get; }

    void Attach(Window window);

    // NEITHER HideToBackground() NOR HideForMinimize() IS HERE. It is the one window operation that the tray
    // affordance guards, so leaving it on the contract every consumer holds made the guard advisory: the
    // act stayed reachable whatever the state machine decided. It lives on IWindowHideCapability, which
    // is not registered in the container and has exactly two enumerated holders. See that file.

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
