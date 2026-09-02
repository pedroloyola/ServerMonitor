using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// The one authoritative exit (M13 S2 §C/§F), proved on the real controller with recording collaborators.
/// These cover A/B/C/D/E/K/M/N of the Atlas matrix plus Vigil C1, and every one of them fails if the
/// ordering, the one-shot guard, the finally, or the watchdog is removed.
/// </summary>
public sealed class AppLifecycleControllerTests
{
    /// <summary>Records the order of the steps and can be made to fail or block any of them.</summary>
    private sealed class RecordingExitSequence : IExitSequence
    {
        public List<string> Steps { get; } = new();

        public bool HostStops { get; set; } = true;

        public Action? OnStopAcceptingForegroundWork { get; set; }

        public Action? OnRemoveTrayIcon { get; set; }

        public Action? OnHideUserInterface { get; set; }

        public Action? OnDrainHost { get; set; }

        public void StopAcceptingForegroundWork()
        {
            Steps.Add(nameof(StopAcceptingForegroundWork));
            OnStopAcceptingForegroundWork?.Invoke();
        }

        public void RemoveTrayIcon()
        {
            Steps.Add(nameof(RemoveTrayIcon));
            OnRemoveTrayIcon?.Invoke();
        }

        public void HideUserInterface()
        {
            Steps.Add(nameof(HideUserInterface));
            OnHideUserInterface?.Invoke();
        }

        public bool DrainHost()
        {
            Steps.Add(nameof(DrainHost));
            OnDrainHost?.Invoke();
            return HostStops;
        }
    }

    private sealed class RecordingWatchdog : ITerminationWatchdog
    {
        public int ArmCount { get; private set; }

        public int DisarmCount { get; private set; }

        public TimeSpan? Deadline { get; private set; }

        public Action? OnDeadline { get; private set; }

        public void Arm(TimeSpan deadline, Action onDeadlineReached)
        {
            ArmCount++;
            Deadline = deadline;
            OnDeadline = onDeadlineReached;
        }

        public void Disarm() => DisarmCount++;

        /// <summary>Fires the deadline exactly as the real background thread would.</summary>
        public void FireDeadline() => OnDeadline?.Invoke();
    }

    private sealed class RecordingTerminator : IProcessTerminator
    {
        public List<int> Terminations { get; } = new();

        public void Terminate(int exitCode) => Terminations.Add(exitCode);
    }

    private sealed class Harness
    {
        public RecordingExitSequence Sequence { get; } = new();

        public RecordingWatchdog Watchdog { get; } = new();

        public RecordingTerminator Terminator { get; } = new();

        public int ApplicationExits { get; private set; }

        public AppLifecycleController Controller { get; }

        public Harness(LaunchMode launchMode = LaunchMode.Foreground, TimeSpan? deadline = null)
        {
            Controller = new AppLifecycleController(
                () => Sequence,
                () => ApplicationExits++,
                Watchdog,
                Terminator,
                NullLogger<AppLifecycleController>.Instance,
                launchMode,
                deadline);
        }
    }

    // ---------------------------------------------------------------- states

    [Fact]
    public void A_foreground_launch_starts_in_foreground()
    {
        var h = new Harness();

        Assert.Equal(AppLifecycleState.Foreground, h.Controller.State);
        Assert.False(h.Controller.StartedInBackground);
        Assert.False(h.Controller.IsExiting);
    }

    [Fact]
    public void A_background_launch_starts_in_background_and_remembers_it()
    {
        var h = new Harness(LaunchMode.Background);

        Assert.Equal(AppLifecycleState.Background, h.Controller.State);
        Assert.True(h.Controller.StartedInBackground);
    }

    [Fact]
    public void Foreground_and_background_transitions_are_reversible_until_the_exit()
    {
        var h = new Harness();

        h.Controller.EnterBackground();
        Assert.Equal(AppLifecycleState.Background, h.Controller.State);

        h.Controller.EnterForeground();
        Assert.Equal(AppLifecycleState.Foreground, h.Controller.State);
    }

    [Fact]
    public void Exiting_is_terminal()
    {
        var h = new Harness();

        h.Controller.RequestExit(ExitReason.TrayExit);
        h.Controller.EnterForeground();
        h.Controller.EnterBackground();

        Assert.Equal(AppLifecycleState.Exiting, h.Controller.State);
        Assert.True(h.Controller.IsExiting);
    }

    // ---------------------------------------------------------------- the exit

    /// <summary>
    /// The order is the design: refuse work, remove the icon (after the exit is committed, before the
    /// drain — Vigil C3), hide, then drain. Only then does the dispatcher end.
    /// </summary>
    [Fact]
    public void The_exit_runs_its_steps_in_the_reviewed_order_and_then_exits_once()
    {
        var h = new Harness();

        h.Controller.RequestExit(ExitReason.TrayExit);

        Assert.Equal(
            ["StopAcceptingForegroundWork", "RemoveTrayIcon", "HideUserInterface", "DrainHost"],
            h.Sequence.Steps);
        Assert.Equal(1, h.ApplicationExits);
    }

    [Fact]
    public void The_exit_is_one_shot_however_many_callers_ask()
    {
        var h = new Harness();

        Parallel.For(0, 32, _ => h.Controller.RequestExit(ExitReason.TrayExit));
        h.Controller.RequestExit(ExitReason.UserClosedWindow);

        Assert.Equal(1, h.ApplicationExits);
        Assert.Equal(4, h.Sequence.Steps.Count); // one pass, four steps
        Assert.Equal(1, h.Watchdog.ArmCount);
    }

