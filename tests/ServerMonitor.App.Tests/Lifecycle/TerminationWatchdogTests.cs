using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// The PRODUCTION <see cref="TerminationWatchdog"/> under test — never a double (BOSS.md §10).
/// <para>
/// The previous round proved these properties against a <c>FakeTerminationWatchdog</c>, and a mutant that
/// made the real watchdog expire immediately passed the whole suite. Only the waiting is replaced here,
/// by <see cref="ManualWatchdogScheduler"/>; the arming, the one-shot guard and the callback are the real
/// class. The corresponding mutation evidence is in the review report.
/// </para>
/// </summary>
public sealed class TerminationWatchdogTests
{
    /// <summary>
    /// How long a handshake may take before the test FAILS. It never decides an ordering: every wait below
    /// is on an event the other side sets, and running out of patience is reported as a failure.
    /// </summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private static TerminationWatchdog Create(ManualWatchdogScheduler scheduler) =>
        new(scheduler, NullLogger<TerminationWatchdog>.Instance);

    // ---------------------------------------------------------------- the REAL scheduler's thread

    /// <summary>
    /// <b>IsBackground is part of the correctness contract, not an implementation detail</b> (tray
    /// corrections §10). A foreground watchdog thread keeps the process alive after the UI and the host
    /// are gone, turning the termination guarantee into the source of another zombie — and the mutation
    /// IsBackground=false previously passed all 34 tests, so the guarantee was unprotected.
    /// <para>
    /// This exercises the PRODUCTION thread-creation path: the real
    /// <see cref="DedicatedThreadWatchdogScheduler"/> schedules a real callback, and the callback reports
    /// the properties of the thread it actually ran on.
    /// </para>
    /// </summary>
    [Fact]
    public void The_real_scheduler_runs_on_a_background_thread_that_is_not_the_thread_pool()
    {
        var scheduler = new DedicatedThreadWatchdogScheduler();
        using var fired = new ManualResetEventSlim(false);
        var isBackground = false;
        var isThreadPool = true;
        var threadName = string.Empty;

        scheduler.ScheduleOnce(TimeSpan.FromMilliseconds(1), () =>
        {
            isBackground = Thread.CurrentThread.IsBackground;
            isThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            threadName = Thread.CurrentThread.Name ?? string.Empty;
            fired.Set();
        });

        Assert.True(fired.Wait(TimeSpan.FromSeconds(30)), "the watchdog callback never ran");
        Assert.True(
            isBackground,
            "the watchdog thread must be a background thread: a foreground one keeps the process alive "
            + "after the UI and the host are gone, which is the zombie this class exists to prevent");
        Assert.False(isThreadPool, "the watchdog must not run on the pool a wedged shutdown can starve");
        Assert.Contains("watchdog", threadName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same property through the whole production path — watchdog + real scheduler — so the contract
    /// holds for how the app actually arms it, not only for a directly constructed scheduler.
    /// </summary>
    [Fact]
    public void A_production_watchdog_arms_onto_a_background_thread()
    {
        var watchdog = new TerminationWatchdog(
            new DedicatedThreadWatchdogScheduler(), NullLogger<TerminationWatchdog>.Instance);
        using var fired = new ManualResetEventSlim(false);
        var isBackground = false;

        watchdog.Arm(TimeSpan.FromMilliseconds(1), () =>
        {
            isBackground = Thread.CurrentThread.IsBackground;
            fired.Set();
        });

        Assert.True(fired.Wait(TimeSpan.FromSeconds(30)), "the armed watchdog never fired");
        Assert.True(isBackground);
    }

    [Fact]
    public void A_new_watchdog_is_not_armed_and_schedules_nothing()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);

        Assert.False(watchdog.IsArmed);
        Assert.Equal(0, scheduler.ScheduleCount);
    }

