using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// The ordered steps of a true exit, behind an interface so the controller stays free of XAML, of the
/// tray and of the host, and so every step can be made to fail or block in a test.
/// </summary>
public interface IExitSequence
{
    /// <summary>Refuse new foreground/lifecycle work: notifications, alerts, refresh-all.</summary>
    void StopAcceptingForegroundWork();

    /// <summary>
    /// Remove the notification-area icon. Runs only AFTER the exit is committed (Vigil C3): removing it
    /// earlier would take away the only exit affordance while the app is still running, and removing it
    /// later would leave up to a drain's worth of icon that answers nothing.
    /// </summary>
    void RemoveTrayIcon();

    /// <summary>Hide the window if there is one, so the app looks closed while the host drains.</summary>
    void HideUserInterface();

    /// <summary>
    /// Stop the monitoring host under its existing bound. Returns true only when the stop actually
    /// completed; a timeout returns false and the caller must NOT then wait on disposal.
    /// </summary>
    bool DrainHost();
}

/// <summary>
/// The single owner of lifecycle state and of the one authoritative exit (M13 S2 §C/§F).
/// <para>
/// <b>Ownership of the AppInstance key.</b> This path deliberately never calls <c>UnregisterKey</c>.
/// Releasing it while still alive would open an interval in which this process is running with no owner,
/// during which a concurrent launch registers the key, becomes primary and starts a SECOND monitoring
/// host writing the same snapshot. <c>UnregisterKey</c> and <c>Exit</c> are separate calls with no
/// atomic combination available in the API, so ordering can only shrink that interval, never close it.
/// Letting process termination release the registration removes the interval by construction, because
/// termination is atomic from the OS's side and shared with nobody. The accepted residual: while this
/// process is dying it remains the redirect target and discards what it receives (EXIT WINS).
/// </para>
/// <para>
/// <b>Why the watchdog is not optional.</b> That mitigation is only sound if the process really does die:
/// a hung shutdown would leave the key owned by a corpse and every later launch would redirect into
/// nothing. So the deadline is armed at the moment the exit is committed and is monotonic and
/// non-restartable, and the terminal escalation is reached whatever happens in between.
/// </para>
/// </summary>
public sealed class AppLifecycleController : IAppLifecycleController
{
    /// <summary>
    /// Global budget from the successful transition to Exiting until the process must be gone. The
    /// host stop keeps its own smaller bound inside this one; 10 s is the reviewed value.
    /// </summary>
    public static readonly TimeSpan DefaultTerminationDeadline = TimeSpan.FromSeconds(10);

    // Resolved lazily, and deliberately so: the exit sequence depends on the tray and the notification
    // service, and both of those depend on this controller. Taking a factory breaks that cycle at
    // construction without a service locator — by the time an exit is requested, everything exists.
    private readonly Func<IExitSequence> _exitSequenceFactory;
    private readonly Action _exitApplication;
    private readonly ITerminationWatchdog _watchdog;
    private readonly IProcessTerminator _terminator;
    private readonly TimeSpan _terminationDeadline;
    private readonly Action<ExitReason>? _onExitCommitted;
    private readonly ILogger<AppLifecycleController> _logger;

    private int _state;
    private int _applicationExitInvoked;

