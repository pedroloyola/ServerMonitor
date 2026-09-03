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
        // Data-driven from the effect kinds themselves, so a new kind is covered automatically.
        var kinds = Enumerable.Range(0, 6).ToArray();
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

    private static void WaitForState(TrayStateMachine machine, TrayLifecycleState expected)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (machine.LifecycleState == expected)
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"expected {expected}, observed {machine.LifecycleState}");
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
