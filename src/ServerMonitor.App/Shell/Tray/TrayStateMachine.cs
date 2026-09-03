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

    /// <summary>
    /// A scheduled continuation will never run: the UI dispatcher refused it. The episode can make no
    /// further progress on its own, so it ends here rather than waiting for a deadline nobody will
    /// deliver.
    /// </summary>
    ContinuationRefused,
    Release
}

/// <summary>An event carrying its generation, so the preamble can judge obsolescence.</summary>
/// <param name="Outcome">
/// What the shell did, for events that report a shell operation. Kept as the TYPED outcome rather than a
/// boolean, because the lifecycle needs to distinguish an add that never created anything from one that
/// created an icon and then failed — those decide whether removal is required at all.
/// </param>
internal readonly record struct TrayEvent(TrayEventKind Kind, long Generation, ShellOutcome Outcome)
{
    /// <summary>Whether the operation reported success. Convenience for the paths that only need that.</summary>
    internal bool Success => Outcome == ShellOutcome.Succeeded;

    /// <summary>
    /// Whether the shell may be holding an icon because of the operation this event reports. False only
    /// when the add itself was refused, which is the case that makes cleanup unnecessary.
    /// </summary>
    internal bool MayHaveCreatedAnEffect => Outcome is ShellOutcome.Succeeded or ShellOutcome.FailedWithPossibleEffect;
}

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
    Unverified = 3,

    /// <summary>
    /// Every add was observed to fail at <c>NIM_ADD</c> itself, and nothing is in flight that could still
    /// create an icon. There is nothing to remove, and there never was.
    /// </summary>
    NeverCreated = 4
}

/// <summary>
/// What became of the obligation to remove our icon from the shell.
/// </summary>
/// <remarks>
/// The model was binary — cleanup verified, or not — and that could not express the difference the human
/// decision names: cleanup that <b>was needed and could not be verified</b> is not the same as cleanup
/// that <b>was never needed because no icon was ever created</b>. Collapsing them made a machine whose
/// <c>NIM_ADD</c> always fails exit rather than degrade, because a <c>NIM_DELETE</c> for an icon that
/// never existed returns false and was read as a failure.
/// <para>
/// <b>This is a refinement of CV-16, not a relaxation.</b> <see cref="Unverified"/> is only ever reachable
/// when removal was REQUIRED, and it still authorises nothing: it goes to the fail-safe exit.
/// <see cref="NotRequired"/> is not a cleanup failure and must never be mapped to one.
/// </para>
/// </remarks>
internal enum CleanupDisposition
{
    /// <summary>
    /// No icon was ever created and none can still appear, so there is nothing to remove. NOT a failure.
    /// </summary>
    NotRequired = 0,

    /// <summary>Removal was required and the shell confirmed it. The session may continue degraded.</summary>
    Verified = 1,

    /// <summary>
    /// Removal was REQUIRED and could not be established within its budget. CV-16 applies: the process
    /// may not continue, degraded or otherwise.
    /// </summary>
    Unverified = 2
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
    /// A queued effect and whether it may run yet.
    /// <para>
    /// Ordering the effects by sequence fixed one half: a Delete can no longer overtake the Add it
    /// compensates. It left the neighbour open — an ALREADY RUNNING drainer could still pick up an effect
    /// emitted by another transition and reach the shell before THAT transition published its state
    /// change. Staging the effects and committing them after the publication was the first attempt and
    /// had to be withdrawn: it made commit order diverge from decision order, reintroducing the very
    /// inversion being fixed.
    /// </para>
    /// <para>
    /// Readiness solves both at once. Effects are queued in sequence order at decision time, so the order
    /// is never in question, and they are marked runnable only after their own transition has published.
    /// A drainer STOPS at the first effect that is not ready rather than skipping it, because skipping is
    /// what would reorder.
    /// </para>
    /// </summary>
    private readonly record struct PendingEffect(Effect Effect, bool Ready);

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