    public AppLifecycleController(
        Func<IExitSequence> exitSequenceFactory,
        Action exitApplication,
        ITerminationWatchdog watchdog,
        IProcessTerminator terminator,
        ILogger<AppLifecycleController> logger,
        LaunchMode launchMode = LaunchMode.Foreground,
        TimeSpan? terminationDeadline = null,
        Action<ExitReason>? onExitCommitted = null)
    {
        _exitSequenceFactory = exitSequenceFactory ?? throw new ArgumentNullException(nameof(exitSequenceFactory));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onExitCommitted = onExitCommitted;
        _terminationDeadline = terminationDeadline ?? DefaultTerminationDeadline;
        if (_terminationDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationDeadline));
        }

        StartedInBackground = launchMode == LaunchMode.Background;
        _state = (int)(StartedInBackground ? AppLifecycleState.Background : AppLifecycleState.Foreground);
    }

    public AppLifecycleState State => (AppLifecycleState)Volatile.Read(ref _state);

    public bool StartedInBackground { get; }

    public bool IsExiting => State == AppLifecycleState.Exiting;

    public void EnterForeground() => TransitionUnlessExiting(AppLifecycleState.Foreground);

    public void EnterBackground() => TransitionUnlessExiting(AppLifecycleState.Background);

    public void RequestExit(ExitReason reason)
    {
        if (!TryTransitionToExiting())
        {
            _logger.LogDebug("Exit already in progress; ignoring the {Reason} request.", reason);
            return;
        }

        _logger.LogInformation("True exit requested ({Reason}).", reason);

        // The ONLY place the exit is known to be ours (Prism, CV-17). TryTransitionToExiting is the CAS
        // that already existed; this observes its result rather than adding a second one, and it is
        // reached exclusively on the winning branch. A call that LOST returned above, so an exit the user
        // asked for — Sair, or X with background off — never produces a notice telling them to open the
        // app again to keep monitoring, which would contradict what they just did.
        //
        // It runs BEFORE StopAcceptingForegroundWork, because that step is what closes the notification
        // service to new work, and AFTER nothing else, so no shutdown step can delay it.
        RunStep(nameof(_onExitCommitted), () => _onExitCommitted?.Invoke(reason));

        // Armed only after the transition succeeded, so it can never fire outside Exiting, and never
        // restarted, so nothing can push the deadline out. It is intentionally NOT disarmed below: a
        // process that fails to die after Exit() is exactly what it is here to end.
        _watchdog.Arm(_terminationDeadline, OnTerminationDeadlineReached);

        try
        {
            // Even building the sequence must not be able to throw at the caller: RequestExit is invoked
            // from a window event handler and from the tray, and an exception there would both surface as
            // a crash and skip the steps. A sequence that cannot be built means an exit with no cleanup,
            // which is still an exit.
            IExitSequence? exitSequence = null;
            RunStep("BuildExitSequence", () => exitSequence = _exitSequenceFactory());
            if (exitSequence is null)
            {
                return;
            }

            RunStep(nameof(IExitSequence.StopAcceptingForegroundWork), exitSequence.StopAcceptingForegroundWork);
            RunStep(nameof(IExitSequence.RemoveTrayIcon), exitSequence.RemoveTrayIcon);
            RunStep(nameof(IExitSequence.HideUserInterface), exitSequence.HideUserInterface);

            var stopped = false;
            RunStep(nameof(IExitSequence.DrainHost), () => stopped = exitSequence.DrainHost());
            if (!stopped)
            {
                _logger.LogWarning("The monitoring host did not stop within its bound; exiting anyway.");
            }
        }
        finally
        {
            // Vigil C1: the terminal step runs even if any step above threw. A half-failed shutdown must
            // never be able to leave the process alive.
            ExitApplicationOnce();
        }
    }

    private void OnTerminationDeadlineReached()
    {
        // Only reachable from the watchdog, and only while Exiting.
        _terminator.Terminate(ProcessTerminator.WatchdogExitCode);
    }

    private void ExitApplicationOnce()
    {
        if (Interlocked.Exchange(ref _applicationExitInvoked, 1) != 0)
        {
            return;
        }

        try
        {
            _exitApplication();
        }
        catch (Exception exception)
        {
            // Nothing left to fall back on but the watchdog, which is already armed.
            _logger.LogError(exception, "Application exit failed; the termination watchdog now owns the exit.");
        }
    }

    private void RunStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The {Step} shutdown step failed; continuing to exit.", name);
        }
    }

    private bool TryTransitionToExiting()
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == (int)AppLifecycleState.Exiting)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, (int)AppLifecycleState.Exiting, current) == current)
            {
                return true;
            }
        }
    }

    private void TransitionUnlessExiting(AppLifecycleState target)
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == (int)AppLifecycleState.Exiting || current == (int)target)
            {
                return; // Exiting is terminal; an identical state needs no write
            }

            if (Interlocked.CompareExchange(ref _state, (int)target, current) == current)
            {
                return;
            }
        }
    }
}
