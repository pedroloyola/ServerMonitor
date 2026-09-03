namespace ServerMonitor.App.Services;

/// <summary>The three lifecycle states (M13 S2 §B.1). There is no fourth: headless is BACKGROUND
/// whose Dashboard has not been materialized yet.</summary>
public enum AppLifecycleState
{
    /// <summary>Dashboard visible, monitoring host running.</summary>
    Foreground,

    /// <summary>Dashboard hidden or never created, tray available, monitoring host running.</summary>
    Background,

    /// <summary>Terminal and one-shot: ordered shutdown in progress, then the process ends.</summary>
    Exiting
}

/// <summary>Why a true exit was requested. Diagnostic only — every reason takes the same path.</summary>
public enum ExitReason
{
    /// <summary>The user closed the window while background monitoring was disabled.</summary>
    UserClosedWindow,

    /// <summary>The user chose "Sair do ServerAlyzer" in the tray menu.</summary>
    TrayExit,

    /// <summary>Startup failed and the partially built process must not linger.</summary>
    StartupFailure,

    /// <summary>
    /// No usable exit affordance exists (the tray icon could not be created and no window can be shown),
    /// so a monitoring process the user cannot stop must not continue (§K).
    /// </summary>
    NoExitAffordance,

    /// <summary>
    /// The notification-area icon could not be positively removed, so the process must not continue —
    /// normally or degraded — while it may still be holding an affordance whose removal cannot be
    /// established (M13 S2-T, CV-16). This is the ONLY reason that raises the fail-safe exit notice.
    /// </summary>
    TrayCleanupUnverified
}

/// <summary>
/// The single owner of the lifecycle state and of <see cref="RequestExit"/> (M13 S2 §C).
/// <para>
/// Everything that used to decide shutdown implicitly — <c>Window.Closed</c> stopping the host, the tray
/// closing the window to get there — now routes here. Exactly one authoritative exit path exists, it runs
/// once, it works with no window at all, and it ends the process within a hard deadline whatever happens
/// during the drain.
/// </para>
/// </summary>
public interface IAppLifecycleController
{
    /// <summary>The current state. Never goes back once <see cref="AppLifecycleState.Exiting"/>.</summary>
    AppLifecycleState State { get; }

    /// <summary>
    /// True when THIS process was started with <c>--background</c>. Used to suppress the first-close
    /// notice, which only makes sense after a user-initiated FOREGROUND → BACKGROUND transition.
    /// </summary>
    bool StartedInBackground { get; }

    /// <summary>True while a true exit is in progress or done.</summary>
    bool IsExiting { get; }

    /// <summary>Records that the Dashboard is now visible/foreground. No-op once exiting.</summary>
    void EnterForeground();

    /// <summary>Records that the Dashboard is hidden or not materialized. No-op once exiting.</summary>
    void EnterBackground();

    /// <summary>
    /// THE authoritative exit. One-shot, idempotent, thread-safe, safe with no window: transition to
    /// <see cref="AppLifecycleState.Exiting"/>, stop accepting foreground work, remove the tray icon,
    /// hide the UI, drain the host under its bound, and terminate — exactly once.
    /// </summary>
    void RequestExit(ExitReason reason);
}