    /// <summary>
    /// Runs immediately before <c>StateChanged</c> is invoked, after every check has passed.
    /// </summary>
    /// <remarks>
    /// <see cref="BeforeDeliveryForTests"/> sits before the checks and therefore cannot observe the one
    /// window that mattered: between the LAST check and the invocation. A mutation that moved the
    /// invocation back outside the lock survived precisely because no probe could stand there. This one
    /// can.
    /// </remarks>
    internal Action? AtInvocationForTests;

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
        Dispatch(new TrayEvent(
            kind,
            generation,
            success ? ShellOutcome.Succeeded : ShellOutcome.FailedWithoutEffect));

    /// <summary>
    /// Whether the effect at the head of the queue may run right now. Test seam, read-only.
    /// </summary>
    /// <remarks>
    /// This replaces a seam that RAN a drain from inside the publication. That version re-entered the
    /// machine while it held the decision lock, recursed through effect completions, and overflowed the
    /// stack — which killed the test host, and <c>dotnet test</c> printed a green "Passed!" line for the
    /// seven tests that had finished before the crash. Observing the queue answers the same question
    /// without executing anything: a drainer arriving at this instant would find exactly this.
    /// </remarks>
    /// <summary>
    /// Runs a drain from wherever the test stands, as a second drainer would. Test seam.
    /// </summary>
    /// <remarks>
    /// Re-entrant by design: the drain takes the shell gate and then the decision lock, both of which a
    /// probe already holding the decision lock re-enters on its own thread. That is what lets a test ask
    /// "what would a drainer do RIGHT NOW" at a point that cannot otherwise be reached.
    /// </remarks>
    internal void DrainForTests() => DrainEffects();

    internal bool HeadEffectIsRunnableForTests
    {
        get
        {
            lock (_decision)
            {
                return _pending.Count > 0 && _pending[0].Ready;
            }
        }
    }

    internal static int[] EffectKindsForTests() =>
        [.. Enum.GetValues<EffectKind>().Select(kind => (int)kind)];

    /// <summary>The only type in the program that retains the shell capability.</summary>
    private sealed class EffectExecutor(INativeTrayRegistration native)
    {
        private readonly INativeTrayRegistration _native = native;

