using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>The events that may reach <see cref="TrayStateMachine.Transition"/>. Closed set.</summary>
internal enum TrayEventKind
{
    Establish,
    TaskbarCreated,
    DebounceElapsed,
    RetryDue,
    AddCompleted,
    DeadlineObserved,
    CleanupCompleted,
    Release
}

/// <summary>An event carrying its generation, so the preamble can judge obsolescence.</summary>
internal readonly record struct TrayEvent(TrayEventKind Kind, long Generation, bool Success);

/// <summary>What the shell may still be holding because of us.</summary>
internal enum ShellEffectState
{
    /// <summary>No Add was ever issued for the current episode.</summary>
    NotIssued = 0,

    /// <summary>An Add was issued. Marked BEFORE the call, deliberately: see the class remarks.</summary>
    MayExist = 1,

    /// <summary>A Delete returned true, so the shell no longer holds our icon.</summary>
    Deleted = 2,

    /// <summary>Delete kept returning false while we knew an effect might exist. Fail-safe territory.</summary>
    Unverified = 3
}

/// <summary>
/// The single lifecycle authority for the tray affordance (M13 S2-T, design
/// <c>docs/m13-s2t-linearizable-state-machine.md</c>).
/// <para>
/// <b>One atomic state, one normative transition function.</b> <see cref="Transition"/> is the only
/// writer of lifecycle state in the program. It is called directly and synchronously by every event
/// source — there is no queue and no dispatch between accepting an event and changing the state, which
/// is what removes the false-<c>Available</c> interval that three earlier structures reintroduced.
/// </para>
/// <para>
/// <b>Effects are passive data.</b> They describe what to do and have no behaviour to redefine. Only
/// the private nested <c>EffectExecutor</c> holds <see cref="INativeTrayRegistration"/>; this class does
/// not keep it. The capability crosses the boundary exactly once, in the constructor parameter, which
/// forwards without retaining.
/// </para>
/// <para>
/// <b>Nothing inside the decision domain performs I/O</b>: no native call, no await, no dispatch, no
/// gate acquisition. Effects run after the lock is released and their results re-enter through the same
/// function, so a blocked shell call can never block a transition.
/// </para>
/// </summary>
internal sealed class TrayStateMachine : ITrayAffordanceSource, IDisposable
{
    /// <summary>Budget A: attempts inside one admitted episode (initial + 2).</summary>
    internal const int MaxAttemptsPerEpisode = 3;

    /// <summary>Bounded compensation attempts before the effect is declared unverified.</summary>
    internal const int MaxCleanupAttempts = 3;

    /// <summary>Bounded fail-safe sink attempts before escalating. A fixed bound is what stops a loop.</summary>
    internal const int MaxFailSafeAttempts = 3;

    internal static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan SecondRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// TRAY RECOVERY GLOBAL MONOTONIC DEADLINE. ~1250 ms of scheduled delays plus ~250 ms of execution
    /// and scheduling slack. <b>The slack is our decision, not a Windows scheduling guarantee.</b> The
    /// normative guarantee is safety: after this deadline nothing may publish Available, and
    /// terminalization happens on the first execution that observes the expiry.
    /// </summary>
    internal static readonly TimeSpan RecoveryDeadline = TimeSpan.FromMilliseconds(1500);

    // ---------------------------------------------------------------------------------------------
    // Effects: private nested, passive data. Not nameable, declarable or constructible outside.
    // ---------------------------------------------------------------------------------------------

    private enum EffectKind
    {
        AddIcon,
        DeleteIcon,
        ScheduleDebounce,
        ScheduleRetry,
        ScheduleDeadline,
        FailSafeExit
    }

    private readonly record struct Effect(EffectKind Kind, long Generation, long Sequence, TimeSpan Delay);

    /// <summary>
    /// The operation and the affordance flag come from ONE expression, in an exhaustive switch with no
    /// default arm: a new kind is a compile error rather than a silent <c>false</c>, and the two values
    /// cannot disagree because they do not live in separate places.
    /// </summary>
    private static (NativeTrayOperation Operation, bool MayCreateAffordance) Describe(EffectKind kind) =>
        kind switch
        {
            EffectKind.AddIcon => (NativeTrayOperation.Add, true),
            EffectKind.DeleteIcon => (NativeTrayOperation.Delete, false),
            EffectKind.ScheduleDebounce => (NativeTrayOperation.None, false),
            EffectKind.ScheduleRetry => (NativeTrayOperation.None, false),
            EffectKind.ScheduleDeadline => (NativeTrayOperation.None, false),
            EffectKind.FailSafeExit => (NativeTrayOperation.None, false)
            // No `_ =>` arm on purpose. CS8509 is escalated to an error in the csproj.
        };

