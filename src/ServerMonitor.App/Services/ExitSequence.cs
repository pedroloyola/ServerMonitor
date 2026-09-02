using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// The production steps of a true exit, in the reviewed order (M13 S2 §C). It is the only place that
/// knows WHICH collaborators participate; <see cref="AppLifecycleController"/> owns WHEN.
/// <para>
/// The order matters and is not arbitrary. Foreground work is refused first, so nothing new is accepted
/// while the rest runs. The tray icon is removed second — after the exit is committed, never before (it
/// is the only exit affordance in background, §K), and before the drain, so there is no icon answering
/// nothing for the length of a stop (Vigil C3). The window is hidden third, so the app looks closed
/// immediately instead of standing there through the drain. Only then does the host stop.
/// </para>
/// </summary>
public sealed class ExitSequence(
    IUserNotificationService notificationService,
    IServerAlertCoordinator alertCoordinator,
    IRefreshAllCoordinator refreshAllCoordinator,
    TrayService trayService,
    ApplicationWindowController windowController,
    AppShutdownCoordinator shutdownCoordinator,
    ILogger<ExitSequence> logger) : IExitSequence
{
    public void StopAcceptingForegroundWork()
    {
        notificationService.BeginShutdown();
        alertCoordinator.BeginShutdown();
        refreshAllCoordinator.BeginShutdown();
        logger.LogDebug("Foreground work refused for the remainder of the exit.");
    }

    public void RemoveTrayIcon() => trayService.RemoveIconForExit();

    public void HideUserInterface()
    {
        // Hide BEFORE the controller stops accepting commands, otherwise the hide itself is dropped.
        // Tolerates there being no window at all: that is the headless exit (A12).
        windowController.HideToBackground();
        windowController.BeginShutdown();
    }

    public bool DrainHost() => shutdownCoordinator.Shutdown();
}
