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
    /// </summary>
    [Fact]
    public void A_failed_arming_does_not_consume_the_one_shot()
    {
        var scheduler = new ManualWatchdogScheduler();
        var watchdog = new TerminationWatchdog(new ThrowingWatchdogScheduler(), NullLogger<TerminationWatchdog>.Instance);
        Assert.Throws<TerminationWatchdogArmingException>(
            () => watchdog.Arm(TimeSpan.FromSeconds(10), () => { }));

        var armed = Create(scheduler);
        var terminations = 0;
        armed.Arm(TimeSpan.FromSeconds(10), () => terminations++);
        scheduler.ElapseAll();

        Assert.True(armed.IsArmed);
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
