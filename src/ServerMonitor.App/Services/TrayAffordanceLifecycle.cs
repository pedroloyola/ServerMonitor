using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// What the S2-T affordance states MEAN for the lifecycle (M13 S2-T split: S2 owns the semantics).
/// <para>
/// One request is made of it — <see cref="EnterBackground"/> — and one decision is made by it:
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
public sealed class TrayAffordanceLifecycle : ITrayLossConsumer
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

        // TWO CHANNELS, AND THE DIFFERENCE IS THE POINT. The observations arrive on the event; the
        // LOSS arrives directly, because acting on it is what degrades the session or ends the process
        // and it must be distinguishable from a bystander that happened to throw.
        _source.StateChanged += OnAffordanceStateChanged;
        _source.SetLossConsumer(this);
    }

    /// <summary>
    /// Enters background if — and only while — this session may: the affordance is positively established
    /// AND the session has not degraded.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here hands back a permission — not a property, and not a return value.</b> The property
    /// went first, and a <c>bool</c> return took its place: called with an empty action it produced a bare
    /// "you are permitted" that the caller could keep and act on later, which is the same capability in a
    /// new shape. A caller that needs to know what happened finds out from inside its own action, where
    /// what it learns is that the act was DONE, not that it MAY be done.
    /// </remarks>
    public void EnterBackground(Action enterBackground)
    {
        ArgumentNullException.ThrowIfNull(enterBackground);

        lock (_sync)
        {
            if (_degradedForSession)
            {
                return;
            }

            // The session gate is ours; the affordance gate is the source's, and it runs the act under
            // its own lock so the two cannot come apart.
            _source.EnterBackground(enterBackground);
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

    /// <summary>
    /// The authoritative consumption of a loss. Explicit implementation, so it is not on this class's
    /// public surface: only the holder of <see cref="ITrayLossConsumer"/> — the state machine — can invoke
    /// it, and no other caller can force a degradation by calling it directly.
    /// </summary>
    void ITrayLossConsumer.AcknowledgeLoss(TrayAffordanceState state) => Degrade(state);

    /// <summary>
    /// OBSERVATIONS ONLY. A loss arriving here is ignored on purpose: it is consumed authoritatively
    /// through <see cref="ITrayLossConsumer"/>, and handling it in both places would degrade twice and
    /// put the critical consumer back among the observers.
    /// </summary>
    private void OnAffordanceStateChanged(object? sender, EventArgs args)
    {
        var state = _source.State;
        if (state is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)
        {
            return;
        }

        Apply(state);
    }

    private void Apply(TrayAffordanceState state)
    {
        if (state == TrayAffordanceState.Recovering)
        {
            // HOLD. The previous proof is already invalid, so background is not legitimate; but an
            // unauthenticated TaskbarCreated broadcast must not be able to degrade the session either.
            // Only Lost degrades, and Lost is bounded by the recovery deadline (M13 S2-T).
            _logger.LogDebug("The tray affordance is revalidating; holding without degrading.");
            return;
        }

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