    /// <summary>Test seam only. Exposes the same expression production uses, without widening it.</summary>
    internal static (NativeTrayOperation Operation, bool MayCreateAffordance) DescribeForTests(int kind) =>
        Describe((EffectKind)kind);

    /// <summary>The only type in the program that retains the shell capability.</summary>
    private sealed class EffectExecutor(INativeTrayRegistration native)
    {
        private readonly INativeTrayRegistration _native = native;

        internal bool Run(NativeTrayOperation operation) => operation switch
        {
            NativeTrayOperation.Add => _native.Add() && _native.SetVersion(),
            NativeTrayOperation.Delete => _native.Delete(),
            NativeTrayOperation.None => true,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    // ---------------------------------------------------------------------------------------------

    private readonly EffectExecutor _executor;
    private readonly EpisodeFrequencyLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly Action _requestAuthoritativeExit;
    private readonly Action _escalateTermination;
    private readonly ILogger _logger;

    private readonly object _decision = new();
    private readonly object _nativeGate = new();
    private readonly Queue<Effect> _pending = new();
    private readonly List<ITimer> _timers = [];

    private TrayLifecycleState _state = TrayLifecycleState.Unavailable;
    private long _generation;
    private long _sequence;
    private bool _episodeActive;
    private long _deadlineTimestamp;
    private int _attemptsUsed;
    private int _cleanupAttempts;
    private ShellEffectState _effect = ShellEffectState.NotIssued;
    private int _reconciliationPending;
    private bool _failSafeCompleted;
    private bool _failSafeRequested;
    private bool _disposed;

    public TrayStateMachine(
        INativeTrayRegistration native,
        Action requestAuthoritativeExit,
        Action escalateTermination,
        TimeProvider timeProvider,
        ILogger<TrayStateMachine> logger,
        EpisodeFrequencyLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(native);

        // A missing sink is a CONSTRUCTION error, never a late runtime drop: the fail-safe path is the
        // only progress mechanism a Releasing episode has before the watchdog exists.
        _requestAuthoritativeExit = requestAuthoritativeExit
            ?? throw new ArgumentNullException(nameof(requestAuthoritativeExit));
        _escalateTermination = escalateTermination ?? throw new ArgumentNullException(nameof(escalateTermination));

        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limiter = limiter ?? new EpisodeFrequencyLimiter(_time);

        // The capability is forwarded, never retained by this class.
        _executor = new EffectExecutor(native);
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public TrayAffordanceState State
    {
        get { lock (_decision) { return Project(_state); } }
    }

    /// <summary>The internal state. Diagnostics and tests only.</summary>
    internal TrayLifecycleState LifecycleState
    {
        get { lock (_decision) { return _state; } }
    }

    /// <summary>Whether the shell may still hold an icon of ours. Diagnostics and tests only.</summary>
    internal ShellEffectState EffectState
    {
        get { lock (_decision) { return _effect; } }
    }

    /// <summary>
    /// Whether the single fail-safe shot has been consumed. Exposed so a mutation that marks it on
    /// ENTRY instead of after a normal return is observable: with an always-throwing sink this must
    /// stay false, or an exception has silently eaten the only progress mechanism Releasing has.
    /// </summary>
    internal bool FailSafeCompleted
    {
        get { lock (_decision) { return _failSafeCompleted; } }
    }

    /// <summary>True when a Lost episode could not positively verify its cleanup.</summary>
    internal bool CleanupVerified
    {
        get { lock (_decision) { return _effect is ShellEffectState.Deleted or ShellEffectState.NotIssued; } }
    }

    /// <summary>Starts the initial establishment episode. Same arbiter as broadcast recovery.</summary>
    public void Establish() => Dispatch(new TrayEvent(TrayEventKind.Establish, 0, false));

    /// <summary>A <c>TaskbarCreated</c> broadcast reached our window.</summary>
    public void NotifyTaskbarCreated() => Dispatch(new TrayEvent(TrayEventKind.TaskbarCreated, 0, false));

    /// <summary>The single public terminal operation. Idempotent; a no-op once terminal.</summary>
    public void Release() => Dispatch(new TrayEvent(TrayEventKind.Release, 0, false));

    // ---------------------------------------------------------------------------------------------
    // Dispatch: decide under the lock, execute outside it.
    // ---------------------------------------------------------------------------------------------

    private void Dispatch(TrayEvent trayEvent)
    {
        Outcome outcome;
        lock (_decision)
        {
            outcome = Transition(trayEvent, _time.GetTimestamp());
        }

        Execute(outcome);
    }

    private readonly record struct Outcome(bool FailSafeExit, bool Publish, TrayAffordanceState State);

    /// <summary>
    /// THE transition function. Called with <see cref="_decision"/> held; performs no I/O of any kind.
    /// </summary>
    private Outcome Transition(TrayEvent trayEvent, long monotonicNow)
    {
        var before = _state;

        // --- Preamble step 1: Release is absorbing. One guard, common to every call. ---
        if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)
        {
            return TerminalOnly(trayEvent);
        }

        // --- Preamble step 2: obsolescence, WITH the CV-19 carve-out. ---
        // Effect-conclusion events capable of having created a shell affordance are NEVER discarded by
        // stale generation: they are routed to reconciliation. Discarding them is precisely how an
        // orphaned icon is written.
        if (trayEvent.Generation != 0 && trayEvent.Generation != _generation
            && trayEvent.Kind != TrayEventKind.AddCompleted)
        {
            return Result(before);
        }

        // --- Preamble step 3: the deadline terminalizes here, before the event is looked at. ---
        if (_episodeActive && monotonicNow >= _deadlineTimestamp)
        {
            EnterLost("the recovery deadline expired");

            // The event still has to be reconciled if it may have created an affordance.
            if (trayEvent.Kind == TrayEventKind.AddCompleted)
            {
                ReconcileAddCompletion(trayEvent);
            }

            return Result(before);
        }

        switch (trayEvent.Kind)
        {
            case TrayEventKind.Establish:
                if (_state == TrayLifecycleState.Unavailable && !_episodeActive)
                {
                    BeginEpisode(monotonicNow);
                    Emit(EffectKind.ScheduleDeadline, RecoveryDeadline);
                    Attempt();
                }

                break;

            case TrayEventKind.TaskbarCreated:
                // Admission and invalidation are ONE linearizable operation. The frequency gate is
                // consulted HERE, inside the transition: a suppressed message is exactly equivalent to
                // one that never arrived, which is the right answer for unauthenticated input and makes
                // "exceeding B never emits Lost" true by construction.
                if (_state == TrayLifecycleState.Recovering)
                {
                    // Additional broadcasts JOIN the existing episode. They do not move the clock, do
                    // not create a generation and do not consume budget B — exactly one episode is in
                    // flight, which is also what keeps the CV-8 UI cost inside the approved envelope.
                    break;
                }

                if (_state != TrayLifecycleState.Available)
                {
                    break;
                }

                if (!_limiter.TryBeginEpisode(monotonicNow))
                {
                    _logger.LogDebug("A TaskbarCreated broadcast was suppressed by the admission limiter.");
                    break;
                }

                BeginEpisode(monotonicNow);
                Emit(EffectKind.ScheduleDeadline, RecoveryDeadline);
                Emit(EffectKind.ScheduleDebounce, DebounceDelay);
                break;

            case TrayEventKind.DebounceElapsed:
                if (_state == TrayLifecycleState.Recovering && _attemptsUsed == 0)
                {
                    Attempt();
                }

                break;

            case TrayEventKind.RetryDue:
                if (_state == TrayLifecycleState.Recovering)
                {
                    Attempt();
                }

                break;

            case TrayEventKind.AddCompleted:
                HandleAddCompleted(trayEvent);
                break;

            case TrayEventKind.DeadlineObserved:
                // Step 3 already handled expiry. Nothing else to do.
                break;

            case TrayEventKind.CleanupCompleted:
                HandleCleanupCompleted(trayEvent);
                break;

            case TrayEventKind.Release:
                return EnterReleasing(before);

            default:
                throw new ArgumentOutOfRangeException(nameof(trayEvent), trayEvent.Kind, null);
        }

        return Result(before);
    }

    private Outcome TerminalOnly(TrayEvent trayEvent)
    {
        var before = _state;

        // Terminal does NOT mean "ignore". An AddCompleted that may have created an icon is reconciled,
        // because the result is obsolete for the lifecycle but not for the shell.
        if (trayEvent.Kind == TrayEventKind.AddCompleted)
        {
            ReconcileAddCompletion(trayEvent);
        }
        else if (trayEvent.Kind == TrayEventKind.CleanupCompleted)
        {
            HandleTerminalCleanup(trayEvent);
        }
        else if (trayEvent.Kind == TrayEventKind.Release)
        {
            // Idempotent: a repeated Release, including the one the S2 ExitSequence makes through
            // RemoveTrayIcon, returns immediately so the authoritative exit path can never block on us.
            return Result(before);
        }

        return Result(before);
    }

    private Outcome EnterReleasing(TrayLifecycleState before)
    {
        _state = TrayLifecycleState.Releasing;
        _episodeActive = false;
        _generation++;

        if (_effect == ShellEffectState.MayExist)
        {
            _cleanupAttempts = 0;
            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);
        }
        else
        {
            TryComplete();
        }

        return Result(before);
    }

