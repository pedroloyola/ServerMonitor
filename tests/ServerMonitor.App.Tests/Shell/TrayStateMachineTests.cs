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
    /// An already-running drainer cannot execute another transition's effect before that transition has
    /// published.
    /// </summary>
    /// <remarks>
    /// This is the neighbour the sequence ordering left open: <c>Add</c> could no longer overtake
    /// <c>Delete</c>, but a drainer that was already inside the loop could still reach the shell on
    /// behalf of a transition that had not yet said what happened.
    /// <para>
    /// Deterministic, with a real barrier and no retries. The probe puts the test EXACTLY at the moment
    /// of publication — after the effect has been queued, before it has been released — and from there a
    /// second thread is made to run a full drain and joined. If a drainer could run the effect then, it
    /// would have; the assertion is on what the shell saw, not on whether a race happened to be caught.
    /// </para>
    /// <para>
    /// The previous version tried the interleaving 200 times and hoped. That is not a proof, it raised
    /// xUnit1031 by blocking on tasks, and under load — several agents on this machine — it is exactly
    /// where the intermittent failures came from.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_active_drainer_cannot_run_an_effect_before_its_transition_publishes()
    {
        using var machine = Create();
        machine.Establish();

        var callsAtPublication = Array.Empty<string>();
        var headRunnableAtPublication = true;
        var drainRan = false;

        machine.BeforeDeliveryForTests = () =>
        {
            if (drainRan)
            {
                return;
            }

            drainRan = true;

            // What a drainer arriving at this instant would find. OBSERVED, never executed: a seam that
            // actually ran a drain from here re-entered the machine while it held its own lock, and the
            // run hung rather than failing.
            headRunnableAtPublication = machine.HeadEffectIsRunnableForTests;
            callsAtPublication = [.. _native.Calls];
        };

        _native.AddResult = false;
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);

        Assert.True(drainRan, "the publication probe never ran");

        // The effect this transition emitted is queued but NOT runnable while the transition is still
        // publishing, so a drainer arriving now — on any thread — executes nothing on its behalf.
        Assert.False(headRunnableAtPublication, "an effect was runnable before its transition published");
        Assert.DoesNotContain("Add", callsAtPublication.Skip(2));
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
            marshalToUi: continuation =>
            {
                deferred.Add(continuation);
                return true;
            });

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

                return true;
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
    /// A drainer arriving mid-publication runs NOTHING — it stops at the unready effect, it does not skip
    /// past it to a later one that happens to be ready.
    /// </summary>
    /// <remarks>
    /// Stopping and skipping are different guarantees and only one of them is safe. Skipping would let a
    /// later effect overtake an earlier one, which is the inversion the sequence exists to prevent; an
    /// assertion that only checks "the head was not runnable" does not tell them apart.
    /// <para>
    /// The queue is built to contain both kinds at once: the publishing transition's own effects, still
    /// unready, followed by a Delete emitted by a nested Release that IS ready. A drain driven from that
    /// exact point must execute neither.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_drainer_stops_at_an_unready_effect_instead_of_skipping_to_a_ready_one()
    {
        using var machine = Create();
        machine.Establish();

        var deletesDuringPublication = -1;
        var probed = false;

        machine.BeforeDeliveryForTests = () =>
        {
            if (probed)
            {
                return;
            }

            probed = true;

            // Nested: emits a compensating Delete and marks it ready, without draining.
            machine.Release();

            // A full drain from here. The head is this publication's own effect, still unready.
            machine.DrainForTests();
            deletesDuringPublication = _native.DeleteCallsSnapshot;
        };

        var deletesBefore = _native.DeleteCallsSnapshot;
        machine.NotifyTaskbarCreated();

        Assert.True(probed, "the publication probe never ran");
        Assert.Equal(deletesBefore, deletesDuringPublication);
    }

    /// <summary>
    /// Between the last check and the invocation, no other thread can change the state.
    /// </summary>
    /// <remarks>
    /// This is the window Atlas measured: the publication used to revalidate and then release the lock,
    /// so a Release could win in the gap and the notification still went out. Moving the check earlier
    /// only moved the gap — the fix is that the check and the invocation are one critical section.
    /// <para>
    /// Asymmetric by construction, not by timing: while the fix holds, the other thread CANNOT make
    /// progress, so no wait is long enough to see it change the state; without the fix it changes it
    /// immediately. The wait exists to bound the test, not to decide the race.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_other_thread_can_change_the_state_between_the_last_check_and_the_invocation()
    {
        using var machine = Create();
        using var releaseFinished = new ManualResetEventSlim(false);
        var stateAtInvocation = TrayLifecycleState.Unavailable;
        var probed = false;

        machine.AtInvocationForTests = () =>
        {
            if (probed)
            {
                return;
            }

            probed = true;

            var releaser = new Thread(() =>
            {
                machine.Release();
                releaseFinished.Set();
            });

            releaser.Start();

            // POSITIVE observation of the block, and NO WALL CLOCK anywhere. Signalling before attempting
            // the lock proves only that the thread started; deciding by an elapsed interval decides by the
            // scheduler; and spinning on DateTime.UtcNow is the same wall clock wearing a loop. A thread
            // blocked on a monitor reports WaitSleepJoin, and SpinWait bounds the wait by ITERATIONS
            // rather than by time — a count is a fact about this run, not about how busy the machine is.
            var spin = new SpinWait();
            var observations = 0;
            while (observations++ < 100_000
                   && releaser.IsAlive
                   && (releaser.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            {
                spin.SpinOnce();
            }

            Assert.True(
                (releaser.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                $"the release did not block on the delivery's lock; thread was {releaser.ThreadState}");

            stateAtInvocation = machine.LifecycleState;
        };

        machine.Establish();

        Assert.True(probed, "the invocation probe never ran");
        Assert.NotEqual(TrayLifecycleState.Releasing, stateAtInvocation);
        Assert.NotEqual(TrayLifecycleState.Released, stateAtInvocation);

        // The release is allowed to finish afterwards; the point is only that it could not land INSIDE
        // the delivery.
        Assert.True(releaseFinished.Wait(Patience), "the release never completed");
    }

    /// <summary>
    /// O1. The clock is part of what the affordance IS, so no reader can be told it is usable while an
    /// episode is overdue — whichever path they came in by.
    /// </summary>
    /// <remarks>
    /// The deadline used to be a gate at the delivery site, and there was always another way past it: the
    /// clock kept moving between the check and the invocation, and no other reader went through the check
    /// at all. It is now evaluated inside the projection, which is the one function every reader and the
    /// publication both go through.
    /// </remarks>
    [Fact]
    public void An_overdue_episode_reads_as_Lost_from_every_path_without_any_event_being_delivered()
    {
        var deferred = new List<Action>();

        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: continuation =>
            {
                deferred.Add(continuation);
                return true;
            });

        machine.Establish();
        machine.NotifyTaskbarCreated();
        Assert.Equal(TrayAffordanceState.Recovering, machine.State);

        // Nothing is delivered: the continuations are queued and never run. Only time passes.
        _time.Advance(TrayStateMachine.RecoveryDeadline);

        Assert.Equal(TrayAffordanceState.Lost, machine.State);
        Assert.Equal(TrayLifecycleState.Recovering, machine.LifecycleState);
    }

    /// <summary>
    /// The commit PERFORMS the act; it does not answer a question and leave the caller to act.
    /// </summary>
    /// <remarks>
    /// The whole correction is that there is no interval between the determination and the act. A commit
    /// that returned <c>true</c> and left the caller to do the work would be the old detachable boolean
    /// wearing a new signature — and my first tests for this exercised a FAKE source, so a mutation of
    /// the real one went unnoticed.
    /// </remarks>
    [Fact]
    public void The_commit_performs_the_act_while_the_affordance_holds()
    {
        using var machine = Create();
        machine.Establish();

        var ran = 0;
        machine.EnterBackground(() => ran++);

        // Nothing comes back: the only evidence is that the act ran, which is a record of what happened
        // and not a permission to do it later.
        Assert.Equal(1, ran);
    }

    /// <summary>Without an established affordance the act does not run at all.</summary>
    [Fact]
    public void The_commit_refuses_and_runs_nothing_when_there_is_no_affordance()
    {
        _native.AddResult = false;
        using var machine = Create();
        machine.Establish();

        var ran = 0;
        machine.EnterBackground(() => ran++);

        Assert.Equal(0, ran);
    }

    /// <summary>
    /// A multicast delivery is re-decided in front of EACH subscriber: the state can die between the
    /// first and the second.
    /// </summary>
    /// <remarks>
    /// Validating once and then serving N handlers means the second one is served on the strength of a
    /// judgement made before the first one ran. Here the first handler pushes the clock past the deadline;
    /// the second must not be told the affordance is usable.
    /// </remarks>
    [Fact]
    public void A_multicast_delivery_is_revalidated_in_front_of_every_subscriber()
    {
        var deferred = new List<Action>();
        var secondSaw = new List<TrayAffordanceState>();

        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: continuation =>
            {
                deferred.Add(continuation);
                return true;
            });

        machine.Establish();
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);

        var pushed = false;

        // FIRST subscriber: moves the clock past the bound while the multicast is in progress.
        machine.StateChanged += (_, _) =>
        {
            if (pushed)
            {
                return;
            }

            pushed = true;
            _time.Advance(TrayStateMachine.RecoveryDeadline);
        };

        // SECOND subscriber: must not be served on a judgement made before the first one ran.
        machine.StateChanged += (_, _) => secondSaw.Add(machine.State);

        for (var index = 0; index < deferred.Count; index++)
        {
            deferred[index]();
        }

        Assert.True(pushed, "the first subscriber never ran");
        Assert.DoesNotContain(TrayAffordanceState.Available, secondSaw);
    }

    /// <summary>
    /// A subscriber that releases stops the multicast for the ones after it.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule, and it needed its own test: it is not only the clock that can
    /// invalidate a delivery half way through. Release dominates, and a handler that has not run yet must
    /// not be told about an affordance the process has already given up.
    /// </remarks>
    [Fact]
    public void A_subscriber_that_releases_stops_the_multicast_for_the_ones_after_it()
    {
        using var machine = Create();
        var secondRan = 0;
        var released = false;

        machine.StateChanged += (_, _) =>
        {
            if (released)
            {
                return;
            }

            released = true;
            machine.Release();
        };

        machine.StateChanged += (_, _) => secondRan++;

        machine.Establish();

        Assert.True(released, "the first subscriber never ran");
        Assert.Equal(0, secondRan);
    }

    /// <summary>
    /// A loss nobody acknowledged ends the process. Isolating the subscriber protects the QUEUE; it must
    /// not also swallow the failure of the consumer that was supposed to act on the loss.
    /// </summary>
    /// <remarks>
    /// The consumer of a loss is not one subscriber among several: it is what degrades the session or
    /// ends the process. If it threw, nothing materialised a window and nothing asked for an exit, while
    /// the machine sat in Lost — alive, with no affordance. That is the same situation as an unverifiable
    /// cleanup and it takes the same answer.
    /// </remarks>
    [Fact]
    public void A_loss_that_no_subscriber_acknowledged_requests_the_authoritative_exit()
    {
        using var machine = Create();
        machine.Establish();

        machine.StateChanged += (_, _) =>
        {
            if (machine.State is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)
            {
                throw new InvalidOperationException("the degradation path is broken");
            }
        };

        _native.AddResult = false;
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.True(
            Volatile.Read(ref _exitRequests) > 0,
            "a loss that nobody acted on must not leave the process alive without an affordance");
    }

    /// <summary>
    /// A subscriber failing on a NON-degrading notification is still just a subscriber: it is isolated,
    /// and it does not end the process.
    /// </summary>
    /// <remarks>
    /// The pair matters. Escalating on every subscriber exception would make any faulty observer able to
    /// quit the app, which trades one defect for a louder one.
    /// </remarks>
    [Fact]
    public void A_subscriber_failing_on_a_non_degrading_notification_does_not_end_the_process()
    {
        using var machine = Create();

        machine.StateChanged += (_, _) => throw new InvalidOperationException("noisy observer");

        machine.Establish();

        Assert.Equal(TrayAffordanceState.Available, machine.State);
        Assert.Equal(0, Volatile.Read(ref _exitRequests));
    }

    /// <summary>
    /// O3, the OTHER layer: the release survives a failure that the subscriber guard does not catch.
    /// </summary>
    /// <remarks>
    /// The obligation is defended twice — the machine isolates subscriber exceptions, and the release
    /// sits in a <c>finally</c> — and a property defended twice has to be proven twice, or a mutation
    /// removing either layer stays green because the other still holds. This drives a failure from a
    /// point the isolation does not cover, so only the <c>finally</c> can save the compensation.
    /// </remarks>
    [Fact]
    public void The_effect_release_survives_a_failure_the_subscriber_guard_does_not_catch()
    {
        using var machine = Create();
        machine.Establish();

        var deletesBefore = _native.DeleteCallsSnapshot;
        machine.AtInvocationForTests = () => throw new InvalidOperationException("delivery exploded");

        _native.AddResult = false;
        try
        {
            machine.NotifyTaskbarCreated();
            _time.Advance(TrayStateMachine.DebounceDelay);
            _time.Advance(TrayStateMachine.FirstRetryDelay);
            _time.Advance(TrayStateMachine.SecondRetryDelay);
        }
        catch (InvalidOperationException)
        {
            // The failure is deliberate and is allowed to reach the caller; what it may NOT do is leave
            // the machine holding an icon it can never remove.
        }

        machine.AtInvocationForTests = null;

        // Nudge the machine so any queued work can drain, then check the obligation was discharged.
        machine.Release();

        Assert.True(
            _native.DeleteCallsSnapshot > deletesBefore,
            "a failure during delivery must not strand the compensating delete");
    }

    /// <summary>
    /// O1, at the event: the deadline is re-read immediately before the notification goes out, with
    /// nothing in between that could move the clock.
    /// </summary>
    /// <remarks>
    /// The probe stands exactly where the gap used to be. Checking earlier — even a few statements
    /// earlier — leaves the clock free to move in between, which is what was measured; the assertion
    /// therefore has to drive the clock from that precise point.
    /// </remarks>
    [Fact]
    public void The_deadline_is_re_read_at_the_invocation_and_not_before_it()
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
            marshalToUi: continuation =>
            {
                deferred.Add(continuation);
                return true;
            });

        machine.Establish();
        machine.StateChanged += (_, _) => delivered.Add(machine.State);
        machine.NotifyTaskbarCreated();

        _time.Advance(TrayStateMachine.DebounceDelay);

        var pushed = false;
        machine.AtInvocationForTests = () =>
        {
            if (pushed)
            {
                return;
            }

            pushed = true;
            _time.Advance(TrayStateMachine.RecoveryDeadline);
        };

        for (var index = 0; index < deferred.Count; index++)
        {
            deferred[index]();
        }

        Assert.True(pushed, "the invocation probe never ran");
        Assert.DoesNotContain(TrayAffordanceState.Available, delivered);
    }

    /// <summary>
    /// O2. A continuation the UI thread refuses TERMINALIZES the episode; it is not abandoned.
    /// </summary>
    /// <remarks>
    /// The first fix stopped running refused work inline, which removed a topology defect and introduced
    /// a worse one: the machine stayed alive in Recovering, with no affordance, degrading nothing and
    /// terminalizing nothing. Dropping the work is not neutral — an admitted episode has to end.
    /// </remarks>
    [Fact]
    public void A_refused_continuation_terminalizes_the_episode_instead_of_abandoning_it()
    {
        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: _ => false);

        machine.Establish();
        machine.NotifyTaskbarCreated();

        // The debounce timer fires and the dispatcher refuses it.
        _time.Advance(TrayStateMachine.DebounceDelay);

        Assert.Equal(TrayAffordanceState.Lost, machine.State);
        Assert.NotEqual(TrayLifecycleState.Recovering, machine.LifecycleState);
    }

    /// <summary>
    /// O3. A subscriber that throws cannot strand the mandatory compensation.
    /// </summary>
    /// <remarks>
    /// Releasing the effects was a statement after the publication, so external code between the two
    /// could prevent it for ever — and the effect it stranded was the Delete that removes our own icon.
    /// An obligation discharged only on the success path is not discharged.
    /// </remarks>
    [Fact]
    public void A_subscriber_that_throws_cannot_block_the_compensating_delete()
    {
        using var machine = Create();
        machine.Establish();

        machine.StateChanged += (_, _) => throw new InvalidOperationException("subscriber is hostile");

        var deletesBefore = _native.DeleteCallsSnapshot;

        _native.AddResult = false;
        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        // The icon from Establish exists, so losing the affordance MUST compensate for it.
        Assert.True(
            _native.DeleteCallsSnapshot > deletesBefore,
            "a hostile subscriber must not be able to prevent the removal of our own icon");
    }

    /// <summary>
    /// A continuation the UI thread will not take is DROPPED, never run on the timer's thread.
    /// </summary>
    /// <remarks>
    /// The first version fell back to running inline, which cancelled the guarantee the main path
    /// establishes: the topology held only when the dispatcher was healthy, and a continuation on the
    /// timer thread is exactly the second drainer the ordering work exists to exclude.
    /// </remarks>
    [Fact]
    public void A_refused_continuation_never_runs_off_the_UI_thread()
    {
        var refused = 0;

        using var machine = new TrayStateMachine(
            _native,
            () => Interlocked.Increment(ref _exitRequests),
            () => Interlocked.Increment(ref _escalations),
            _time,
            NullLogger<TrayStateMachine>.Instance,
            limiter: null,
            marshalToUi: _ =>
            {
                Interlocked.Increment(ref refused);
                return false;
            });

        machine.Establish();
        var addsBefore = _native.AddCallsSnapshot;

        machine.NotifyTaskbarCreated();
        _time.Advance(TrayStateMachine.DebounceDelay);
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);
        _time.Advance(TrayStateMachine.RecoveryDeadline);

        Assert.True(refused > 0, "no continuation was offered to the UI thread");
        Assert.Equal(addsBefore, _native.AddCallsSnapshot);
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

        using var foreignQueued = new ManualResetEventSlim(false);
        var foreign = new Thread(() =>
        {
            foreignQueued.Set();
            machine.InvokeUnderShellGate(() =>
            {
                foreignStarted.Set();
                _native.Calls.Add("Dpi");
            });
        });

        foreign.Start();
        Assert.True(foreignQueued.Wait(Patience), "the DPI update was never requested");

        // POSITIVE observation of exclusion, not silence: wait until the thread is actually BLOCKED.
        // A thread waiting on a monitor reports WaitSleepJoin; one that sailed through would be Stopped.
        // Asserting "nothing happened for 150 ms" asserted something about the scheduler.
        // Bounded by ITERATIONS, not by the wall clock — the same bar the delivery test had to meet.
        var spin = new SpinWait();
        var observations = 0;
        while (observations++ < 100_000
               && foreign.IsAlive
               && (foreign.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
        {
            spin.SpinOnce();
        }

        Assert.True(
            (foreign.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            $"the DPI update did not block on the gate; thread was {foreign.ThreadState}");
        Assert.False(foreignStarted.IsSet, "the DPI update ran while a shell call was in flight");

        _native.AddMayReturn.Set();
        WaitForBackground();
        foreign.Join(Patience);

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
            marshalToUi: continuation =>
            {
                deferred.Add(continuation);
                return true;
            });

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
    /// QUESTION D, CASE 1. An initial registration that NEVER succeeded degrades; it does not end the
    /// application.
    /// </summary>
    /// <remarks>
    /// Every <c>NIM_ADD</c> was refused and nothing is in flight that could still create an icon, so
    /// there is nothing to remove and there never was: the disposition is <c>NotRequired</c>, which is
    /// not a cleanup failure. Reading a <c>NIM_DELETE</c> that has nothing to delete as a failure is what
    /// used to turn this into a fail-safe exit and cost the user the app on a machine where the shell
    /// simply would not take the icon.
    /// </remarks>
    [Fact]
    public void QD1_an_initial_registration_that_never_succeeded_degrades_instead_of_exiting()
    {
        _native.AddResult = false;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(CleanupDisposition.NotRequired, machine.Cleanup);
        Assert.Equal(ShellEffectState.NeverCreated, machine.EffectState);
        Assert.Equal(TrayAffordanceState.Lost, machine.State);

        // The degraded session is the outcome: no fail-safe exit, and no escalation.
        Assert.Equal(0, Volatile.Read(ref _exitRequests));
        Assert.Equal(0, Volatile.Read(ref _escalations));
    }

    /// <summary>
    /// QUESTION D, CASE 1, second half: no pointless Delete is issued at all.
    /// </summary>
    /// <remarks>
    /// The decision is explicit that removal retries should not run when cleanup is provably
    /// <c>NotRequired</c>. Asserting the disposition alone would not catch a version that issued three
    /// futile <c>NIM_DELETE</c> calls and then classified the result correctly.
    /// </remarks>
    [Fact]
    public void QD1_no_delete_is_issued_when_nothing_was_ever_created()
    {
        _native.AddResult = false;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(0, _native.DeleteCallsSnapshot);
    }

    /// <summary>
    /// QUESTION D, CASE 2. <c>NIM_ADD</c> succeeded and <c>NIM_SETVERSION</c> did not: the icon may
    /// exist, so removal is REQUIRED. This is not equivalent to a failed add.
    /// </summary>
    [Fact]
    public void QD2_an_add_that_succeeded_before_a_later_failure_requires_cleanup()
    {
        _native.AddResult = true;
        _native.SetVersionResult = false;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        // Removal was required and, with a Delete that works, it is confirmed.
        Assert.Equal(CleanupDisposition.Verified, machine.Cleanup);
        Assert.True(_native.DeleteCallsSnapshot > 0, "a possible icon must be removed");
        Assert.NotEqual(TrayAffordanceState.Available, machine.State);
    }

    /// <summary>
    /// QUESTION D, CASE 2 continued: required cleanup plus a confirmed <c>NIM_DELETE</c> is
    /// <c>Verified</c>, and the degraded session is allowed to continue.
    /// </summary>
    [Fact]
    public void QD3_a_required_cleanup_that_is_confirmed_allows_the_degraded_session()
    {
        _native.AddResult = true;
        _native.SetVersionResult = false;
        _native.DeleteResult = true;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(CleanupDisposition.Verified, machine.Cleanup);
        Assert.Equal(0, Volatile.Read(ref _exitRequests));
    }

    /// <summary>
    /// QUESTION D, CASE 2 continued: required cleanup whose removal cannot be confirmed within its budget
    /// is <c>Unverified</c>, and CV-16 still applies.
    /// </summary>
    [Fact]
    public void QD4_a_required_cleanup_that_cannot_be_confirmed_escalates()
    {
        _native.AddResult = true;
        _native.SetVersionResult = false;
        _native.DeleteResult = false;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);

        Assert.Equal(CleanupDisposition.Unverified, machine.Cleanup);
        Assert.Equal(TrayStateMachine.MaxCleanupAttempts, _native.DeleteCallsSnapshot);
        Assert.True(Volatile.Read(ref _exitRequests) > 0, "CV-16 still escalates when removal was required");
    }

    /// <summary>
    /// QUESTION D, CASE 3. An add still in flight keeps cleanup REQUIRED: a Release before it concludes
    /// must not classify the machine as having created nothing.
    /// </summary>
    [Fact]
    public void QD5_an_add_in_flight_keeps_cleanup_required_and_is_still_compensated()
    {
        _native.AddMayReturn.Reset();
        using var machine = Create();

        Run(machine.Establish);
        Assert.True(_native.AddEntered.Wait(Patience), "the Add never started");

        Run(machine.Release);
        WaitForState(machine, TrayLifecycleState.Releasing);

        // While the Add is outstanding the machine cannot claim nothing was created.
        Assert.NotEqual(CleanupDisposition.NotRequired, machine.Cleanup);

        _native.AddMayReturn.Set();
        WaitForBackground();

        // The late success is obsolete for the lifecycle and compensated for the shell.
        Assert.NotEqual(TrayAffordanceState.Available, machine.State);
        Assert.True(_native.DeleteCallsSnapshot > 0, "a late successful Add must still be compensated");
    }

    /// <summary>
    /// QUESTION D, case 6. A <c>NIM_DELETE</c> that runs by accident when nothing was ever created cannot
    /// turn <c>NotRequired</c> into <c>Unverified</c>.
    /// </summary>
    /// <remarks>
    /// This is the bypass in the other direction, and it is the one Vigil has to be able to rule out: if
    /// a stray failed delete could downgrade the disposition, <c>NotRequired</c> would become a way of
    /// reaching an escalation rather than a way of avoiding one.
    /// </remarks>
    [Fact]
    public void QD6_a_stray_failed_delete_cannot_turn_NotRequired_into_Unverified()
    {
        _native.AddResult = false;
        using var machine = Create();

        machine.Establish();
        _time.Advance(TrayStateMachine.FirstRetryDelay);
        _time.Advance(TrayStateMachine.SecondRetryDelay);
        Assert.Equal(CleanupDisposition.NotRequired, machine.Cleanup);

        // A cleanup completion arrives anyway, reporting failure, for the current generation.
        machine.InjectForTests(TrayEventKind.CleanupCompleted, machine.GenerationForTests, false);

        Assert.Equal(CleanupDisposition.NotRequired, machine.Cleanup);
        Assert.Equal(0, Volatile.Read(ref _exitRequests));
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
        // CASE 2: NIM_ADD succeeded and NIM_SETVERSION did not, so the icon MAY EXIST and removal is
        // required. An add that is refused outright no longer reaches here — that is CASE 1, and it
        // degrades rather than escalating.
        _native.AddResult = true;
        _native.SetVersionResult = false;
        _native.DeleteResult = false;                 // and the removal can never be confirmed
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
        // CASE 2: NIM_ADD succeeded and NIM_SETVERSION did not, so the icon MAY EXIST and removal is
        // required. An add that is refused outright no longer reaches here — that is CASE 1, and it
        // degrades rather than escalating.
        _native.AddResult = true;
        _native.SetVersionResult = false;
        _native.DeleteResult = false;                 // and the removal can never be confirmed
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
        // CASE 2: NIM_ADD succeeded and NIM_SETVERSION did not, so the icon MAY EXIST and removal is
        // required. An add that is refused outright no longer reaches here — that is CASE 1, and it
        // degrades rather than escalating.
        _native.AddResult = true;
        _native.SetVersionResult = false;
        _native.DeleteResult = false;                 // and the removal can never be confirmed
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
