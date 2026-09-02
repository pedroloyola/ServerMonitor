using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// What the S2-T affordance states MEAN for the lifecycle (M13 S2-T split: S2 owns the semantics).
/// <para>
/// One question is asked of it — <see cref="CanEnterBackground"/> — and one decision is made by it:
/// when the affordance is not established, the session degrades. Both halves are the S2 side of the
/// contract; neither touches the shell.
/// </para>
/// <para>
/// <b>Degradation is one-way for the session.</b> Once the app has told the user "closing the window now
/// quits", it must keep meaning that until the process restarts. A tray icon that came back later would
/// otherwise silently flip the close button's meaning under someone who had just read the opposite, and
/// the persisted preference — which is never rewritten here — would look like it was being ignored at
/// random.
/// </para>
/// <para>
/// <b>Degrading is not the same as exiting.</b> Monitoring continues; what changes is that the window
/// becomes the affordance, so it is materialized straight onto Settings → Background with the warning
/// already visible. Only when there is no window either does the app exit, because a monitoring process
/// the user cannot stop is the A12 zombie by another name.
/// </para>
/// </summary>
public sealed class TrayAffordanceLifecycle
{
    private readonly ITrayAffordanceSource _source;
    private readonly IApplicationWindowController _windowController;
    private readonly IBackgroundDegradationNotice _degradationNotice;
    private readonly IAppLifecycleController _lifecycleController;
    private readonly ILogger<TrayAffordanceLifecycle> _logger;
    private readonly object _sync = new();

    private bool _degradedForSession;

    public TrayAffordanceLifecycle(
        ITrayAffordanceSource source,
        IApplicationWindowController windowController,
        IBackgroundDegradationNotice degradationNotice,
        IAppLifecycleController lifecycleController,
        ILogger<TrayAffordanceLifecycle> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _windowController = windowController ?? throw new ArgumentNullException(nameof(windowController));
        _degradationNotice = degradationNotice ?? throw new ArgumentNullException(nameof(degradationNotice));
        _lifecycleController = lifecycleController ?? throw new ArgumentNullException(nameof(lifecycleController));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _source.StateChanged += OnAffordanceStateChanged;
    }

    /// <summary>
    /// True only while the affordance is positively established AND this session has not degraded. It is
    /// the single precondition for hiding the window: everything else — a returned Start, a flag, an
    /// object, a registry entry — is inference, and inference is what this contract removes.
    /// </summary>
    public bool CanEnterBackground
    {
        get
        {
            lock (_sync)
            {
                return !_degradedForSession && _source.State == TrayAffordanceState.Available;
            }
        }
    }

    /// <summary>True once this session has given up on the tray. One-way.</summary>
    public bool IsDegradedForSession
    {
        get { lock (_sync) { return _degradedForSession; } }
    }

    /// <summary>
    /// Evaluates the current state once at startup, so a process that begins with no affordance — the
    /// headless launch whose registration never succeeded — degrades immediately instead of sitting
    /// invisible with no way out.
    /// </summary>
    public void Evaluate() => Apply(_source.State);

    private void OnAffordanceStateChanged(object? sender, EventArgs args) => Apply(_source.State);

    private void Apply(TrayAffordanceState state)
    {
        if (state == TrayAffordanceState.Available)
        {
            lock (_sync)
            {
                if (_degradedForSession)
                {
                    // Deliberately NOT recovering: see the class remarks. The next launch starts clean.
                    _logger.LogInformation(
                        "The tray affordance is available again, but this session stays degraded.");
                    return;
                }
            }

            _logger.LogInformation("The tray affordance is established; background is available.");
            return;
        }

        Degrade(state);
    }

    private void Degrade(TrayAffordanceState state)
    {
        lock (_sync)
        {
            if (_degradedForSession)
            {
                return;
            }

            _degradedForSession = true;
        }

        if (_lifecycleController.IsExiting)
        {
            // EXIT WINS: never materialize UI while the process is on its way out.
            return;
        }

        _logger.LogWarning(
            "The tray affordance is {State}; this session continues in the foreground with true-exit semantics.",
            state);

        // Order matters and is asserted by the tests: the warning is raised BEFORE the window appears, so
        // the InfoBar is present in the first visible frame, and the window opens DIRECTLY on
        // Settings → Background rather than showing the Dashboard on the way.
        _degradationNotice.Raise();
        _windowController.OpenBackgroundSettings();

        if (!_windowController.IsMaterialized)
        {
            _logger.LogError(
                "No tray affordance and no window: exiting rather than monitoring with no way to stop.");
            _lifecycleController.RequestExit(ExitReason.NoExitAffordance);
        }
    }
}