    private void BeginEpisode(long monotonicNow)
    {
        _generation++;
        _episodeActive = true;
        _deadlineTimestamp = monotonicNow + (long)(RecoveryDeadline.TotalSeconds * _time.TimestampFrequency);
        _attemptsUsed = 0;
        _state = TrayLifecycleState.Recovering;
    }

    private void Attempt()
    {
        _attemptsUsed++;

        // Marked BEFORE the call, deliberately. Marking after would let an interruption convince us
        // nothing exists in the shell when it does; marking before costs at most a redundant Delete.
        _effect = ShellEffectState.MayExist;
        _reconciliationPending++;
        Emit(EffectKind.AddIcon, TimeSpan.Zero);
    }

    private void HandleAddCompleted(TrayEvent trayEvent)
    {
        _reconciliationPending = Math.Max(0, _reconciliationPending - 1);

        if (trayEvent.Generation != _generation || !_episodeActive)
        {
            ReconcileStale(trayEvent);
            return;
        }

        if (trayEvent.Success)
        {
            _effect = ShellEffectState.MayExist;
            _state = TrayLifecycleState.Available;
            _episodeActive = false;

            return;
        }

        if (_attemptsUsed >= MaxAttemptsPerEpisode)
        {
            EnterLost("the retry budget was exhausted with an observed native failure");
            return;
        }

        Emit(EffectKind.ScheduleRetry, _attemptsUsed == 1 ? FirstRetryDelay : SecondRetryDelay);
    }