        /// <summary>
        /// Performs the operation and reports what the SHELL did, keeping the two native calls of an add
        /// distinguishable.
        /// </summary>
        /// <remarks>
        /// It used to return <c>Add() &amp;&amp; SetVersion()</c>. That single boolean is where the
        /// information was lost: a false could mean the icon was never created or that it was created and
        /// only the version call failed, and the lifecycle needs to tell those apart to know whether
        /// removal is required at all.
        /// </remarks>
        internal ShellOutcome Run(NativeTrayOperation operation)
        {
            switch (operation)
            {
                case NativeTrayOperation.Add:
                    if (!_native.Add())
                    {
                        // NIM_ADD itself refused: the shell is not holding an icon because of this call.
                        return ShellOutcome.FailedWithoutEffect;
                    }

                    return _native.SetVersion()
                        ? ShellOutcome.Succeeded
                        // The icon EXISTS and only the version call failed. Removing it is mandatory.
                        : ShellOutcome.FailedWithPossibleEffect;

                case NativeTrayOperation.Delete:
                    return _native.Delete() ? ShellOutcome.Succeeded : ShellOutcome.FailedWithoutEffect;

                case NativeTrayOperation.None:
                    return ShellOutcome.NotPerformed;

                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------

    private readonly EffectExecutor _executor;
    private readonly EpisodeFrequencyLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly Action _requestAuthoritativeExit;

    /// <summary>
    /// Hands a scheduled continuation to the UI thread. Returns false when it will not run there.
    /// See <see cref="Schedule"/>.
    /// </summary>
    private readonly Func<Action, bool> _marshalToUi;
    private readonly Action _escalateTermination;
    private readonly ILogger _logger;

    private readonly object _decision = new();
    private readonly object _nativeGate = new();
    private readonly object _deliveryGate = new();

    /// <summary>
    /// Queued effects, sorted by sequence because they are appended under the decision lock, drained by
    /// one thread at a time, and NOT EXECUTABLE until the transition that produced them has published.
    /// <para>
    /// It was a <c>Queue</c> drained by whoever arrived, and the dequeue happened OUTSIDE the gate, so
    /// two drainers could take A then B and let B reach the shell first. <see cref="Effect.Sequence"/>
    /// existed and was never read, which is the worst shape a guarantee can have: written down, and not
    /// enforced. A deterministic probe parked before the gate showed a later DELETE executing before its
    /// ADD, which leaves the icon alive and destroys the compensation invariant.
    /// </para>
    /// </summary>
    private readonly List<PendingEffect> _pending = [];

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

    /// <summary>
    /// Whether any add has ever been observed to leave something behind — it succeeded, or it failed at a
    /// point where the icon may already exist.
    /// </summary>
    /// <remarks>
    /// Once true, a later add that is refused outright cannot downgrade the obligation: the earlier icon
    /// may still be there. It is cleared only by a positively verified removal.
    /// </remarks>
    private bool _shellMayHoldAnIcon;
    private bool _failSafeCompleted;
    private bool _failSafeRequested;

    /// <summary>Sequence of the last effect handed to the shell. Only ever moves forward.</summary>
    private long _lastExecutedSequence;

    /// <summary>Sequence range emitted by the transition in progress. Only touched under the lock.</summary>
    private long _emittedFrom;

    private long _emittedTo;

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
        Func<Action, bool>? marshalToUi = null)
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

        // Default: run inline and report success. That is right for a caller that already owns the
        // thread — every test, and the establishment path itself. Production passes the UI dispatcher.
        _marshalToUi = marshalToUi ?? (continuation =>
        {
            continuation();
            return true;
        });

        // The capability is forwarded, never retained by this class.
        _executor = new EffectExecutor(native);
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public TrayAffordanceState State
    {
        get { lock (_decision) { return Project(_state, _time.GetTimestamp()); } }
    }

    /// <summary>
    /// Runs <paramref name="enterBackground"/> only while the affordance is established, under the same
    /// lock that establishes it.
    /// </summary>
    /// <remarks>
    /// The determination and the act are ONE step. Handing out a boolean and letting the caller act on it
    /// later is what left a window in which the affordance could be lost between the answer and the hide.
    /// </remarks>
    public bool TryEnterBackground(Action enterBackground)
    {
        ArgumentNullException.ThrowIfNull(enterBackground);

        lock (_decision)
        {
            if (Project(_state, _time.GetTimestamp()) != TrayAffordanceState.Available)
            {
                return false;
            }

            enterBackground();
            return true;
        }
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
        get
        {
            // Kept as the shorthand the existing callers use: "nothing of ours is outstanding". It is now
            // derived from the disposition rather than being the model, because a boolean cannot say WHY.
            var disposition = Cleanup;
            return disposition is CleanupDisposition.Verified or CleanupDisposition.NotRequired;
        }
    }

    /// <summary>
    /// What became of the obligation to remove our icon.
    /// </summary>
    /// <remarks>
    /// <b>NotRequired is not a cleanup failure.</b> It is reported when no icon was ever created and none
    /// can still appear, which is the ordinary outcome of a machine whose <c>NIM_ADD</c> is refused — and
    /// mapping it onto a failure is what made that machine exit instead of degrading.
    /// <para>
    /// <b>CV-16 is refined, not relaxed.</b> <see cref="CleanupDisposition.Unverified"/> is only reachable
    /// from <c>MayExist</c>, that is, only when removal really was required, and it still authorises
    /// nothing.
    /// </para>
    /// </remarks>
    internal CleanupDisposition Cleanup
    {
        get
        {
            lock (_decision)
            {
                return _effect switch
                {
                    ShellEffectState.NotIssued => CleanupDisposition.NotRequired,
                    ShellEffectState.NeverCreated => CleanupDisposition.NotRequired,
                    ShellEffectState.Deleted => CleanupDisposition.Verified,
                    ShellEffectState.Unverified => CleanupDisposition.Unverified,

                    // MayExist: the obligation is live and not yet resolved. Reporting it as Unverified
                    // would escalate a cleanup that is merely still in progress; reporting it as
                    // NotRequired would be the bypass CV-16 exists to forbid. It is neither, so it is
                    // reported as the fail-closed one until the shell says otherwise.
                    ShellEffectState.MayExist => CleanupDisposition.Unverified,
                    _ => CleanupDisposition.Unverified
                };
            }
        }
    }

    /// <summary>Starts the initial establishment episode. Same arbiter as broadcast recovery.</summary>
    public void Establish() => Dispatch(new TrayEvent(TrayEventKind.Establish, 0, ShellOutcome.NotPerformed));

    /// <summary>A <c>TaskbarCreated</c> broadcast reached our window.</summary>
    public void NotifyTaskbarCreated() => Dispatch(new TrayEvent(TrayEventKind.TaskbarCreated, 0, ShellOutcome.NotPerformed));

    /// <summary>The single public terminal operation. Idempotent; a no-op once terminal.</summary>
    public void Release() => Dispatch(new TrayEvent(TrayEventKind.Release, 0, ShellOutcome.NotPerformed));

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
        bool FailSafeExit, bool Publish, TrayAffordanceState State, long Deadline,
        long EmittedFrom, long EmittedTo);

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

