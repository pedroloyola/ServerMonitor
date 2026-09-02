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
}
