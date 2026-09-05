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
    Action<TrayGuardedOperation> perform,
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

        // NOTHING IS HANDED OVER AND NOTHING COMES BACK — not a boolean, and no longer a delegate
        // either. This method used to pass an Action, which is a place for the caller's own code to run
        // inside the authorisation and record that it held; the recorded fact then outlives it. The
        // operation is named as a VALUE and performed by its owner.
        //
        // This coordinator could not act on the answer even if it had one: it no longer holds the window
        // controller, so hiding the window is not reachable from here at all. Every earlier correction
        // took away the ticket and left the door.
        //
        // And it does not need the answer: BOTH outcomes below cancel the close. Only the exiting branch
        // above returns false, and that is decided without the affordance.
        if (!backgroundSettings.BackgroundMonitoringEnabled)
        {
            // Nothing to do with the affordance: the user turned background monitoring off.
            logger.LogInformation("Window closed with background monitoring disabled; exiting.");
            lifecycleController.RequestExit(ExitReason.UserClosedWindow);
            return true;
        }

        perform(TrayGuardedOperation.EnterBackground);
        return true;
    }
}