    private void ReconcileAddCompletion(TrayEvent trayEvent)
    {
        _reconciliationPending = Math.Max(0, _reconciliationPending - 1);
        ReconcileStale(trayEvent);
    }

    private void ReconcileStale(TrayEvent trayEvent)
    {
        // The result is obsolete for the lifecycle. It is NOT obsolete for the shell: if it may have
        // recreated the icon, a compensating Delete is mandatory.
        if (trayEvent.Success)
        {
            _effect = ShellEffectState.MayExist;
            _cleanupAttempts = 0;
            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);
        }
        else
        {
            TryComplete();
        }
    }

    private void HandleCleanupCompleted(TrayEvent trayEvent)
    {
        if (trayEvent.Success)
        {
            _effect = ShellEffectState.Deleted;
            return;
        }

        if (++_cleanupAttempts < MaxCleanupAttempts)
        {
            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);
            return;
        }

        _effect = ShellEffectState.Unverified;

        // CleanupVerified=false is never a steady state: the process may not continue, normally or
        // degraded, while it may be holding an affordance whose removal cannot be established.
        _state = TrayLifecycleState.Releasing;
        _episodeActive = false;
        _generation++;
        _failSafeRequested = true;
    }

    private void HandleTerminalCleanup(TrayEvent trayEvent)
    {
        if (trayEvent.Success)
        {
            _effect = ShellEffectState.Deleted;
            TryComplete();
            return;
        }

        if (++_cleanupAttempts < MaxCleanupAttempts)
        {
            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);
            return;
        }

        _effect = ShellEffectState.Unverified;
        _failSafeRequested = true;
    }

    /// <summary>
    /// Released is NOT "ReleaseAsync returned". It requires every in-flight effect capable of leaving an
    /// affordance to be reconciled AND the required compensation to have completed positively.
    /// </summary>
    private void TryComplete()
    {
        if (_state != TrayLifecycleState.Releasing)
        {
            return;
        }

        if (_reconciliationPending == 0 && _effect is ShellEffectState.Deleted or ShellEffectState.NotIssued)
        {
            _state = TrayLifecycleState.Released;
        }
    }

    private void EnterLost(string reason)
    {
        _logger.LogWarning("The tray affordance is lost: {Reason}.", reason);
        _state = TrayLifecycleState.Lost;
        _episodeActive = false;

        if (_effect == ShellEffectState.MayExist)
        {
            _cleanupAttempts = 0;
            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);
        }
    }

    private void Emit(EffectKind kind, TimeSpan delay) =>
        _pending.Enqueue(new Effect(kind, _generation, ++_sequence, delay));

    private Outcome Result(TrayLifecycleState before)
    {
        var after = _state;
        var publish = Project(before) != Project(after);
        var failSafe = _failSafeRequested;
        _failSafeRequested = false;
        return new Outcome(failSafe, publish, Project(after));
    }

    private static TrayAffordanceState Project(TrayLifecycleState state) => state switch
    {
        TrayLifecycleState.Unavailable => TrayAffordanceState.Unavailable,
        TrayLifecycleState.Available => TrayAffordanceState.Available,
        TrayLifecycleState.Recovering => TrayAffordanceState.Recovering,
        TrayLifecycleState.Lost => TrayAffordanceState.Lost,
        TrayLifecycleState.Releasing => TrayAffordanceState.Lost,
        TrayLifecycleState.Released => TrayAffordanceState.Lost,
        _ => TrayAffordanceState.Unavailable
    };

    // ---------------------------------------------------------------------------------------------
    // Effect execution: outside the decision domain.
    // ---------------------------------------------------------------------------------------------

    private void Execute(Outcome outcome)
    {
        // FIRST, outside the lock, on this thread, and never behind the native gate: a queued delivery
        // would inherit the problem this replaced.
        if (outcome.FailSafeExit)
        {
            RunFailSafeExit();
        }

        DrainEffects();

        if (outcome.Publish)
        {
            PublishIfCurrent();
        }
    }

    private void DrainEffects()
    {
        // The gate serializes shell I/O and nothing else. It is never acquired inside the decision
        // domain, and the fail-safe path never waits for it.
        while (true)
        {
            Effect effect;
            lock (_decision)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                effect = _pending.Dequeue();
            }

            RunEffect(effect);
        }
    }

    private void RunEffect(Effect effect)
    {
        var (operation, _) = Describe(effect.Kind);

        switch (effect.Kind)
        {
            case EffectKind.ScheduleDebounce:
                Schedule(effect.Delay, () => Dispatch(new TrayEvent(TrayEventKind.DebounceElapsed, effect.Generation, false)));
                return;

            case EffectKind.ScheduleRetry:
                Schedule(effect.Delay, () => Dispatch(new TrayEvent(TrayEventKind.RetryDue, effect.Generation, false)));
                return;

            case EffectKind.ScheduleDeadline:
                Schedule(effect.Delay, () => Dispatch(new TrayEvent(TrayEventKind.DeadlineObserved, effect.Generation, false)));
                return;

            case EffectKind.AddIcon:
            case EffectKind.DeleteIcon:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect.Kind, null);
        }

        bool ok;
        lock (_nativeGate)
        {
            ok = _executor.Run(operation);
        }

        Dispatch(new TrayEvent(
            effect.Kind == EffectKind.AddIcon ? TrayEventKind.AddCompleted : TrayEventKind.CleanupCompleted,
            effect.Generation,
            ok));
    }

    private void Schedule(TimeSpan delay, Action callback)
    {
        var timer = _time.CreateTimer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
        lock (_decision)
        {
            _timers.Add(timer);
        }
    }

    /// <summary>
    /// The fail-safe exit request. NOT a delivery: a direct synchronous call to a sink injected at
    /// construction, outside the lock, on the transition's own thread, as the first effect. A queued
    /// delivery would inherit the very problem this replaced — RequestExit never running, and the 10 s
    /// watchdog is only armed inside RequestExit, so before that there is no net at all.
    /// </summary>
    private void RunFailSafeExit()
    {
        lock (_decision)
        {
            if (_failSafeCompleted)
            {
                return;
            }
        }

        for (var attempt = 1; attempt <= MaxFailSafeAttempts; attempt++)
        {
            try
            {
                _requestAuthoritativeExit();

                // Marked only AFTER a normal return: an exception must not consume the single shot.
                lock (_decision)
                {
                    _failSafeCompleted = true;
                }

                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "The fail-safe exit sink threw on attempt {Attempt} of {Max}.",
                    attempt,
                    MaxFailSafeAttempts);
            }
        }

        // The fixed bound is what stops an always-throwing sink from looping. Escalation is the S2's own
        // terminal step, injected: S2-T invokes, it does not decide how the process dies.
        try
        {
            _escalateTermination();
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "The terminal escalation sink also threw.");
        }
    }

    /// <summary>
    /// Delivery-time revalidation: an event being valid when it was queued is NOT sufficient. Session
    /// semantics are suppressed once the lifecycle is terminal, because they must never tell S2 to
    /// degrade or to trust an affordance while the process is on its way out.
    /// </summary>
    private void PublishIfCurrent()
    {
        lock (_decision)
        {
            if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)
            {
                return;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        List<ITimer> timers;
        lock (_decision)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timers = [.. _timers];
            _timers.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }
    }
}