    /// <summary>
    /// Vigil C1. A step that throws must not be able to leave the process alive, so the terminal exit
    /// lives in a finally — and the later steps still run, because each one is isolated.
    /// </summary>
    [Theory]
    [InlineData("StopAcceptingForegroundWork")]
    [InlineData("RemoveTrayIcon")]
    [InlineData("HideUserInterface")]
    [InlineData("DrainHost")]
    public void A_failing_step_still_ends_with_exactly_one_exit(string failingStep)
    {
        var h = new Harness();
        void Boom() => throw new InvalidOperationException(failingStep);
        switch (failingStep)
        {
            case "StopAcceptingForegroundWork": h.Sequence.OnStopAcceptingForegroundWork = Boom; break;
            case "RemoveTrayIcon": h.Sequence.OnRemoveTrayIcon = Boom; break;
            case "HideUserInterface": h.Sequence.OnHideUserInterface = Boom; break;
            default: h.Sequence.OnDrainHost = Boom; break;
        }

        h.Controller.RequestExit(ExitReason.TrayExit);

        Assert.Equal(1, h.ApplicationExits);
        Assert.Equal(4, h.Sequence.Steps.Count); // isolation: nothing is skipped
    }

    [Fact]
    public void An_exit_sequence_that_cannot_even_be_built_still_exits()
    {
        var exits = 0;
        var controller = new AppLifecycleController(
            () => throw new InvalidOperationException("no sequence"),
            () => exits++,
            new RecordingWatchdog(),
            new RecordingTerminator(),
            NullLogger<AppLifecycleController>.Instance);

        controller.RequestExit(ExitReason.StartupFailure);

        Assert.Equal(1, exits);
    }

    /// <summary>A host that did not stop is reported, and the exit continues regardless.</summary>
    [Fact]
    public void A_host_that_does_not_stop_does_not_stop_the_exit()
    {
        var h = new Harness();
        h.Sequence.HostStops = false;

        h.Controller.RequestExit(ExitReason.UserClosedWindow);

        Assert.Equal(1, h.ApplicationExits);
    }

    /// <summary>D/A12: nothing in the exit needs a window. The steps run identically from headless.</summary>
    [Fact]
    public void The_exit_works_identically_from_a_headless_process()
    {
        var h = new Harness(LaunchMode.Background);

        h.Controller.RequestExit(ExitReason.TrayExit);

        Assert.Equal(
            ["StopAcceptingForegroundWork", "RemoveTrayIcon", "HideUserInterface", "DrainHost"],
            h.Sequence.Steps);
        Assert.Equal(1, h.ApplicationExits);
    }

    // ---------------------------------------------------------------- the watchdog

    [Fact]
    public void The_watchdog_is_armed_only_by_the_exit_and_only_once()
    {
        var h = new Harness();

        h.Controller.EnterBackground();
        h.Controller.EnterForeground();
        Assert.Equal(0, h.Watchdog.ArmCount); // never armed outside Exiting

        h.Controller.RequestExit(ExitReason.TrayExit);
        h.Controller.RequestExit(ExitReason.TrayExit);

        Assert.Equal(1, h.Watchdog.ArmCount);
        Assert.Equal(AppLifecycleController.DefaultTerminationDeadline, h.Watchdog.Deadline);
    }

    [Fact]
    public void The_deadline_is_the_reviewed_ten_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(10), AppLifecycleController.DefaultTerminationDeadline);

    /// <summary>
    /// Atlas ALTA-2. Every ordered step can hang or fail and the process still ends: the watchdog is armed
    /// before any of them and terminates when the global deadline passes.
    /// </summary>
    [Fact]
    public void When_the_deadline_passes_the_process_is_terminated()
    {
        var h = new Harness(deadline: TimeSpan.FromSeconds(10));

        h.Controller.RequestExit(ExitReason.TrayExit);
        h.Watchdog.FireDeadline();

        Assert.Equal([ProcessTerminator.WatchdogExitCode], h.Terminator.Terminations);
        Assert.NotEqual(0, ProcessTerminator.WatchdogExitCode);
    }

    /// <summary>
    /// The watchdog is deliberately NOT disarmed after Application.Exit(): a process that fails to die
    /// after being asked is exactly what it is there to end.
    /// </summary>
    [Fact]
    public void The_watchdog_is_not_disarmed_by_the_exit()
    {
        var h = new Harness();

        h.Controller.RequestExit(ExitReason.TrayExit);

        Assert.Equal(0, h.Watchdog.DisarmCount);
    }

    /// <summary>Even if Application.Exit() itself throws, the watchdog still owns the ending.</summary>
    [Fact]
    public void An_exit_that_throws_leaves_the_watchdog_in_charge()
    {
        var watchdog = new RecordingWatchdog();
        var terminator = new RecordingTerminator();
        var controller = new AppLifecycleController(
            () => new RecordingExitSequence(),
            () => throw new InvalidOperationException("dispatcher gone"),
            watchdog,
            terminator,
            NullLogger<AppLifecycleController>.Instance);

        controller.RequestExit(ExitReason.TrayExit);
        watchdog.FireDeadline();

        Assert.Equal(1, watchdog.ArmCount);
        Assert.Single(terminator.Terminations);
    }

    [Fact]
    public void A_non_positive_deadline_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppLifecycleController(
            () => new RecordingExitSequence(),
            () => { },
            new RecordingWatchdog(),
            new RecordingTerminator(),
            NullLogger<AppLifecycleController>.Instance,
            LaunchMode.Foreground,
            TimeSpan.Zero));
}
