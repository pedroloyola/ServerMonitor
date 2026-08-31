using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class WidgetProviderCoordinatorTests
{
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

        var rehydrate = Task.Run(coordinator.RehydrateFromHost);
        Assert.True(await host.Entered.WaitAsync(5000)); // host is now blocked inside GetActiveWidgets

        coordinator.Shutdown(); // the process decided to exit while rehydration was still running
        block.Set();            // release the host; rehydration continues, but must see the shutdown
        await rehydrate;

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
        var host = new FakeWidgetHost();
        var block = new ManualResetEventSlim(false);
        host.BlockUpdate = block;
        var coordinator = NewCoordinator(host);
        var drainEntered = new SemaphoreSlim(0);
        coordinator.DrainWaitEnteredForTesting = () => drainEntered.Release();

        var activate = Task.Run(() => coordinator.OnWidgetActivated(Widget("a")));
        Assert.True(await host.UpdateEntered.WaitAsync(5000)); // update is in flight, blocked in host

        var shutdown = Task.Run(coordinator.Shutdown);
        Assert.True(await drainEntered.WaitAsync(5000)); // Shutdown is provably blocked on the drain
        Assert.False(shutdown.IsCompleted);              // it has NOT returned while the update is in flight

        block.Set(); // release the update → it completes → drain event set → Shutdown returns
        await shutdown;
        await activate;

        Assert.Single(host.Updates); // the drained update completed, ordered before Shutdown returned

        var before = host.Updates.Count;
        coordinator.OnWidgetActivated(Widget("b")); // after shutdown → no-op
        Assert.Equal(before, host.Updates.Count);
    }

    [Fact]
    public async Task Shutdown_is_bounded_when_an_update_is_stuck_in_the_host()
    {
        // Bounded-shutdown residual: if a synchronous host.Update is genuinely stuck past DrainTimeout,
        // Shutdown returns anyway (the process must be able to revoke). Deterministic via FakeTimeProvider.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        var host = new FakeWidgetHost();
        var block = new ManualResetEventSlim(false);
        host.BlockUpdate = block; // never released until the end → the update stays stuck
        var coordinator = NewCoordinator(host, clock);
        var drainEntered = new SemaphoreSlim(0);
        coordinator.DrainWaitEnteredForTesting = () => drainEntered.Release();

        var activate = Task.Run(() => coordinator.OnWidgetActivated(Widget("a")));
        Assert.True(await host.UpdateEntered.WaitAsync(5000)); // stuck in host.Update

        var shutdown = Task.Run(coordinator.Shutdown);
        Assert.True(await drainEntered.WaitAsync(5000)); // Shutdown has created its timeout and is waiting
        clock.Advance(TimeSpan.FromSeconds(2));          // fire the bounded-drain timeout
        await shutdown;                                  // returns despite the update still being stuck

        Assert.Empty(host.Updates); // the stuck update has NOT completed — proves the timeout path

        block.Set(); // clean up the background task
        await activate;
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
