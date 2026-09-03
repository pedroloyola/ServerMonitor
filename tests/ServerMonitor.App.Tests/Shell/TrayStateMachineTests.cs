using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Shell.Tray;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// The S2-T linearizable state machine (design <c>docs/m13-s2t-linearizable-state-machine.md</c>).
/// <para>
/// These are the tests the mutation matrix points at. Several of them deliberately PARK INSIDE a native
/// call, because the guarantees under test are exactly the ones that only show up while a shell call is
/// outstanding: that the deadline still terminalizes, that a late success is discarded, and that the
/// compensating Delete follows.
/// </para>
/// </summary>
public sealed class TrayStateMachineTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly BlockingNativeTrayRegistration _native = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<Task> _background = [];

    private int _exitRequests;
    private int _escalations;
    private Exception? _sinkFailure;

    private TrayStateMachine Create(
        Action? requestExit = null,
        Action? escalate = null,
        EpisodeFrequencyLimiter? limiter = null) =>
        new(
            _native,
            requestExit ?? (() => Interlocked.Increment(ref _exitRequests)),
            escalate ?? (() => Interlocked.Increment(ref _escalations)),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter);

    // ---------------------------------------------------------------------------------------------
    // Ordering under concurrent drainers, and delivery-time validation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A Delete decided while an Add is in flight reaches the shell AFTER it, with two threads.
    /// </summary>
    /// <remarks>
    /// The queue used to be dequeued OUTSIDE the gate, so two drainers could take A and B and then race
    /// for it — a later Delete could execute before its Add, leaving the icon alive and destroying the
    /// compensation invariant. <c>Effect.Sequence</c> existed and was never read.
    /// <para>
    /// Deterministic: the Add is parked inside the shell, and the releasing thread is known to be past
    /// its publication before the assertion runs, so "the Delete has not happened yet" is observed at a
    /// point fixed by signals rather than by a timeout.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_delete_decided_during_an_in_flight_add_still_reaches_the_shell_after_it()
    {
        using var machine = Create();
        using var releasing = new ManualResetEventSlim(false);

        _native.AddMayReturn.Reset();
        Run(machine.Establish);
        Assert.True(_native.AddEntered.Wait(Patience), "the Add never started");

        machine.LifecycleChangedForTests += (_, _) =>
        {
            if (machine.LifecycleState == TrayLifecycleState.Releasing)
            {
                releasing.Set();
            }
        };
        Run(machine.Release);

        // The releasing thread is past its transition — it decided, emitted its Delete, and is now in
        // commit/drain waiting for the gate the parked Add holds. Fixed by a signal, not by a timeout.
        Assert.True(releasing.Wait(Patience), "the release never transitioned");
        Assert.DoesNotContain("Delete", _native.Calls);

        _native.AddMayReturn.Set();
        WaitForBackground();

        var add = _native.Calls.IndexOf("Add");
        var delete = _native.Calls.IndexOf("Delete");
        Assert.True(add >= 0 && delete > add, $"order was [{string.Join(", ", _native.Calls)}]");
    }

    /// <summary>
    /// Two threads committing while the shell gate is held must not be able to invert the order in which
    /// their effects reach the shell.
    /// </summary>
    /// <remarks>
    /// This is the race Atlas measured. The queue used to be dequeued OUTSIDE the gate, so two drainers
    /// could take A and B and then race for it, and a later DELETE could execute before its ADD, leaving
    /// the icon alive. <c>Effect.Sequence</c> existed and was never read.
    /// <para>
    /// It is a REPEATED test rather than a probe, and that is a deliberate trade. Reproducing the
    /// interleaving needs both threads to sit between dequeue and gate at the same time, which no fixed
    /// seam can pin without changing the very structure under test — a probe placed inside the gate and a
    /// probe placed outside it are not the same probe. Holding the gate from the test while both threads
    /// commit puts them there, and the machine's own sequence check turns any inversion into an
    /// exception. The test is therefore ASYMMETRIC: it can only fail when the ordering is actually
    /// broken.
    /// </para>
    /// </remarks>
    [Fact]
    public void Concurrent_drainers_never_invert_the_effect_order()
    {
        const int iterations = 200;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var native = new BlockingNativeTrayRegistration();
            using var machine = new TrayStateMachine(
                native,
                () => { },
                () => { },
                _time,
                NullLogger<TrayStateMachine>.Instance);

            using var gateHeld = new ManualResetEventSlim(false);
            using var mayRelease = new ManualResetEventSlim(false);

            // Hold the shell gate so both worker threads pile up behind it with effects committed.
            var holder = Task.Run(() => machine.InvokeUnderShellGate(() =>
            {
                gateHeld.Set();
                mayRelease.Wait(Patience);
            }));

            Assert.True(gateHeld.Wait(Patience), "the gate was never taken");

            var establish = Task.Run(machine.Establish);
            var release = Task.Run(machine.Release);

            mayRelease.Set();

            // An inversion surfaces as the machine's own ordering check throwing, which arrives here.
            Task.WaitAll([holder, establish, release], Patience);

            var add = native.Calls.IndexOf("Add");
            if (add >= 0)
            {
                var delete = native.Calls.IndexOf("Delete");
                Assert.True(
                    delete < 0 || delete > add,
                    $"iteration {iteration}: [{string.Join(", ", native.Calls)}]");
            }
        }
    }

    /// <summary>
    /// Release dominates AT DELIVERY: a notification decided before it is not delivered after it.
    /// </summary>
    /// <remarks>
    /// The publication used to release the decision lock between validating the state and invoking the
    /// handler. With a barrier parked in that gap, a Release won and the delivery still went out.
    /// <para>
    /// Driven through the probe rather than with threads: the window is a specific point inside the
    /// publication, and entering it on purpose is both exact and repeatable, where a second thread would
    /// only sometimes arrive there.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_notification_decided_before_a_release_is_not_delivered_after_it()
    {
        using var machine = Create();
        var delivered = 0;

        machine.StateChanged += (_, _) => delivered++;

        var released = false;
        machine.BeforeDeliveryForTests = () =>
        {
            if (released)
            {
                return;
            }

            released = true;
            machine.Release();
        };

        machine.Establish();

        Assert.True(released, "the delivery probe never ran");
        Assert.Equal(TrayLifecycleState.Released, machine.LifecycleState);
        Assert.Equal(0, delivered);
    }

    /// <summary>
    /// Nothing after the deadline publishes Available — asserted on the DELIVERY, which is the half that
    /// was open. This is the root invariant eight rounds of design were spent on.
    /// </summary>
    /// <remarks>
    /// The decision half was already covered: the preamble terminalizes before the event is examined, so
    /// a late success never DECIDES Available. What was not covered is a decision taken legitimately
    /// before the deadline and delivered after it — the publication released the decision lock between
    /// validating and invoking, and in that gap the deadline can pass.
    /// <para>
    /// Built deterministically, because the window has to be entered on purpose: scheduled continuations
    /// are queued instead of run, so the deadline TIMER firing does not terminalize anything, and the
    /// clock is pushed past the deadline from inside the publication itself. The machine is therefore in
    /// the exact state that matters — still Available, episode still live, clock already past the
    /// deadline — at the moment the delivery decides whether to go out.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_decision_taken_before_the_deadline_is_not_delivered_as_Available_after_it()
    {
        var deferred = new List<Action>();
        var delivered = new List<TrayAffordanceState>();

        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: deferred.Add);

        machine.Establish();
        machine.StateChanged += (_, _) => delivered.Add(machine.State);

        machine.NotifyTaskbarCreated();
        Assert.Equal(TrayLifecycleState.Recovering, machine.LifecycleState);

        // Fire the debounce timer; its continuation is queued, not run.
        _time.Advance(TrayStateMachine.DebounceDelay);

        var pushedTheClock = false;
        machine.BeforeDeliveryForTests = () =>
        {
            if (pushedTheClock)
            {
                return;
            }

            pushedTheClock = true;

            // The deadline passes between the decision and this delivery. The deadline timer fires too,
            // but its continuation only queues, so nothing has terminalized: the machine is still
            // Available with a live episode.
            _time.Advance(TrayStateMachine.RecoveryDeadline);
        };

        // Run the debounce continuation: it attempts, the shell succeeds, and Available is DECIDED.
        RunDeferred(deferred);

        Assert.True(pushedTheClock, "the delivery probe never ran");
        Assert.DoesNotContain(TrayAffordanceState.Available, delivered);
    }

    private static void RunDeferred(List<Action> deferred)
    {
        for (var index = 0; index < deferred.Count; index++)
        {
            deferred[index]();
        }
    }

    /// <summary>
    /// CV-7/CV-8: the recovery shell call happens on the UI thread, like every other shell call in this
    /// machine and like the design, the CV map and the frequency limiter all assert.
    /// </summary>
    /// <remarks>
    /// The retry is started by a timer, which fires on the thread pool. Running the continuation there
    /// would have put <c>NIM_ADD</c> on a different thread from the one CV-8's cost measurements were
    /// taken on — a document asserting one topology while the code ran another.
    /// </remarks>
    [Fact]
    public void A_scheduled_recovery_attempt_is_marshalled_before_it_touches_the_shell()
    {
        var marshalled = 0;
        var shellCallsOutsideTheMarshaller = 0;
        var insideMarshaller = false;

        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: continuation =>
            {
                Interlocked.Increment(ref marshalled);
                insideMarshaller = true;
                try
                {
                    continuation();
                }
                finally
                {
                    insideMarshaller = false;
                }
            });

        machine.Establish();
        _native.AddResult = false;

        var addsBefore = _native.AddCalls;
        _native.Calls.Clear();
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);

        // The debounce continuation is the first scheduled hop; the retries are the next ones.
        _time.Advance(TrayStateMachine.FirstRetryDelay);

        Assert.True(marshalled > 0, "no scheduled continuation went through the UI marshaller");
        Assert.True(_native.AddCalls > addsBefore, "the recovery never reached the shell");
        Assert.Equal(0, shellCallsOutsideTheMarshaller);
        Assert.False(insideMarshaller);
    }

    /// <summary>
    /// A shell call the machine does not own — the DPI icon update — is serialized against the ones it
    /// does.
    /// </summary>
    /// <remarks>
    /// It replaces and destroys an <c>HICON</c> and issues <c>NIM_MODIFY</c>, and was issued from the
    /// adapter outside this gate, so it could overlap a recovery <c>NIM_ADD</c>: two unsynchronized
    /// callers on one icon, one of them freeing a handle the other may still be using.
    /// </remarks>
    [Fact]
    public void A_foreign_shell_call_waits_for_the_machines_own_shell_call()
    {
        using var machine = Create();
        using var foreignStarted = new ManualResetEventSlim(false);

        _native.AddMayReturn.Reset();
        Run(machine.Establish);
        Assert.True(_native.AddEntered.Wait(Patience), "the Add never started");

        Run(() => machine.InvokeUnderShellGate(() =>
        {
            foreignStarted.Set();
            _native.Calls.Add("Dpi");
        }));

        Assert.False(
            foreignStarted.Wait(TimeSpan.FromMilliseconds(150)),
            "the DPI update ran while a shell call was in flight");

        _native.AddMayReturn.Set();
        WaitForBackground();

        Assert.True(foreignStarted.Wait(Patience), "the DPI update never ran");
        var add = _native.Calls.IndexOf("Add");
        var dpi = _native.Calls.IndexOf("Dpi");
        Assert.True(add >= 0 && dpi > add, $"order was [{string.Join(", ", _native.Calls)}]");
    }

    /// <summary>
    /// CV-19, PROVEN rather than argued: a stale <c>AddCompleted</c> in a non-terminal state is
    /// reconciled, not discarded.
    /// </summary>
    /// <remarks>
    /// The machine cannot reach this state on its own — every generation bump except the episode start
    /// also enters a terminal state, and step 1 of the preamble short-circuits there — so the state is
    /// BUILT. That was the whole objection: a guard whose removal changes nothing is a guard a refactor
    /// deletes with everything still green.
    /// <para>
    /// Entirely single-threaded. Scheduled continuations are queued instead of run, so the episode stays
    /// exactly where it is put: live, non-terminal, on a new generation, with no shell call outstanding.
    /// </para>
    /// </remarks>
    [Fact]
    public void CV19_a_stale_add_completion_in_a_live_episode_is_reconciled_and_compensated()
    {
        var deferred = new List<Action>();

        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: deferred.Add);

        machine.Establish();
        var staleGeneration = machine.GenerationForTests;

        machine.NotifyTaskbarCreated();
        Assert.Equal(TrayLifecycleState.Recovering, machine.LifecycleState);
        Assert.NotEqual(staleGeneration, machine.GenerationForTests);

        var deletesBefore = _native.DeleteCallsSnapshot;

        // An Add from the PREVIOUS generation reports success while the current episode is still live and
        // non-terminal. Discarding it by generation leaves an icon nobody will ever remove.
        machine.InjectForTests(TrayEventKind.AddCompleted, staleGeneration, true);

        Assert.True(
            _native.DeleteCallsSnapshot > deletesBefore,
            $"the stale completion produced no compensating delete; calls were "
                + $"[{string.Join(", ", _native.Calls)}]");
    }

    // ---------------------------------------------------------------------------------------------
    // The lifecycle learns BEFORE the shell I/O runs
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Losing the affordance is published before the compensating <c>NIM_DELETE</c> is issued.
    /// </summary>
    /// <remarks>
    /// The state change is a decision already taken under the lock; the delete is cleanup of an icon that
    /// may or may not still be there. Draining first put the notification behind a synchronous shell call
    /// waiting on the native gate, during an Explorer restart — the one moment shell calls are least
    /// predictable, and the one case where nobody has measured a bound.
    /// <para>
    /// Asserted on the INTERLEAVING rather than by parking a call: the whole episode is driven on this
    /// thread, so parking the delete would park the test. Recording the publication into the same ordered
    /// list the shell writes to gives the sequence directly.
    /// </para>
    /// </remarks>
    [Fact]
    public void Lost_reaches_the_lifecycle_before_the_compensating_delete_reaches_the_shell()
    {
        using var machine = Create();
        machine.Establish();

        machine.StateChanged += (_, _) => _native.Calls.Add($"publish:{machine.State}");

        _native.AddResult = false;
        machine.NotifyTaskbarCreated();

        // Burn the whole episode: debounce, three attempts, both retry delays.
        _time.Advance(TrayStateMachine.DebounceDelay);
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        var published = _native.Calls.IndexOf($"publish:{TrayAffordanceState.Lost}");
        Assert.True(published >= 0, $"Lost was never published: [{string.Join(", ", _native.Calls)}]");

        var deleteAfterLoss = _native.Calls.FindIndex(published, call => call == "Delete");
        Assert.True(
            deleteAfterLoss > published,
            $"the delete must follow the publication: [{string.Join(", ", _native.Calls)}]");
    }

    /// <summary>
    /// The hazard the reordering opens, closed: a subscriber that releases synchronously must not turn a
    /// redundant delete into a fail-safe exit.
    /// </summary>
    /// <remarks>
    /// This is the real path, not a contrived one. On Lost the lifecycle degrades; with no window it asks
    /// for the authoritative exit, whose sequence removes the tray icon, which calls Release — all on the
    /// publishing thread, before the first delete has run. Release then queues a delete for an icon the
    /// first delete is about to remove. A second NIM_DELETE returns false because there is nothing left,
    /// and a false delete is what escalates. The escalation would be manufactured by our own ordering.
    /// </remarks>
    [Fact]
    public void A_subscriber_that_releases_on_Lost_does_not_manufacture_a_fail_safe_exit()
    {
        using var machine = Create();
        machine.Establish();

        machine.StateChanged += (_, _) =>
        {
            if (machine.State == TrayAffordanceState.Lost)
            {
                machine.Release();
            }
        };

        _native.AddResult = false;

        // Driven on THIS thread: nothing parks here, and the point of the test is the synchronous
        // re-entry, which a background task would only make racy.
        machine.NotifyTaskbarCreated();

        _time.Advance(TrayStateMachine.DebounceDelay);
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(0, Volatile.Read(ref _exitRequests));
        Assert.Equal(0, Volatile.Read(ref _escalations));
        Assert.True(machine.CleanupVerified);
    }

    /// <summary>
    /// A delete that reports false for an icon we have already positively removed is redundant, not
    /// failed — but a delete that reports false for an icon that may still exist still escalates.
    /// </summary>
    [Fact]
    public void A_genuinely_failing_delete_still_escalates()
    {
        using var machine = Create();
        machine.Establish();

        _native.DeleteResult = false;
        machine.Release();

        Assert.False(machine.CleanupVerified);
        Assert.Equal(TrayStateMachine.MaxCleanupAttempts, _native.DeleteCalls);
        Assert.True(Volatile.Read(ref _exitRequests) > 0, "an unverifiable cleanup must escalate");
    }

    // ---------------------------------------------------------------------------------------------
    // Establishment and the single producer of Available
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Establish_publishes_Available_only_after_the_shell_confirms_both_calls()
    {
        using var machine = Create();

        machine.Establish();

        Assert.Equal(TrayLifecycleState.Available, machine.LifecycleState);
        Assert.Equal(1, _native.AddCalls);
        Assert.Equal(1, _native.SetVersionCalls);
    }

    [Fact]
    public void A_false_NIM_ADD_never_reaches_Available()
    {
        _native.AddResult = false;
        using var machine = Create();

        machine.Establish();

        Assert.NotEqual(TrayLifecycleState.Available, machine.LifecycleState);
    }

    [Fact]
    public void A_false_NIM_SETVERSION_is_a_contract_failure_and_never_reaches_Available()
    {
        // v4 is a requirement, not a preference: the anchor coordinates position the flyout. We never
        // silently degrade to v3.
        _native.SetVersionResult = false;
        using var machine = Create();

        machine.Establish();

        Assert.NotEqual(TrayLifecycleState.Available, machine.LifecycleState);
    }

    [Fact]
    public void An_exhausted_retry_budget_with_observed_failure_reaches_Lost()
    {
        // Asserts that Lost is REACHED, not that it is the resting state. What happens after it is the
        // open question recorded below, and pinning the resting state here would quietly decide it.
        _native.AddResult = false;
        using var machine = Create();
        var visited = new List<TrayLifecycleState>();
        machine.StateChanged += (_, _) => visited.Add(machine.LifecycleState);

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Contains(TrayLifecycleState.Lost, visited);
        Assert.Equal(TrayStateMachine.MaxAttemptsPerEpisode, _native.AddCalls);
    }

    /// <summary>
    /// <b>RETURNED QUESTION, not an approved behaviour.</b> A registration that never succeeded currently
    /// ends in a fail-safe exit request rather than a degraded foreground session.
    /// </summary>
    /// <remarks>
    /// Found by making <see cref="BlockingNativeTrayRegistration"/> honest: the real
    /// <c>Shell_NotifyIcon(NIM_DELETE)</c> returns FALSE when the shell holds no such icon, and the fake
    /// used to return true unconditionally, so no test could ever see this. The chain is:
    /// <c>NIM_ADD</c> fails three times → Lost → the compensating delete has nothing to delete and
    /// reports false → three cleanup attempts → <c>Unverified</c> → CV-16 escalates to the authoritative
    /// exit.
    /// <para>
    /// It is not obviously wrong. <c>Attempt()</c> marks <c>MayExist</c> BEFORE the call on purpose, and
    /// a false result cannot be attributed: <c>Add() &amp;&amp; SetVersion()</c> also returns false when
    /// the icon WAS registered and only the version call failed, and then the icon really is there and
    /// really must be removed. So the machine cannot tell "nothing to delete" from "delete failed", and
    /// escalating is the fail-closed reading of CV-16.
    /// </para>
    /// <para>
    /// But the product consequence is that a machine where the tray registration fails cannot run the
    /// app at all — the approved design says such a launch degrades to a foreground session with
    /// true-exit semantics, and this exits instead. Two approved rules disagree, and choosing between
    /// them is not mine. This test pins TODAY's behaviour so the decision is visible the moment it
    /// changes; it is not an endorsement.
    /// </para>
    /// </remarks>
    [Fact]
    public void OPEN_QUESTION_a_registration_that_never_succeeded_escalates_instead_of_degrading()
    {
        _native.AddResult = false;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.False(machine.CleanupVerified);
        Assert.Equal(TrayLifecycleState.Releasing, machine.LifecycleState);
        Assert.True(Volatile.Read(ref _exitRequests) > 0);
    }

    // ---------------------------------------------------------------------------------------------
    // T1 / T7 — the deadline, observed from inside a parked native call
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void T1_a_success_that_lands_after_the_deadline_is_discarded_and_never_publishes_Available()
    {
        _native.AddMayReturn.Reset();                 // park the world inside Add
        using var machine = Create();

        Run(machine.Establish);
        Assert.True(_native.AddEntered.Wait(Patience));

        // The call is still outstanding and the native gate is held by that thread. Advancing time on a
        // separate thread lets the deadline be observed while the world is parked.
        Run(() => _time.Advance(TrayStateMachine.RecoveryDeadline + TimeSpan.FromMilliseconds(1)));
        WaitForState(machine, TrayLifecycleState.Lost);

        // Only now does the Add return TRUE — after the deadline.
        _native.AddMayReturn.Set();
        WaitForBackground();

        Assert.NotEqual(TrayLifecycleState.Available, machine.LifecycleState);
        Assert.Equal(TrayAffordanceState.Lost, machine.State);

        // The result is obsolete for the LIFECYCLE but not for the SHELL: it may have created the icon,
        // so a compensating Delete is mandatory and must follow the Add that produced it. Asserting only
        // "some Delete happened" is insufficient — the Lost path emits one of its own, so a mutation
        // that drops the reconciliation would survive.
        // Ordering alone does NOT discriminate here: the Lost path emits a Delete of its own, and the
        // native gate makes it land after the parked Add returns anyway. The reconciliation is a SECOND,
        // separate compensation, so the count is what distinguishes it.
        Assert.True(
            _native.DeleteCallsSnapshot >= 2,
            "the Lost path compensates once, and the late success must be compensated separately; "
                + $"sequence was [{string.Join(",", _native.Calls)}]");
    }

    [Fact]
    public void T11_a_late_successful_Add_is_compensated_and_Released_waits_for_it()
    {
        _native.AddMayReturn.Reset();
        using var machine = Create();

        Run(machine.Establish);
        Assert.True(_native.AddEntered.Wait(Patience));

        Run(machine.Release);
        WaitForState(machine, TrayLifecycleState.Releasing);

        // The Add was emitted BEFORE Releasing, so it may complete physically afterwards — as obsolete
        // and compensated work.
        _native.AddMayReturn.Set();
        WaitForBackground();

        Assert.NotEqual(TrayLifecycleState.Available, machine.LifecycleState);
        Assert.True(_native.DeleteCallsSnapshot >= 1, "a compensating Delete is mandatory");
        Assert.Equal(TrayLifecycleState.Released, machine.LifecycleState);
    }

    // ---------------------------------------------------------------------------------------------
    // T2 / T8 — Release absorption, proved by COUNTING emitted Adds
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void T2_no_new_Add_is_emitted_after_Release()
    {
        using var machine = Create();
        machine.Establish();
        var addsBeforeRelease = _native.AddCalls;

        machine.Release();

        // Everything that could plausibly start work is fired at the terminal state.
        machine.NotifyTaskbarCreated();
        machine.Establish();
        _time.Advance(TrayStateMachine.RecoveryDeadline * 3);

        // Counting ZERO new Adds is the assertion. Observing "the state is Releasing" would pass even
        // with the emission guard mutated away.
        Assert.Equal(addsBeforeRelease, _native.AddCalls);
    }

    [Fact]
    public void T8_a_pending_Add_that_never_started_leaves_nothing_behind()
    {
        _native.AddResult = false;
        using var machine = Create();
        machine.Establish();               // burns attempt 1, schedules a retry
        var addsBeforeRelease = _native.AddCalls;

        machine.Release();
        _time.Advance(TrayStateMachine.SecondRetryDelay * 2);   // the scheduled retry would fire here

        // The claim is that the obsolete retry never runs. The resting state is NOT asserted here: with
        // an Add that never succeeded the release cannot be positively verified, which is the open
        // question pinned above and must not be decided by a side assertion in this test.
        Assert.Equal(addsBeforeRelease, _native.AddCalls);
        Assert.True(
            machine.LifecycleState is TrayLifecycleState.Released or TrayLifecycleState.Releasing,
            $"expected a terminal state, saw {machine.LifecycleState}");
    }

    [Fact]
    public void Release_is_idempotent_and_returns_immediately_when_already_terminal()
    {
        using var machine = Create();
        machine.Establish();

        machine.Release();
        var state = machine.LifecycleState;

        machine.Release();
        machine.Release();

        Assert.Equal(state, machine.LifecycleState);
    }

    // ---------------------------------------------------------------------------------------------
    // T3 / T4 — admission, and the independence of budget B
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void T3_admission_and_invalidation_are_one_step_so_Available_is_never_observed_after_acceptance()
    {
        _native.AddMayReturn.Reset();
        using var machine = Create();

        _native.AddMayReturn.Set();
        machine.Establish();
        Assert.Equal(TrayLifecycleState.Available, machine.LifecycleState);

        // From the instant the broadcast is accepted, Available is gone. There is no schedule in which
        // an observer sees it, because the limiter, the clock and the state change are one body.
        _native.AddMayReturn.Reset();
        _native.AddEntered.Reset();
        Run(machine.NotifyTaskbarCreated);
        Run(() => _time.Advance(TrayStateMachine.DebounceDelay));

        WaitForState(machine, TrayLifecycleState.Recovering);
        Assert.NotEqual(TrayLifecycleState.Available, machine.LifecycleState);

        _native.AddMayReturn.Set();
        WaitForBackground();
    }

    [Fact]
    public void T4_a_suppressed_broadcast_creates_no_episode_no_deadline_and_no_Lost()
    {
        var limiter = new EpisodeFrequencyLimiter(_time, capacity: 1);
        using var machine = Create(limiter: limiter);
        machine.Establish();

        // First broadcast consumes the only admission.
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);
        var addsAfterFirst = _native.AddCalls;

        // Second is suppressed: exactly equivalent to a message that never arrived.
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.RecoveryDeadline * 2);

        Assert.Equal(addsAfterFirst, _native.AddCalls);
        Assert.NotEqual(TrayLifecycleState.Lost, machine.LifecycleState);
    }

    [Fact]
    public void T4_adversarial_all_success_flood_still_converges_on_suppression()
    {
        // THE proof the split required: every NIM_ADD succeeds, so a limiter that could be reset by
        // success would never engage. Counting admissions is what makes it converge.
        using var machine = Create();
        machine.Establish();
        var baseline = _native.AddCalls;

        for (var i = 0; i < 50; i++)
        {
            machine.NotifyTaskbarCreated();
            _time.Advance(TrayStateMachine.DebounceDelay);
        }

        var admitted = _native.AddCalls - baseline;
        Assert.True(
            admitted <= EpisodeFrequencyLimiter.DefaultCapacity,
            $"budget B must converge on suppression; {admitted} episodes were admitted");
    }

    // ---------------------------------------------------------------------------------------------
    // T5 / T13 — cleanup failure escalates rather than degrading
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void T5_an_unverifiable_cleanup_requests_the_authoritative_exit_and_does_not_degrade()
    {
        _native.AddResult = false;
        _native.DeleteResult = false;                 // compensation can never be confirmed
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(ShellEffectState.Unverified, machine.EffectState);
        Assert.False(machine.CleanupVerified);
        Assert.Equal(TrayLifecycleState.Releasing, machine.LifecycleState);
        Assert.Equal(1, Volatile.Read(ref _exitRequests));
        Assert.Equal(TrayStateMachine.MaxCleanupAttempts, _native.DeleteCalls);
    }

    [Fact]
    public void T13_a_sink_that_always_throws_still_terminates_through_the_escalation()
    {
        _native.AddResult = false;
        _native.DeleteResult = false;
        using var machine = Create(requestExit: () =>
        {
            Interlocked.Increment(ref _exitRequests);
            throw new InvalidOperationException("sink is down");
        });

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        // The single shot is not consumed by an exception: three attempts, then escalation.
        Assert.Equal(TrayStateMachine.MaxFailSafeAttempts, Volatile.Read(ref _exitRequests));
        Assert.Equal(1, Volatile.Read(ref _escalations));

        // And the latch is still OPEN. Marking on entry would leave Releasing with the only progress
        // mechanism silently spent, which is the state this design removed for the queued case.
        Assert.False(machine.FailSafeCompleted, "an exception must not consume the single fail-safe shot");
    }

    [Fact]
    public void T12_the_fail_safe_request_runs_on_the_transition_thread_and_never_queues()
    {
        _native.AddResult = false;
        _native.DeleteResult = false;
        var sinkThread = 0;
        using var machine = Create(requestExit: () =>
        {
            sinkThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _exitRequests);
        });

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        var advancingThread = Environment.CurrentManagedThreadId;
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(1, Volatile.Read(ref _exitRequests));
        Assert.Equal(advancingThread, sinkThread);
    }

    [Fact]
    public void A_missing_fail_safe_sink_is_a_construction_error()
    {
        Assert.Throws<ArgumentNullException>(() => new TrayStateMachine(
            _native, null!, () => { }, _time, NullLogger<TrayStateMachine>.Instance));

        Assert.Throws<ArgumentNullException>(() => new TrayStateMachine(
            _native, () => { }, null!, _time, NullLogger<TrayStateMachine>.Instance));
    }

    // ---------------------------------------------------------------------------------------------
    // T6 / T9 — effect ordering, and delivery-time revalidation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void T6_shell_effects_reach_the_shell_in_production_order()
    {
        using var machine = Create();
        machine.Establish();
        machine.Release();

        var add = _native.Calls.IndexOf("Add");
        var delete = _native.Calls.IndexOf("Delete");

        Assert.True(add >= 0 && delete > add, $"expected Add before Delete, got [{string.Join(",", _native.Calls)}]");
    }

    [Fact]
    public void T9_session_semantics_are_not_published_once_the_lifecycle_is_terminal()
    {
        using var machine = Create();
        var states = new List<TrayAffordanceState>();
        machine.StateChanged += (_, _) => states.Add(machine.State);

        machine.Establish();
        Assert.Contains(TrayAffordanceState.Available, states);

        states.Clear();
        machine.Release();

        // Being valid when it was queued is not sufficient: delivery revalidates the lifecycle state.
        Assert.Empty(states);
    }

    // ---------------------------------------------------------------------------------------------
    // Effect shape and the Kind coupling (T16 / T17 / T18)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void T17_every_kind_that_reaches_Add_declares_that_it_may_create_an_affordance()
    {
        // From the ENUM, not from a hard-coded range. The range said "all kinds are covered
        // automatically" and was not: a seventh kind described as Add/false compiled and passed, in the
        // one test that existed to make a new kind impossible to forget.
        var kinds = TrayStateMachine.EffectKindsForTests();
        Assert.NotEmpty(kinds);

        foreach (var kind in kinds)
        {
            var (operation, mayCreate) = TrayStateMachine.DescribeForTests(kind);
            if (operation == NativeTrayOperation.Add)
            {
                Assert.True(mayCreate, $"kind {kind} performs Add but claims it creates no affordance");
            }
        }
    }

    [Fact]
    public void T18_the_effect_switch_has_no_default_arm()
    {
        // An undefined value is legal at runtime. With no default arm the switch throws; add one and it
        // returns instead — which is exactly how this guard detects the mutation. T17 cannot see it,
        // because Enum.GetValues only yields defined values.
        Assert.Throws<SwitchExpressionException>(() => TrayStateMachine.DescribeForTests(int.MaxValue));
    }

    // ---------------------------------------------------------------------------------------------
    // CV-13 / CV-14 — what budget B may and may not do
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CV13_forged_broadcasts_alone_cannot_produce_Lost_by_either_terminal_cause()
    {
        // Only an ADMITTED episode gets a deadline, and only an episode with a deadline can expire. A
        // suppressed message must not start, prolong, restart or keep a deadline alive — otherwise
        // unauthenticated input commands the session transition CV-2 exists to forbid.
        var limiter = new EpisodeFrequencyLimiter(_time, capacity: 1);
        using var machine = Create(limiter: limiter);
        machine.Establish();
        machine.NotifyTaskbarCreated();                       // consumes the only admission
        _time.Advance(TrayStateMachine.DebounceDelay);
        Assert.Equal(TrayLifecycleState.Available, machine.LifecycleState);

        for (var i = 0; i < 20; i++)
        {
            machine.NotifyTaskbarCreated();                   // all suppressed
            _time.Advance(TrayStateMachine.RecoveryDeadline * 2);
        }

        Assert.NotEqual(TrayLifecycleState.Lost, machine.LifecycleState);
        Assert.Equal(TrayLifecycleState.Available, machine.LifecycleState);
    }

    [Fact]
    public void CV14_an_admitted_episode_spends_its_whole_retry_budget_regardless_of_B()
    {
        // B counts episodes STARTED and then stops participating. If it could gate the retries of A it
        // would decide A's outcome by starvation — the coupling CV-2b forbids, entering through the
        // back door.
        var exhausted = new EpisodeFrequencyLimiter(_time, capacity: 1);
        Assert.True(exhausted.TryBeginEpisode(_time.GetTimestamp()));   // B is now exhausted
        Assert.False(exhausted.TryBeginEpisode(_time.GetTimestamp()));

        _native.AddResult = false;
        using var machine = Create(limiter: exhausted);

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(TrayStateMachine.MaxAttemptsPerEpisode, _native.AddCalls);
    }

    [Fact]
    public void CV14_the_frequency_limiter_exposes_exactly_one_entry_point()
    {
        // The cheapest inspection there is, and the one the condition asks to be written down: if the
        // limiter ever grows a second method, the independence has stopped being structural.
        var declared = typeof(EpisodeFrequencyLimiter)
            .GetMethods(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(["TryBeginEpisode"], declared);
    }

    // ---------------------------------------------------------------------------------------------

    private void Run(Action action) => _background.Add(Task.Run(action));

    private void WaitForBackground() => Task.WaitAll([.. _background], Patience);

    /// <summary>
    /// Waits for a state by SIGNAL, not by polling a wall clock.
    /// </summary>
    /// <remarks>
    /// It used to spin on <c>DateTime.UtcNow</c> with a <c>Thread.Sleep(5)</c>, which decides races by
    /// how busy the machine running the tests is. The state machine already raises <c>StateChanged</c>
    /// on every transition, so the signal exists; the timeout stays only as a failure deadline, never as
    /// the thing being measured.
    /// </remarks>
    private static void WaitForState(TrayStateMachine machine, TrayLifecycleState expected)
    {
        using var reached = new ManualResetEventSlim(false);

        void OnChanged(object? sender, EventArgs args)
        {
            if (machine.LifecycleState == expected)
            {
                reached.Set();
            }
        }

        // The LIFECYCLE signal, not the product notification: StateChanged is suppressed for terminal
        // transitions, so waiting on it could never observe Releasing at all.
        machine.LifecycleChangedForTests += OnChanged;
        try
        {
            // Checked after subscribing, so a transition that already happened is not missed.
            if (machine.LifecycleState == expected)
            {
                return;
            }

            Assert.True(
                reached.Wait(Patience),
                $"expected {expected}, observed {machine.LifecycleState}");
        }
        finally
        {
            machine.LifecycleChangedForTests -= OnChanged;
        }
    }

    public void Dispose()
    {
        _native.AddMayReturn.Set();
        _native.DeleteMayReturn.Set();
        try
        {
            Task.WaitAll([.. _background], Patience);
        }
        catch (AggregateException exception)
        {
            _sinkFailure ??= exception;
        }
    }
}
