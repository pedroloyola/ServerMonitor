using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// The one decision behind the window's close button (M13 S2 §D), extracted from the window so it can be
/// proved without a XAML runtime.
/// <para>
/// Three outcomes, and no fourth. While exiting, the close is the <c>Exit()</c> closing its own window
/// and is allowed through. With background monitoring on AND a usable way back out, the close is
/// cancelled and the Dashboard is hidden — same process, same tray, engine alive, snapshot still moving,
/// which is what closes M13-QA-8. Otherwise the close is cancelled too, and routed into the one
/// authoritative exit, so the platform never destroys the window on its own initiative and no shutdown
/// semantics can be implied by a window event again.
/// </para>
/// <para>
/// The first-close notice is attempted only here, and only on a real user close: this is the single place
/// in the app that knows a person pressed X or Alt-F4. Minimize, a background launch, protocol
/// activation, restore and headless never pass through it.
/// </para>
/// </summary>
public sealed class WindowCloseCoordinator(
    IAppLifecycleController lifecycleController,
    IBackgroundMonitoringSettingsService backgroundSettings,
    IApplicationWindowController windowController,
    IBackgroundNoticePresenter noticePresenter,
    Func<bool> hasExitAffordance,
    ILogger<WindowCloseCoordinator> logger)
{
    /// <summary>
    /// Handles the platform's close request.
    /// </summary>
    /// <returns>True when the close must be CANCELLED (the window survives), false to let it proceed.</returns>
    public bool HandleCloseRequest()
    {
        if (lifecycleController.IsExiting)
        {
            return false; // this is Application.Exit() closing the window: let it
        }

        if (backgroundSettings.BackgroundMonitoringEnabled && hasExitAffordance())
        {
            windowController.HideToBackground();
            lifecycleController.EnterBackground();
            logger.LogInformation("Window closed to background; monitoring continues.");

            // Never blocks or delays the hide: the hide already happened, and this only reports it.
            noticePresenter.TryShowOnce();
            return true;
        }

        // Either the user turned background monitoring off, or there is no usable way back to the app
        // (no tray icon), in which case staying resident would mean monitoring the user cannot stop.
        logger.LogInformation("Window closed with no background state available; exiting.");
        lifecycleController.RequestExit(ExitReason.UserClosedWindow);
        return true;
    }
}
