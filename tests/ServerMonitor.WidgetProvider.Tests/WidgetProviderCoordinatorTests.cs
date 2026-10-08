using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class WidgetProviderCoordinatorTests
{
    /// <summary>
    /// Deadlock guard only: every wait below is released by a signal the test controls, never by elapsed time, so
    /// this bound is never reached by a working coordinator — however slow the runner. It exists so a regression
    /// fails the test instead of hanging the run.
    /// </summary>
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a blocking call on its own thread. The lifecycle tests deliberately park calls inside the host, and a
    /// parked thread-pool thread (plus a busy CI runner) can delay an unrelated <c>Task.Run</c> by seconds (CI runs
    /// 37057158486 / 37058982750). A dedicated thread starts at once, so the race setup never waits on the pool.
    /// </summary>
    private static Task RunOnDedicatedThread(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static WidgetProviderCoordinator NewCoordinator(FakeWidgetHost host)
    {
        // A reader pointing at a non-existent path yields an "unavailable" card; host.Update is still
        // invoked, which is all these lifecycle tests observe.
        var reader = new WidgetSnapshotReader(
            Path.Combine(Path.GetTempPath(), "sm-no-such", Guid.NewGuid().ToString("N"), "widget-state.json"));
        return new WidgetProviderCoordinator(host, reader);
    }

    private static WidgetActivation Widget(string id) =>
        new(id, "ServerAlyzer_Widget", WidgetSizeHint.Medium, CustomState: null);

    [Fact]
    public void Startup_with_zero_widgets_paints_nothing()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);

        coordinator.RehydrateFromHost();

        Assert.Equal(0, coordinator.ActiveWidgetCount);
        Assert.Empty(host.Updates);
    }

    [Fact]
    public void Startup_with_existing_widgets_rehydrates_and_repaints_each()
    {
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("a"));
        host.Existing.Add(Widget("b"));
        var coordinator = NewCoordinator(host);

        coordinator.RehydrateFromHost();

        Assert.Equal(2, coordinator.ActiveWidgetCount);
        Assert.Equal(1, host.UpdateCountFor("a"));
        Assert.Equal(1, host.UpdateCountFor("b"));
    }

    [Fact]
    public void Create_registers_and_paints()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);

        coordinator.OnWidgetActivated(Widget("a"));

        Assert.Equal(1, coordinator.ActiveWidgetCount);
        Assert.Equal(1, host.UpdateCountFor("a"));
    }

    [Fact]
    public void Duplicate_create_does_not_double_register()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);

        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetActivated(Widget("a"));

        Assert.Equal(1, coordinator.ActiveWidgetCount);
        Assert.Equal(2, host.UpdateCountFor("a")); // each activation repaints, but only one registration
    }

    [Fact]
    public void Delete_and_duplicate_delete_are_safe()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.OnWidgetDeleted("a");
        coordinator.OnWidgetDeleted("a"); // idempotent, no throw

        Assert.Equal(0, coordinator.ActiveWidgetCount);
    }

    [Fact]
    public void Multiple_widgets_all_paint()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);

        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetActivated(Widget("b"));
        coordinator.OnWidgetActivated(Widget("c"));
        coordinator.RefreshAll();

        Assert.Equal(3, coordinator.ActiveWidgetCount);
        Assert.Equal(2, host.UpdateCountFor("a")); // create + refresh
        Assert.Equal(2, host.UpdateCountFor("b"));
        Assert.Equal(2, host.UpdateCountFor("c"));
    }

    [Fact]
    public void Rehydrate_skips_a_widget_deleted_before_rehydration()
    {
        // H-2: a Delete seen before the one-shot rehydration tombstones the id; a stale GetWidgetInfos
        // snapshot must not resurrect it.
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("ghost"));
        host.Existing.Add(Widget("live"));
        var coordinator = NewCoordinator(host);

        coordinator.OnWidgetDeleted("ghost"); // deleted before rehydration → tombstoned
        coordinator.RehydrateFromHost();

        Assert.Equal(1, coordinator.ActiveWidgetCount);
        Assert.Equal(0, host.UpdateCountFor("ghost")); // never repainted
        Assert.Equal(1, host.UpdateCountFor("live"));
    }

    [Fact]
    public void Delete_after_rehydration_does_not_tombstone_indefinitely()
    {
        // After the one-shot rehydration, a normal Create→Delete→Create cycle must work (no stale tombstone).
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);
        coordinator.RehydrateFromHost();

        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetDeleted("a");
        coordinator.OnWidgetActivated(Widget("a"));

        Assert.Equal(1, coordinator.ActiveWidgetCount);
    }

    [Fact]
    public void GetActiveWidgets_exception_is_contained()
    {
        var host = new FakeWidgetHost { ThrowOnGetActiveWidgets = true };
        var coordinator = NewCoordinator(host);

        coordinator.RehydrateFromHost(); // must not throw
        Assert.Equal(0, coordinator.ActiveWidgetCount);
    }

    [Fact]
    public void Update_exception_for_one_widget_does_not_stop_others()
    {
        var host = new FakeWidgetHost();
        host.ThrowOnUpdateFor.Add("bad");
        host.Existing.Add(Widget("bad"));
        host.Existing.Add(Widget("good"));
        var coordinator = NewCoordinator(host);

        coordinator.RehydrateFromHost(); // must not throw despite "bad" failing

        Assert.Equal(2, coordinator.ActiveWidgetCount);
        Assert.Equal(1, host.UpdateCountFor("good")); // the good one still painted
        Assert.Equal(0, host.UpdateCountFor("bad"));
    }

    [Fact]
    public async Task Late_rehydration_after_shutdown_is_a_noop()
    {
        // M-1: a GetWidgetInfos that overran its startup bound and returns AFTER the process decided to
        // exit must not add or repaint widgets. Deterministic: block the host inside GetActiveWidgets,
        // cancel the shutdown token, then release the host and let rehydration complete.
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("w"));
        var block = new ManualResetEventSlim(false);
        host.BlockGetActiveWidgets = block;
        var coordinator = NewCoordinator(host);

        var rehydrate = RunOnDedicatedThread(coordinator.RehydrateFromHost);
        Assert.True(await host.Entered.WaitAsync(DeadlockGuard)); // host is now blocked inside GetActiveWidgets

        coordinator.Shutdown(); // the process decided to exit while rehydration was still running
        block.Set();            // release the host; rehydration continues, but must see the shutdown
        await rehydrate.WaitAsync(DeadlockGuard);

        Assert.Equal(0, coordinator.ActiveWidgetCount); // no widget added
        Assert.Empty(host.Updates);                     // no repaint
    }

    private static WidgetProviderCoordinator NewCoordinator(FakeWidgetHost host, TimeProvider timeProvider)
    {
        var reader = new WidgetSnapshotReader(
            Path.Combine(Path.GetTempPath(), "sm-no-such", Guid.NewGuid().ToString("N"), "widget-state.json"),
            timeProvider: timeProvider);
        return new WidgetProviderCoordinator(host, reader, timeProvider);
    }

    [Fact]
    public async Task Shutdown_drains_an_in_flight_update_then_blocks_later_ones()
    {
        // M-1 barrier: an update already past the in-flight lease is drained (ordered before Shutdown
        // returns), and any update attempted after Shutdown is a no-op. Deterministic via the drain-wait
        // seam — no wall-clock assertion.
        // The bounded-drain timeout runs on a FakeTimeProvider that is never advanced: with the real clock its 2 s
        // could expire while a slow runner had not yet resumed this test, and Shutdown would return through the
        // timeout path instead of the drain (CI FLAKE-WP-DRAIN). Here the ONLY way out of the drain is the update
        // completing; the timeout path has its own test (Shutdown_is_bounded_when_an_update_is_stuck_in_the_host).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        var host = new FakeWidgetHost();
        var block = new ManualResetEventSlim(false);
        host.BlockUpdate = block;
        var coordinator = NewCoordinator(host, clock);
        var drainEntered = new SemaphoreSlim(0);
        coordinator.DrainWaitEnteredForTesting = () => drainEntered.Release();

        var activate = RunOnDedicatedThread(() => coordinator.OnWidgetActivated(Widget("a")));
        Assert.True(await host.UpdateEntered.WaitAsync(DeadlockGuard)); // update is in flight, blocked in host

        // What the host had recorded at the instant Shutdown returned: the ordering guarantee itself, observed
        // inside the Shutdown thread rather than inferred from a later snapshot.
        var updatesWhenShutdownReturned = -1;
        var shutdown = RunOnDedicatedThread(() =>
        {
            coordinator.Shutdown();
            updatesWhenShutdownReturned = host.Updates.Count;
        });
        Assert.True(await drainEntered.WaitAsync(DeadlockGuard)); // Shutdown is provably blocked on the drain
        Assert.False(shutdown.IsCompleted);                        // it has NOT returned while the update is in flight

        block.Set(); // release the update → it completes → drain event set → Shutdown returns
        await shutdown.WaitAsync(DeadlockGuard);
        await activate.WaitAsync(DeadlockGuard);

        Assert.Equal(1, updatesWhenShutdownReturned); // the drained update completed BEFORE Shutdown returned
        Assert.Single(host.Updates);

        var before = host.Updates.Count;
        coordinator.OnWidgetActivated(Widget("b")); // after shutdown → no-op
        Assert.Equal(before, host.Updates.Count);
    }

    [Fact]
    public async Task Shutdown_is_bounded_when_an_update_is_stuck_in_the_host()
    {
        // Bounded-shutdown residual: if a synchronous host.Update is genuinely stuck past DrainTimeout,
        // Shutdown returns anyway (the process must be able to revoke). Deterministic via FakeTimeProvider.
        // The guarantee is functional — Shutdown leaves through its timeout path once ITS clock has passed the
        // drain bound — and is proven on the fake clock; nothing here asserts a wall-clock latency. The drain-entered
        // seam fires after the drain's timeout source exists, so the advance always reaches that timer.
        var clock = new TimerRecordingTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        var host = new FakeWidgetHost();
        var block = new ManualResetEventSlim(false);
        host.BlockUpdate = block; // never released until the end → the update stays stuck
        var coordinator = NewCoordinator(host, clock);
        var drainEntered = new SemaphoreSlim(0);
        coordinator.DrainWaitEnteredForTesting = () => drainEntered.Release();

        var activate = RunOnDedicatedThread(() => coordinator.OnWidgetActivated(Widget("a")));
        Assert.True(await host.UpdateEntered.WaitAsync(DeadlockGuard)); // stuck in host.Update

        var shutdown = RunOnDedicatedThread(coordinator.Shutdown);
        Assert.True(await drainEntered.WaitAsync(DeadlockGuard)); // Shutdown has created its timeout and is waiting
        // CI-WP-BOUNDED-CLOCK: the 2 s drain bound must be a timer on the INJECTED clock. A drain on the real
        // clock would also let Shutdown return (after 2 real seconds, inside the deadlock guard), so only this
        // assertion distinguishes the two.
        Assert.Contains(TimeSpan.FromSeconds(2), clock.CreatedTimerDueTimes);
        clock.Advance(TimeSpan.FromSeconds(2));                    // fire the bounded-drain timeout
        await shutdown.WaitAsync(DeadlockGuard);                   // returns despite the update still being stuck

        Assert.Empty(host.Updates); // the stuck update has NOT completed — proves the timeout path

        block.Set(); // clean up the background task
        await activate.WaitAsync(DeadlockGuard);
    }

    [Fact]
    public void Context_changed_repaints()
    {
        var host = new FakeWidgetHost();
        var coordinator = NewCoordinator(host);
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.OnWidgetContextChanged(new WidgetActivation("a", "ServerAlyzer_Widget", WidgetSizeHint.Large, null));

        Assert.Equal(2, host.UpdateCountFor("a"));
        Assert.Equal(1, coordinator.ActiveWidgetCount);
    }
}