            case TrayEventKind.ContinuationRefused:
                // Not a deadline: the bound has NOT passed, and waiting for it would be waiting for a
                // timer whose continuation the dispatcher has just told us it will not run. The episode
                // is over because it can no longer progress, which is a different fact and gets its own
                // event rather than being smuggled through the deadline path.
                EnterLost("a scheduled continuation was refused by the UI dispatcher");
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
            _shellMayHoldAnIcon = true;
            _state = TrayLifecycleState.Available;
            _episodeActive = false;

            return;
        }

        RecordFailedAdd(trayEvent);

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

    /// <summary>
    /// Classifies an add that did not succeed: did it leave anything behind?
    /// </summary>
    /// <remarks>
    /// This is the distinction the whole of Question D turns on. <c>NIM_ADD</c> refused means the shell is
    /// not holding an icon because of this call, and if nothing else can still create one there is nothing
    /// to remove — so a later <c>NIM_DELETE</c> returning false is not a failure, it is the truth.
    /// <c>NIM_ADD</c> succeeding and <c>NIM_SETVERSION</c> failing is the opposite case: the icon exists
    /// and removal is mandatory.
    /// <para>
    /// FAIL CLOSED where it cannot tell. <c>_effect</c> is set to <c>MayExist</c> BEFORE the call on
    /// purpose, and it is downgraded only when the shell reported a refusal AND nothing else is in
    /// flight that could still create an icon AND no earlier add ever left one behind.
    /// </para>
    /// </remarks>
    private void RecordFailedAdd(TrayEvent trayEvent)
    {
        if (trayEvent.MayHaveCreatedAnEffect)
        {
            _effect = ShellEffectState.MayExist;
            _shellMayHoldAnIcon = true;
            return;
        }

        if (_reconciliationPending > 0 || _shellMayHoldAnIcon)
        {
            // Something may still create an icon, or one was created earlier. Required until reconciled.
            return;
        }

        _effect = ShellEffectState.NeverCreated;
    }