    [Fact]
    public void Arming_schedules_exactly_the_requested_deadline_once()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);

        watchdog.Arm(TimeSpan.FromSeconds(10), () => { });

        Assert.True(watchdog.IsArmed);
        Assert.Equal([TimeSpan.FromSeconds(10)], scheduler.ScheduledDelays);
    }

    /// <summary>
    /// Monotonic and non-restartable: later arms are ignored entirely, so nothing can push the deadline
    /// out or add a second escalation.
    /// </summary>
    [Fact]
    public void The_deadline_cannot_be_restarted_or_extended()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        var terminations = 0;

        watchdog.Arm(TimeSpan.FromSeconds(10), () => terminations++);
        watchdog.Arm(TimeSpan.FromSeconds(60), () => terminations++);
        watchdog.Arm(TimeSpan.FromMilliseconds(1), () => terminations++);

        Assert.Equal([TimeSpan.FromSeconds(10)], scheduler.ScheduledDelays);

        scheduler.ElapseAll();
        Assert.Equal(1, terminations);
    }

    [Fact]
    public void The_terminal_action_runs_exactly_once_when_the_deadline_passes()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        var terminations = 0;

        watchdog.Arm(TimeSpan.FromSeconds(10), () => terminations++);
        Assert.Equal(0, terminations); // nothing before the deadline

        scheduler.ElapseAll();

        Assert.Equal(1, terminations);
    }

    /// <summary>A terminal action that throws must not escape the watchdog thread.</summary>
    [Fact]
    public void A_failing_terminal_action_is_contained()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        watchdog.Arm(TimeSpan.FromSeconds(10), () => throw new InvalidOperationException("boom"));

        Assert.Null(Record.Exception(scheduler.ElapseAll));
    }

    [Fact]
    public void Arming_rejects_a_non_positive_deadline_and_a_missing_action()
    {
        var watchdog = Create(new ManualWatchdogScheduler());

        Assert.Throws<ArgumentOutOfRangeException>(() => watchdog.Arm(TimeSpan.Zero, () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => watchdog.Arm(TimeSpan.FromSeconds(-1), () => { }));
        Assert.Throws<ArgumentNullException>(() => watchdog.Arm(TimeSpan.FromSeconds(10), null!));
    }

    // ---------------------------------------------------------------- ownership: THE returned defect

    /// <summary>
    /// The blocking defect: the watchdog was a container-created singleton in the very host the exit
    /// stops and disposes, so <c>host.Dispose()</c> disposed it and a failed <c>Exit()</c> left no
    /// escalation. It must not be disposable at all — a container cannot dispose what is not
    /// <see cref="IDisposable"/>.
    /// </summary>
    [Fact]
    public void The_watchdog_is_not_disposable_so_no_container_can_end_it()
    {
        Assert.False(typeof(TerminationWatchdog).IsAssignableTo(typeof(IDisposable)));
        Assert.False(typeof(TerminationWatchdog).IsAssignableTo(typeof(IAsyncDisposable)));
        Assert.False(typeof(ITerminationWatchdog).IsAssignableTo(typeof(IDisposable)));

        // And there is no way to cancel it: nothing but process death makes it inert.
        Assert.DoesNotContain(
            typeof(ITerminationWatchdog).GetMethods(),
            method => method.Name is "Disarm" or "Cancel" or "Stop");
    }

    /// <summary>
    /// Composition, end to end: a watchdog owned by the process and merely REGISTERED in a container
    /// survives that container being disposed — which is exactly what happens to the host during a true
    /// exit — and still terminates when its deadline passes.
    /// </summary>
    [Fact]
    public void Disposing_the_container_neither_owns_nor_ends_the_watchdog()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        var terminations = 0;

        var services = new ServiceCollection();
        services.AddSingleton<ITerminationWatchdog>(watchdog); // the production registration shape
        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ITerminationWatchdog>();
        resolved.Arm(TimeSpan.FromSeconds(10), () => terminations++);

        provider.Dispose(); // host.Dispose() during the exit

        scheduler.ElapseAll();

        Assert.Same(watchdog, resolved);
        Assert.Equal(1, terminations);
    }

    // ------------------------------------------------------------------ CV-21 A: arming fails closed

    /// <summary>
    /// A scheduler that cannot establish the wait leaves NOTHING behind it. The flag used to be set before
    /// the schedule, so this exact case produced an object reporting <c>IsArmed == true</c> with no
    /// escalation at all — a guarantee that existed only as a boolean.
    /// </summary>
    [Fact]
    public void Arming_that_cannot_be_established_never_reports_armed()
    {
        var watchdog = new TerminationWatchdog(new ThrowingWatchdogScheduler(), NullLogger<TerminationWatchdog>.Instance);

        var failure = Assert.Throws<TerminationWatchdogArmingException>(
            () => watchdog.Arm(TimeSpan.FromSeconds(10), () => { }));

        Assert.False(watchdog.IsArmed);
        Assert.Equal(TimeSpan.FromSeconds(10), failure.Deadline);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    /// <summary>
    /// Fail CLOSED, not fail DEAD. A failed attempt must not consume the one-shot: the process would then
    /// be permanently unable to arm the last resort because of a transient scheduler failure.
    /// <para>
    /// The failure and the retry hit the SAME instance. Two separate instances (the previous shape) prove
    /// nothing about this one-shot, so a watchdog that burned its flag on the failed attempt passed.
    /// </para>
    /// </summary>
    [Fact]
    public void A_failed_arming_does_not_consume_the_one_shot()
    {
        var scheduler = new ManualWatchdogScheduler { FailNextAttempts = 1 };
        var watchdog = Create(scheduler);
        var terminations = 0;

        Assert.Throws<TerminationWatchdogArmingException>(
            () => watchdog.Arm(TimeSpan.FromSeconds(10), () => terminations++));
        Assert.False(watchdog.IsArmed);
        Assert.Equal(0, scheduler.ScheduleCount);

        watchdog.Arm(TimeSpan.FromSeconds(10), () => terminations++);

        Assert.True(watchdog.IsArmed);
        Assert.Equal(2, scheduler.Attempts); // the retry reached the scheduler: nothing was consumed
        Assert.Equal([TimeSpan.FromSeconds(10)], scheduler.ScheduledDelays);

        scheduler.ElapseAll();
        Assert.Equal(1, terminations);
    }

    /// <summary>
    /// <b>The ORDER itself, not just the rollback.</b> Every other test here observes the state only after
    /// <c>Arm</c> returned, so publishing armed BEFORE the scheduling while KEEPING the rollback in the
    /// catch passed all of them: the rollback hid the early write from anyone who looked afterwards. This
    /// holds the scheduling open mid-flight and has a concurrent reader look INSIDE that interval, which
    /// is the only place the ordering is visible.
    /// <para>
    /// The reader takes the same lock-free path a real consumer takes, so an early write is observable
    /// exactly as it would be in production. Both waits are handshakes; <see cref="Failsafe"/> only fails.
    /// </para>
    /// </summary>
    [Fact]
    public void IsArmed_is_false_while_the_scheduling_is_still_in_flight()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        using var schedulingStarted = new ManualResetEventSlim(false);
        using var observationTaken = new ManualResetEventSlim(false);
        var armedMidFlight = true;
        var observerRan = false;
        var released = false;

        var observer = new Thread(() =>
        {
            observerRan = schedulingStarted.Wait(Failsafe);
            armedMidFlight = watchdog.IsArmed; // no gate: exactly what a concurrent consumer would see
            observationTaken.Set();
        }) { IsBackground = true };
        observer.Start();

        // Held open in the middle of the scheduling, so the observation happens after the scheduler was
        // entered and before Arm could possibly have finished.
        scheduler.OnScheduleAttempt = () =>
        {
            schedulingStarted.Set();
            released = observationTaken.Wait(Failsafe);
        };

        watchdog.Arm(TimeSpan.FromSeconds(10), () => { });

        Assert.True(observer.Join(Failsafe), "the observer never finished");
        Assert.True(observerRan, "the observer never saw the scheduling start");
        Assert.True(released, "the arming was never released");
        Assert.False(
            armedMidFlight,
            "the watchdog published Armed while its scheduling was still in flight: the flag must be "
            + "written AFTER the escalation exists, and a rollback in the catch does not make an early "
            + "write acceptable");
        Assert.True(watchdog.IsArmed); // and it IS armed once the scheduling has actually happened
    }

    /// <summary>
    /// The other half of the one-shot: once a deadline IS established the watchdog is closed for good, so
    /// a later call never even reaches the scheduler - it cannot extend the deadline, add a second
    /// escalation, or fail.
    /// </summary>
    [Fact]
    public void An_established_deadline_stops_every_later_call_before_the_scheduler()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        watchdog.Arm(TimeSpan.FromSeconds(10), () => { });

        scheduler.FailNextAttempts = 1; // would throw if the call got through
        watchdog.Arm(TimeSpan.FromSeconds(60), () => { });

        Assert.True(watchdog.IsArmed);
        Assert.Equal(1, scheduler.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(10)], scheduler.ScheduledDelays);
    }

    /// <summary>
    /// Two callers arriving together - the window close and the tray "Sair", the real race - while the
    /// FIRST attempt fails. Check, establish and publish must be one indivisible step under the arming
    /// gate, and the failed attempt must leave the deadline for the second caller to establish.
    /// <para>
    /// <b>White-box, deliberately.</b> Exclusion is proved on the REAL monitor: the private
    /// <c>_armGate</c> is read by reflection (a rename fails the lookup loudly, never silently), and a
    /// probe thread must be refused that monitor while the first attempt is inside the scheduler. A
    /// thread-state poll or a spin budget would only infer that the second caller is parked; a refused
    /// <see cref="Monitor.TryEnter(object, int)"/> with a zero timeout is a direct answer and waits on
    /// nothing. Declared residual: the second caller signals on the statement before its <c>Arm</c> call,
    /// so it is either parked on the gate or about to be - both are serialized behind the first attempt.
    /// </para>
    /// <para>
    /// Nothing asserts inside the scheduler hook: production's catch would turn a failed assertion into a
    /// <see cref="TerminationWatchdogArmingException"/>, which the outer <c>Assert.Throws</c> accepts. Every
    /// observation is captured and asserted after <c>Arm</c> returned.
    /// </para>
    /// </summary>
    [Fact]
    public void Two_concurrent_callers_after_a_failed_attempt_establish_exactly_one_deadline()
    {
        var gateField = typeof(TerminationWatchdog).GetField("_armGate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gateField); // the probe below is meaningless without the real gate

        var scheduler = new ManualWatchdogScheduler { FailNextAttempts = 1 };
        var watchdog = Create(scheduler);
        var gate = gateField.GetValue(watchdog);
        Assert.NotNull(gate);

        var terminations = 0;
        var hookCalls = 0;
        using var firstIsInsideTheScheduler = new ManualResetEventSlim(false);
        using var secondIsAboutToArm = new ManualResetEventSlim(false);
        var secondSawTheFirst = false;
        var secondFailed = 0;

        // Captured inside the hook, asserted after Arm returned.
        Exception? hookFailure = null;
        var secondSignalled = false;
        var probeFinished = false;
        var probeEnteredTheGate = true;
        var armedDuringTheFailedAttempt = true;

        var second = new Thread(() =>
        {
            secondSawTheFirst = firstIsInsideTheScheduler.Wait(Failsafe);
            secondIsAboutToArm.Set(); // the very next statement is the arming call
            try
            {
                watchdog.Arm(TimeSpan.FromSeconds(10), () => Interlocked.Increment(ref terminations));
            }
            catch (TerminationWatchdogArmingException)
            {
                Interlocked.Increment(ref secondFailed);
            }
        }) { IsBackground = true };
        second.Start();

        scheduler.OnScheduleAttempt = () =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1)
            {
                return; // the second caller's own attempt must not block
            }

            try
            {
                firstIsInsideTheScheduler.Set();
                secondSignalled = secondIsAboutToArm.Wait(Failsafe);

                var probe = new Thread(() =>
                {
                    probeEnteredTheGate = Monitor.TryEnter(gate, 0);
                    if (probeEnteredTheGate)
                    {
                        Monitor.Exit(gate);
                    }
                }) { IsBackground = true };
                probe.Start();
                probeFinished = probe.Join(Failsafe);

                armedDuringTheFailedAttempt = watchdog.IsArmed;
            }
            catch (Exception exception)
            {
                hookFailure = exception;
            }
        };

        Assert.Throws<TerminationWatchdogArmingException>(
            () => watchdog.Arm(TimeSpan.FromSeconds(10), () => Interlocked.Increment(ref terminations)));
        Assert.True(second.Join(Failsafe), "the second caller never returned");

        Assert.Null(hookFailure);
        Assert.True(secondSawTheFirst, "the second caller never saw the first attempt start");
        Assert.True(secondSignalled, "the second caller never reached its arming call");
        Assert.True(probeFinished, "the gate probe never finished");
        Assert.False(
            probeEnteredTheGate,
            "the arming gate was free while the first attempt was inside the scheduler: check, establish "
            + "and publish must be one indivisible step, or a concurrent caller can interleave");
        Assert.False(armedDuringTheFailedAttempt, "an attempt that has not succeeded must never read as armed");

        Assert.Equal(0, secondFailed);
        Assert.True(watchdog.IsArmed);
        Assert.Equal(2, scheduler.Attempts);
        Assert.Equal(1, scheduler.ScheduleCount);
        Assert.Equal(2, hookCalls);

        scheduler.ElapseAll();
        Assert.Equal(1, terminations);
    }

    /// <summary>
    /// The same race with a SUCCESSFUL first attempt: the losers must find an established deadline and
    /// return without scheduling a second escalation.
    /// <para>
    /// <b>Stress/regression smoke, not a deterministic proof.</b> Correct code passes under every
    /// interleaving, so this cannot be flaky; but whether a gate-less mutant actually interleaves here is
    /// up to the scheduler. The deterministic exclusion proof is the gate probe above.
    /// </para>
    /// </summary>
    [Fact]
    public void Concurrent_callers_never_establish_two_deadlines()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = Create(scheduler);
        var terminations = 0;
        const int callers = 16;
        using var start = new Barrier(callers);
        var failures = 0;

        var threads = Enumerable.Range(0, callers).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            try
            {
                watchdog.Arm(TimeSpan.FromSeconds(10), () => Interlocked.Increment(ref terminations));
            }
            catch (TerminationWatchdogArmingException)
            {
                Interlocked.Increment(ref failures);
            }
        }) { IsBackground = true }).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(Failsafe), "an arming caller never returned");
        }

        Assert.Equal(0, failures);
        Assert.True(watchdog.IsArmed);
        Assert.Equal(1, scheduler.ScheduleCount);
        Assert.Equal(1, scheduler.Attempts);

        scheduler.ElapseAll();
        Assert.Equal(1, terminations);
    }

    // ------------------------------------------------------- CV-21 B: the BOOL is inspected, for real

    /// <summary>
    /// The REAL P/Invoke, the REAL BOOL and the REAL last error, exercised without the test host dying:
    /// a null handle is refused by Windows with <c>ERROR_INVALID_HANDLE</c> and terminates nothing. The
    /// BOOL used to be discarded, so a refused escalation was reported as a completed one.
    /// </summary>
    [Fact]
    public void A_refused_termination_reports_the_win32_error_and_kills_nothing()
    {
        const int ErrorInvalidHandle = 6;

        var result = new ProcessTerminator().TerminateHandle(IntPtr.Zero, ProcessTerminator.WatchdogExitCode);

        Assert.False(result.Requested);
        Assert.Equal(ErrorInvalidHandle, result.Win32Error);
    }

    /// <summary>A scheduler that refuses to establish anything (CV-21 A).</summary>
    private sealed class ThrowingWatchdogScheduler : IWatchdogScheduler
    {
        public void ScheduleOnce(TimeSpan delay, Action callback) =>
            throw new InvalidOperationException("the wait could not be established");
    }
}
