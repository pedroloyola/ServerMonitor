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

    /// <summary>
    /// Every declared effect kind, from the enum itself.
    /// <para>
    /// The test that claimed to cover all kinds automatically used a hard-coded <c>Range(0, 6)</c>, so a
    /// seventh kind compiled and passed — in the very test that existed to make a new kind impossible to
    /// forget. The count now comes from the type.
    /// </para>
    /// </summary>
    /// <summary>
    /// Raised on every LIFECYCLE change, including the ones that publish nothing.
    /// </summary>
    /// <remarks>
    /// Test seam. <c>StateChanged</c> is the product notification and is deliberately suppressed for
    /// terminal transitions, so a test that needs to know another thread has reached <c>Releasing</c>
    /// had no signal at all and fell back to polling a wall clock — which decides races by how busy the
    /// machine running the tests is. Handlers must do nothing but signal: this is raised while the
    /// decision lock is held.
    /// </remarks>
    internal event EventHandler? LifecycleChangedForTests;

    /// <summary>
    /// Runs inside <see cref="PublishIfCurrent"/>, between taking the delivery token and revalidating.
    /// </summary>
    /// <remarks>
    /// Test seam, and the only way to exercise that gap deliberately — which is exactly how the gap was
    /// found. A guard on a window that no test can enter is a guard nothing falsifies, and this slice has
    /// twice shipped one of those; this is the probe that stops the third.
    /// </remarks>
    internal Action? BeforeDeliveryForTests;

    /// <summary>The generation the machine is currently on. Test seam; see <see cref="InjectForTests"/>.</summary>
    internal long GenerationForTests
    {
        get { lock (_decision) { return _generation; } }
    }

    /// <summary>
    /// Injects an event with a chosen generation, so a test can BUILD a state the machine cannot reach
    /// on its own.
    /// </summary>
    /// <remarks>
    /// It exists for exactly one condition. CV-19 requires the obsolescence guard to carve out
    /// effect-conclusion events, and removing that carve-out failed no test — because a stale
    /// <c>AddCompleted</c> in a NON-terminal state has no natural path: every generation bump except
    /// <c>BeginEpisode</c> also enters a terminal state, and step 1 of the preamble short-circuits there.
    /// <para>
    /// A guard whose removal changes nothing is a guard a refactor can delete with everything still
    /// green, which is the failure mode this whole slice has been policing. Rather than leave the
    /// condition asserted-but-unproven, the state is built here: the door is <c>internal</c>, takes only
    /// values the event type already allows, and goes through the SAME <see cref="Dispatch"/> every real
    /// event uses — it cannot reach a code path production cannot.
    /// </para>
    /// </remarks>
    internal void InjectForTests(TrayEventKind kind, long generation, bool success) =>
        Dispatch(new TrayEvent(kind, generation, success));

    internal static int[] EffectKindsForTests() =>
        [.. Enum.GetValues<EffectKind>().Select(kind => (int)kind)];

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

    /// <summary>
    /// Runs a scheduled continuation on the UI thread. See <see cref="Schedule"/>.
    /// </summary>
    private readonly Action<Action> _marshalToUi;
    private readonly Action _escalateTermination;
    private readonly ILogger _logger;

    private readonly object _decision = new();
    private readonly object _nativeGate = new();
    private readonly object _deliveryGate = new();

    /// <summary>
    /// Queued effects, sorted by sequence because they are appended under the decision lock, and drained
    /// by one thread at a time.
    /// <para>
    /// It was a <c>Queue</c> drained by whoever arrived, and the dequeue happened OUTSIDE the gate, so
    /// two drainers could take A then B and let B reach the shell first. <see cref="Effect.Sequence"/>
    /// existed and was never read, which is the worst shape a guarantee can have: written down, and not
    /// enforced. A deterministic probe parked before the gate showed a later DELETE executing before its
    /// ADD, which leaves the icon alive and destroys the compensation invariant.
    /// </para>
    /// </summary>
    private readonly List<Effect> _pending = [];

    private readonly List<ITimer> _timers = [];

    /// <summary>
    /// The machine whose <see cref="Execute"/> frame owns the current thread, if any.
    /// <para>
    /// Two things re-enter: a shell call concludes by dispatching its result, and a subscriber can act
    /// synchronously from inside a publication. Either one can transition, emit effects with HIGHER
    /// sequence numbers, and drain them while a LOWER one is still staged in the frame above — the same
    /// inversion the sequence check catches between threads, reached from the inside.
    /// </para>
    /// <para>
    /// The guard is on the whole frame and not just on the drain, because the subscriber re-enters from
    /// within the publication, which happens BEFORE the outer frame commits. A nested frame publishes and
    /// commits — neither may be deferred — and leaves the draining to the frame that owns it.
    /// </para>
    /// </summary>
    [ThreadStatic]
    private static TrayStateMachine? _executingMachine;

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

    /// <summary>Sequence of the last effect handed to the shell. Only ever moves forward.</summary>
    private long _lastExecutedSequence;

    /// <summary>Monotonic delivery token, so an older notification can never land after a newer one.</summary>
    private long _publishSequence;

    private long _deliveredSequence;
    private bool _disposed;

    public TrayStateMachine(
        INativeTrayRegistration native,
        Action requestAuthoritativeExit,
        Action escalateTermination,
        TimeProvider timeProvider,
        ILogger<TrayStateMachine> logger,
        EpisodeFrequencyLimiter? limiter = null,
        Action<Action>? marshalToUi = null)
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

        // Default: run inline. That is right for a caller that already owns the thread — every test, and
        // the establishment path itself. Production passes the UI dispatcher; see Schedule.
        _marshalToUi = marshalToUi ?? (continuation => continuation());

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

    /// <summary>
    /// What a transition decided. The effects are STAGED here rather than published to
    /// <see cref="_pending"/> inside the transition: a drainer that is already running must not be able
    /// to pick up this transition's shell work before this transition has published its own state
    /// change. They are committed in <see cref="Execute"/>, after the publication, with the sequence
    /// numbers they were given under the lock — so the ordering is the decision's, not the commit's.
    /// </summary>
    /// <param name="Deadline">
    /// The deadline of the episode that took this decision, carried so the delivery can be checked
    /// against the RIGHT deadline. Reading the field at delivery time does not work: a successful
    /// recovery clears the episode before the publication is delivered, so the state that matters is
    /// gone by then.
    /// </param>
    private readonly record struct Outcome(
        bool FailSafeExit, bool Publish, TrayAffordanceState State, long Deadline);

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
        // REDUNDANT DELETE. Publication now runs before the drain, which makes a synchronous re-entry
        // possible: a subscriber that degrades, finds no window and asks for the authoritative exit
        // reaches Release on this very stack, and Release queues its own delete for an icon a previous
        // delete has already positively removed. Shell_NotifyIcon(NIM_DELETE) returns FALSE when there is
        // nothing to delete, and that false is not a cleanup failure — it reports exactly the state the
        // cleanup wanted. Only a positively verified removal reaches this branch: anything that could
        // have recreated the icon sets _effect back to MayExist first, so a real failure still escalates.
        if (_effect == ShellEffectState.Deleted)
        {
            return;
        }

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
        if (_effect == ShellEffectState.Deleted)
        {
            // Same rule as HandleCleanupCompleted: a delete for an icon already positively removed is
            // redundant, not failed. TryComplete still runs, because the release is resolved.
            TryComplete();
            return;
        }

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

    /// <summary>
    /// Queues an effect. Called with <see cref="_decision"/> held, so the sequence numbers are assigned
    /// in the linearized order of the decisions that produced them, and appending keeps the queue sorted
    /// by construction.
    /// </summary>
    /// <remarks>
    /// A previous attempt staged effects per transition and committed them AFTER the publication, so a
    /// drainer could not reach a transition's shell work before that transition had published. It had to
    /// be withdrawn: commits from concurrent transitions land in commit order, not decision order, so a
    /// Delete decided second could be committed first and run before the Add it compensates — the exact
    /// inversion being fixed, reintroduced by the fix. The machine's own ordering check caught it on the
    /// first run.
    /// <para>
    /// The two properties cannot both hold with two drainers, and this is the one whose violation
    /// corrupts state rather than delaying a notification: an inverted Delete leaves a live icon nobody
    /// will remove. The residual is recorded in the CV map.
    /// </para>
    /// </remarks>
    private void Emit(EffectKind kind, TimeSpan delay) =>
        _pending.Add(new Effect(kind, _generation, ++_sequence, delay));

    private Outcome Result(TrayLifecycleState before)
    {
        var after = _state;
        var publish = Project(before) != Project(after);
        var failSafe = _failSafeRequested;
        _failSafeRequested = false;

        if (before != after)
        {
            LifecycleChangedForTests?.Invoke(this, EventArgs.Empty);
        }

        return new Outcome(failSafe, publish, Project(after), _deadlineTimestamp);
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

        // PUBLISH BEFORE THE SHELL I/O.
        //
        // The state change is a DECISION, already taken under the lock; the Delete that follows is
        // cleanup of an icon that may or may not still exist. Draining first put the notification behind
        // a synchronous Shell_NotifyIcon that waits on the native gate — possibly behind an in-flight
        // NIM_ADD — and did so during an Explorer restart, which is the one moment shell calls are least
        // predictable. Measured at 3-5 ms on a healthy shell, but there is no measured bound during a
        // restart, and a bound nobody has measured is not a bound. The 1.5 s the lifecycle is promised
        // must not be spent waiting for I/O whose duration is unknown.
        //
        // The fail-safe exit is ahead of BOTH for the older and stronger reason: it is the only progress
        // mechanism left when everything else is stuck, so it may never depend on the gate.
        var nested = ReferenceEquals(_executingMachine, this);
        if (!nested)
        {
            _executingMachine = this;
        }

        try
        {
            if (outcome.Publish)
            {
                PublishIfCurrent(outcome);
            }

            if (!nested)
            {
                DrainEffects();
            }
        }
        finally
        {
            if (!nested)
            {
                _executingMachine = null;
            }
        }
    }

    /// <summary>
    /// Runs a shell call that this machine does not own, under the SAME gate that serializes its own
    /// <c>NIM_ADD</c> and <c>NIM_DELETE</c>.
    /// </summary>
    /// <remarks>
    /// The DPI update replaces and destroys an <c>HICON</c> and issues <c>NIM_MODIFY</c>. It was made
    /// directly from the adapter, outside this gate, so it could overlap a recovery <c>NIM_ADD</c> — two
    /// unsynchronized callers on one icon, one of them freeing a handle the other may be using.
    /// <para>
    /// It takes a delegate rather than growing <see cref="INativeTrayRegistration"/> or the effect
    /// protocol: the capability stays where CV-20 put it, the effect kinds stay a closed passive set, and
    /// what crosses this boundary is only the right to be serialized.
    /// </para>
    /// </remarks>
    internal void InvokeUnderShellGate(Action shellCall)
    {
        ArgumentNullException.ThrowIfNull(shellCall);

        lock (_nativeGate)
        {
            shellCall();
        }
    }

    /// <summary>
    /// Runs committed effects in sequence order, ONE drainer at a time.
    /// </summary>
    /// <remarks>
    /// The gate is taken around the whole loop, dequeue included. Taking it only around the shell call —
    /// which is what this used to do — let two drainers take A and B and then race for the gate, so B
    /// could reach the shell first. The sequence is now both respected and CHECKED: a violation throws
    /// rather than silently producing a live icon nobody asked for.
    /// </remarks>
    private void DrainEffects()
    {
        lock (_nativeGate)
        {
            while (true)
            {
                Effect effect;
                lock (_decision)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    effect = _pending[0];
                    _pending.RemoveAt(0);

                    if (effect.Sequence <= _lastExecutedSequence)
                    {
                        throw new InvalidOperationException(
                            $"Effect {effect.Sequence} would run after {_lastExecutedSequence}; "
                            + "the shell ordering has been violated.");
                    }

                    _lastExecutedSequence = effect.Sequence;
                }

                RunEffect(effect);
            }
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

        // The gate is already held by DrainEffects, which owns it for the whole drain.
        var ok = _executor.Run(operation);

        Dispatch(new TrayEvent(
            effect.Kind == EffectKind.AddIcon ? TrayEventKind.AddCompleted : TrayEventKind.CleanupCompleted,
            effect.Generation,
            ok));
    }

    /// <summary>
    /// Schedules a continuation, ON THE UI THREAD.
    /// </summary>
    /// <remarks>
    /// The timer itself fires wherever the provider fires it — for the system provider, the thread pool.
    /// Running the continuation there was a real divergence, not a detail: the recovery <c>NIM_ADD</c>
    /// would have executed off the UI thread, while the design, this map's CV-7/CV-8 rows and
    /// <see cref="EpisodeFrequencyLimiter"/>'s own justification all say the shell calls happen on the UI
    /// thread — and CV-8's cost measurements were taken there. A document asserting one topology while
    /// the code ran another is the defect this slice has been correcting all along, so the code moves to
    /// match the approved design rather than the design being quietly restated.
    /// <para>
    /// If the dispatcher refuses the work — it is shutting down — the continuation runs inline. Dropping
    /// it would strand an episode with a deadline nobody will observe.
    /// </para>
    /// </remarks>
    private void Schedule(TimeSpan delay, Action callback)
    {
        var timer = _time.CreateTimer(_ => _marshalToUi(callback), null, delay, Timeout.InfiniteTimeSpan);
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
    /// Delivery-time revalidation: an event being valid when it was decided is NOT sufficient.
    /// </summary>
    /// <remarks>
    /// This used to release the decision lock BETWEEN validating and invoking, and that gap was
    /// measurable: with a barrier parked in it, a Release won and the delivery still went out
    /// afterwards, and — the one that matters — a decision taken before the deadline could deliver
    /// <c>Available</c> after it. That is the root invariant this design spent eight rounds on: nothing
    /// after the deadline publishes Available.
    /// <para>
    /// Three things close it. Deliveries are serialized against each other, so two of them cannot
    /// interleave. Each carries a monotonic token, so a delivery that lost a race is dropped instead of
    /// landing after a newer one. And the state is revalidated INSIDE that serialization, immediately
    /// before the invocation, against both the terminal states and the deadline — the notification is
    /// parameterless and subscribers read <see cref="State"/>, so the value they see is live either way,
    /// but a notification that should never have gone out must not go out at all.
    /// </para>
    /// </remarks>
    private void PublishIfCurrent(Outcome outcome)
    {
        long token;
        lock (_decision)
        {
            token = ++_publishSequence;
        }

        BeforeDeliveryForTests?.Invoke();

        lock (_deliveryGate)
        {
            if (token <= Volatile.Read(ref _deliveredSequence))
            {
                // A newer delivery already went out. This one is stale by construction.
                return;
            }

            lock (_decision)
            {
                if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)
                {
                    // Release dominates. Suppressed here rather than at decision time, because the
                    // Release may have won AFTER this delivery was decided.
                    return;
                }

                if (Project(_state) == TrayAffordanceState.Available
                    && outcome.Deadline != 0
                    && _time.GetTimestamp() >= outcome.Deadline)
                {
                    // The deadline passed between the decision and this moment. Publishing Available now
                    // would be exactly the late success the whole deadline exists to refuse.
                    _logger.LogWarning(
                        "An Available notification was decided before the deadline and reached delivery after it; dropped.");
                    return;
                }

                _deliveredSequence = token;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
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