    private void ReconcileStale(TrayEvent trayEvent)
    {
        // The result is obsolete for the lifecycle. It is NOT obsolete for the shell: if it may have
        // recreated the icon, a compensating Delete is mandatory — including the case where NIM_ADD
        // succeeded and only the version call failed.
        if (trayEvent.MayHaveCreatedAnEffect)
        {
            _effect = ShellEffectState.MayExist;
            _shellMayHoldAnIcon = true;
            _cleanupAttempts = 0;
            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);
        }
        else
        {
            RecordFailedAdd(trayEvent);
            TryComplete();
        }
    }

    private void HandleCleanupCompleted(TrayEvent trayEvent)
    {
        if (_effect is ShellEffectState.NeverCreated or ShellEffectState.NotIssued)
        {
            // A Delete that ran anyway — a stale effect, or a caller doing it by accident — reports false
            // because there is nothing to delete. That is the state we want, not a failure, and it must
            // never turn NotRequired into Unverified.
            return;
        }

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
            _shellMayHoldAnIcon = false;
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
        if (_effect is ShellEffectState.NeverCreated or ShellEffectState.NotIssued)
        {
            // Same rule on the terminal path. TryComplete still runs: the release IS resolved, because
            // there was never anything to remove.
            TryComplete();
            return;
        }

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
            _shellMayHoldAnIcon = false;
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

        // NeverCreated resolves a release exactly as a verified removal does: there is nothing to remove
        // and there never was. Treating it as unresolved is what made a failed registration hang on to a
        // Releasing state and then escalate.
        if (_reconciliationPending == 0
            && _effect is ShellEffectState.Deleted
                or ShellEffectState.NotIssued
                or ShellEffectState.NeverCreated)
        {
            _state = TrayLifecycleState.Released;
        }
    }

    private void EnterLost(string reason)
    {
        _logger.LogWarning("The tray affordance is lost: {Reason}.", reason);
        _state = TrayLifecycleState.Lost;
        _episodeActive = false;

        // No Delete when nothing was ever created: the human decision is explicit that pointless delete
        // retries should not run at all when cleanup is provably NotRequired.
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
    private void Emit(EffectKind kind, TimeSpan delay)
    {
        var effect = new Effect(kind, _generation, ++_sequence, delay);
        _emittedFrom = _emittedFrom == 0 ? effect.Sequence : _emittedFrom;
        _emittedTo = effect.Sequence;
        _pending.Add(new PendingEffect(effect, Ready: false));
    }

    private Outcome Result(TrayLifecycleState before)
    {
        var after = _state;
        var now = _time.GetTimestamp();

        // ProjectState, not Project: a transition happened or it did not, and that is independent of what
        // the clock has since made of it. Using the clock-aware projection here meant a real transition
        // into Lost produced no notification whenever the projection had already reached Lost on its own,
        // so the lifecycle was never told to degrade.
        var publish = ProjectState(before) != ProjectState(after);
        var failSafe = _failSafeRequested;
        _failSafeRequested = false;

        if (before != after)
        {
            LifecycleChangedForTests?.Invoke(this, EventArgs.Empty);
        }

        var from = _emittedFrom;
        var to = _emittedTo;
        _emittedFrom = 0;
        _emittedTo = 0;

        return new Outcome(failSafe, publish, Project(after, now), _deadlineTimestamp, from, to);
    }

    private static TrayAffordanceState ProjectState(TrayLifecycleState state) => state switch
    {
        TrayLifecycleState.Unavailable => TrayAffordanceState.Unavailable,
        TrayLifecycleState.Available => TrayAffordanceState.Available,
        TrayLifecycleState.Recovering => TrayAffordanceState.Recovering,
        TrayLifecycleState.Lost => TrayAffordanceState.Lost,
        TrayLifecycleState.Releasing => TrayAffordanceState.Lost,
        TrayLifecycleState.Released => TrayAffordanceState.Lost,
        _ => TrayAffordanceState.Unavailable
    };

    /// <summary>
    /// THE projection: what the affordance is, evaluated against the clock at this instant.
    /// </summary>
    /// <remarks>
    /// <b>The deadline lives here, and only here.</b> It used to be checked at the delivery site, which
    /// made it a gate someone had to pass rather than part of what the value MEANS — and there was always
    /// another way past: the clock kept moving between the check and the invocation, and every other
    /// reader (<c>CanEnterBackground</c>, a subscriber reading <see cref="State"/>) never went through the
    /// check at all. Evaluating it inside the projection removes the notion of a bypass: there is no
    /// second place to enforce it, because it is not enforced, it is computed.
    /// </remarks>
    /// <param name="monotonicNow">The reading of the clock this answer is for.</param>
    private TrayAffordanceState Project(TrayLifecycleState state, long monotonicNow)
    {
        var projected = ProjectState(state);

        // The bound belongs to the EPISODE: while one is live and overdue, nothing it might have
        // established can be reported as usable. A recovery that concluded — successfully, inside the
        // bound — ends the episode, and with it the bound; its proof does not expire.
        if (_episodeActive
            && monotonicNow >= _deadlineTimestamp
            && projected is TrayAffordanceState.Available or TrayAffordanceState.Recovering)
        {
            // Past the bound, an unproven affordance is Lost. This is also what keeps an episode whose
            // continuations were never delivered from sitting in Recovering for ever: the terminal
            // projection is a fact about time, not something a timer has to deliver.
            return TrayAffordanceState.Lost;
        }

        return projected;
    }

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
            try
            {
                if (outcome.Publish)
                {
                    PublishIfCurrent(outcome);
                }
            }
            finally
            {
                // Only NOW may this transition's effects run — queued in order at decision time so
                // nothing can overtake them, released here so no drainer reaches the shell on this
                // transition's behalf before it has said what happened.
                //
                // In a FINALLY, because the release is an OBLIGATION and an obligation discharged only on
                // the success path is not discharged. A subscriber that threw on Lost used to leave the
                // mandatory compensating Delete unrunnable for ever: external code could permanently
                // prevent the removal of our own icon, which is the one thing Option A promised could
                // never happen.
                ReleaseEmittedEffects(outcome);
            }

            // A fail-safe raised DURING the publication — an unacknowledged loss — is run here, outside
            // the decision lock and before the drain, on the same terms as one raised by a transition.
            if (TakeFailSafeRequest())
            {
                RunFailSafeExit();
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
    /// <summary>
    /// Reads and clears a fail-safe request raised outside a transition. Under the lock, like every other
    /// read of that flag.
    /// </summary>
    private bool TakeFailSafeRequest()
    {
        lock (_decision)
        {
            if (!_failSafeRequested)
            {
                return false;
            }

            _failSafeRequested = false;
            return true;
        }
    }

    private void ReleaseEmittedEffects(Outcome outcome)
    {
        if (outcome.EmittedTo == 0)
        {
            return;
        }

        lock (_decision)
        {
            for (var index = 0; index < _pending.Count; index++)
            {
                var pending = _pending[index];
                if (pending.Effect.Sequence >= outcome.EmittedFrom
                    && pending.Effect.Sequence <= outcome.EmittedTo)
                {
                    _pending[index] = pending with { Ready = true };
                }
            }
        }
    }

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

                    var head = _pending[0];
                    if (!head.Ready)
                    {
                        // STOP, do not skip: skipping would let a later effect overtake this one, which
                        // is the inversion the sequence exists to prevent. The transition that owns this
                        // effect drains it once it has published.
                        return;
                    }

                    effect = head.Effect;
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
                Schedule(effect.Delay, () => Dispatch(new TrayEvent(TrayEventKind.DebounceElapsed, effect.Generation, ShellOutcome.NotPerformed)));
                return;

            case EffectKind.ScheduleRetry:
                Schedule(effect.Delay, () => Dispatch(new TrayEvent(TrayEventKind.RetryDue, effect.Generation, ShellOutcome.NotPerformed)));
                return;

            case EffectKind.ScheduleDeadline:
                Schedule(effect.Delay, () => Dispatch(new TrayEvent(TrayEventKind.DeadlineObserved, effect.Generation, ShellOutcome.NotPerformed)));
                return;

            case EffectKind.AddIcon:
            case EffectKind.DeleteIcon:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(effect), effect.Kind, null);
        }

        // The gate is already held by DrainEffects, which owns it for the whole drain.
        var outcome = _executor.Run(operation);

        Dispatch(new TrayEvent(
            effect.Kind == EffectKind.AddIcon ? TrayEventKind.AddCompleted : TrayEventKind.CleanupCompleted,
            effect.Generation,
            outcome));
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
    /// If the dispatcher refuses the work the continuation is DROPPED, not run inline. Running it inline
    /// was the first version and it undid the guarantee it was there to make: the fallback executed on
    /// the timer's own thread, so the topology held only on the happy path — and a continuation running
    /// there is a second drainer, which is the thing the ordering work exists to exclude. A guarantee
    /// that holds only when nothing goes wrong is not a guarantee.
    /// </para>
    /// <para>
    /// The dispatcher refuses only while it is shutting down, and at that point <c>Release</c> is what
    /// runs and it absorbs every outstanding episode. Dropping is therefore bounded, and it is logged.
    /// </para>
    /// </remarks>
    private void Schedule(TimeSpan delay, Action callback) => Schedule(delay, callback, _generation);

    private void Schedule(TimeSpan delay, Action callback, long generation)
    {
        var timer = _time.CreateTimer(
            _ =>
            {
                if (_marshalToUi(callback))
                {
                    return;
                }

                // REFUSAL IS AN EVENT, NOT SILENCE.
                //
                // Dropping the continuation swapped a topology defect for a progress defect, which is
                // worse: the machine stayed alive in Recovering with no affordance, degrading nothing and
                // terminalizing nothing. The projection already makes every READER see Lost past the
                // bound, so nothing can be told the affordance is usable — but the compensating Delete
                // still has to be issued, and only a transition issues it.
                //
                // So the refusal terminalizes the episode. It runs on the timer's thread, which is the
                // one departure from the UI topology and it is bounded to this case: the dispatcher only
                // refuses while it is shutting down, so the thread it would have marshalled to no longer
                // exists. Ordering is safe there regardless — that is what the sequence and readiness
                // work bought.
                _logger.LogWarning(
                    "The UI dispatcher refused a scheduled tray continuation; terminalizing the episode "
                    + "instead of abandoning it.");

                Dispatch(new TrayEvent(TrayEventKind.ContinuationRefused, generation, ShellOutcome.NotPerformed));
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
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

        lock (_deliveryGate)
        {
            // The check and the invocation are now ONE critical section. Revalidating and then releasing
            // the lock left a window between the last check and StateChanged: a Release could still win
            // in it, and the deadline could still expire, and both were reproduced with a probe placed at
            // that exact point. Moving the check earlier only moved the window; the fix is that there is
            // no gap left to move it into.
            //
            // Holding the decision lock across a subscriber callback is safe HERE and nowhere else in
            // this machine: every dispatch and every drain runs on the UI thread (see Schedule), so there
            // is no second thread to block, and a subscriber that re-enters is on this very thread, where
            // the lock is re-entrant and the nesting guard stops it draining underneath us.
            lock (_decision)
            {
                if (token <= _deliveredSequence)
                {
                    // A newer delivery already went out. This one is stale by construction.
                    return;
                }

                // The probe belongs AT the point of delivery. Earlier, it could not observe anything that
                // happened after the last check — which is precisely where the defect was.
                BeforeDeliveryForTests?.Invoke();

                if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)
                {
                    // Release dominates. Checked here rather than at decision time, because the Release
                    // may have won AFTER this delivery was decided.
                    return;
                }

                _deliveredSequence = token;
                var delivered = Project(_state, _time.GetTimestamp());

                AtInvocationForTests?.Invoke();

                // Immediately before the event goes out, and inside the same critical section. The
                // projection governs what a READER sees; this governs whether the EVENT is delivered at
                // all, and they are two different observables. Checking earlier left the clock free to
                // move in between — which is exactly what was measured.
                if (ProjectState(_state) == TrayAffordanceState.Available
                    && outcome.Deadline != 0
                    && _time.GetTimestamp() >= outcome.Deadline)
                {
                    _logger.LogWarning(
                        "An Available notification was decided before the deadline and reached delivery "
                        + "after it; dropped.");
                    return;
                }

                try
                {
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception exception)
                {
                    // TWO PROPERTIES, TWO TREATMENTS.
                    //
                    // The machine never lets foreign code decide whether its own bookkeeping completes —
                    // that is why the release sits in a finally and why this catch exists at all, and the
                    // WndProc boundary has isolated callbacks for the same reason since CV-1.
                    //
                    // But the queue and the DELIVERY are not the same obligation, and the first fix made
                    // the catch protect both. The consumer of a LOSS is not one subscriber among several:
                    // it is what degrades the session or ends the process. If it failed, nobody has acted
                    // on the loss, and we cannot tell a failure to degrade from a degradation that
                    // happened — so the process may not be left alive with no affordance.
                    //
                    // An unacknowledged loss is an unverified cleanup by another name, and it takes the
                    // same answer: the authoritative exit.
                    _logger.LogError(exception, "A tray affordance subscriber threw; the notification is dropped.");

                    if (delivered is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)
                    {
                        _logger.LogError(
                            "The loss of the tray affordance was not acknowledged; requesting the authoritative exit.");
                        _failSafeRequested = true;
                    }
                }
            }
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
