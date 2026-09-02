using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetProvider.Hosting;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// The repaint pump's LIFECYCLE (M13 QA-9): the pump must run exactly while widgets are on screen, and
/// firing it must actually repaint. These tests use a fake pump so the arm/disarm state machine is proved
/// without any filesystem or timing dependency; the pump's own behavior and the real end-to-end path are
/// covered by <see cref="WidgetSnapshotChangeWatcherTests"/> and
/// <see cref="WidgetRepaintIntegrationTests"/>.
/// </summary>
public sealed class WidgetProviderCoordinatorPumpTests
{
    private static WidgetActivation Widget(string id) =>
        new(id, "ServerAlyzer_Widget", WidgetSizeHint.Medium, CustomState: null);

    /// <summary>
    /// A reader pointing at a non-existent path yields an "unavailable" card; host.Update is still invoked,
    /// which is all these lifecycle tests observe.
    /// </summary>
    private static WidgetSnapshotReader NowhereReader() => new(
        Path.Combine(Path.GetTempPath(), "sm-no-such", Guid.NewGuid().ToString("N"), "widget-state.json"));

    private static (WidgetProviderCoordinator Coordinator, FakeWidgetRefreshPump Pump) NewCoordinator(
        FakeWidgetHost host)
    {
        FakeWidgetRefreshPump? pump = null;
        var coordinator = new WidgetProviderCoordinator(
            host,
            NowhereReader(),
            pumpFactory: refresh => pump = new FakeWidgetRefreshPump(refresh));

        return (coordinator, pump!);
    }

    [Fact]
    public void Pump_is_not_armed_before_any_widget_is_on_screen()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());

        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.Equal(0, pump.ArmCount);
        Assert.False(pump.IsArmed);
    }

    [Fact]
    public void Startup_with_zero_widgets_leaves_the_pump_off()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());

        coordinator.RehydrateFromHost();

        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.False(pump.IsArmed);
        Assert.Equal(0, pump.ArmCount);
    }

    [Fact]
    public void First_widget_on_screen_arms_the_pump()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());

        coordinator.OnWidgetActivated(Widget("a"));

        Assert.Equal(1, coordinator.OnScreenWidgetCount);
        Assert.True(pump.IsArmed);
        Assert.Equal(1, pump.ArmCount);
    }

    [Fact]
    public void Further_widgets_do_not_disturb_an_already_running_pump()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());

        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetActivated(Widget("b"));
        coordinator.OnWidgetContextChanged(Widget("b"));

        Assert.Equal(2, coordinator.OnScreenWidgetCount);
        Assert.True(pump.IsArmed);
        Assert.Equal(0, pump.DisarmCount);
    }

    [Fact]
    public void Deactivating_one_of_two_widgets_keeps_the_pump_running()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetActivated(Widget("b"));

        coordinator.OnWidgetDeactivated("a");

        Assert.Equal(1, coordinator.OnScreenWidgetCount);
        Assert.True(pump.IsArmed);
        Assert.Equal(0, pump.DisarmCount);
    }

    [Fact]
    public void Last_deactivate_disarms_the_pump()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetActivated(Widget("b"));

        coordinator.OnWidgetDeactivated("a");
        coordinator.OnWidgetDeactivated("b");

        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.False(pump.IsArmed);
        Assert.Equal(1, pump.DisarmCount);
    }

    [Fact]
    public void Deactivate_keeps_the_widget_registered_it_is_only_off_screen()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.OnWidgetDeactivated("a");

        // It still EXISTS — it is just not being viewed — so a later host callback repaints it directly.
        Assert.Equal(1, coordinator.ActiveWidgetCount);
        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.False(pump.IsArmed);
    }

    [Fact]
    public void Reactivating_after_the_board_reopens_arms_the_pump_again()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetDeactivated("a");

        coordinator.OnWidgetActivated(Widget("a"));

        Assert.True(pump.IsArmed);
        Assert.Equal(2, pump.ArmCount);
    }

    [Fact]
    public void Deleting_the_last_widget_disarms_the_pump()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.OnWidgetDeleted("a");

        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.False(pump.IsArmed);
    }

    [Fact]
    public void Deactivating_an_unknown_or_empty_id_is_harmless()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.OnWidgetDeactivated("does-not-exist");
        coordinator.OnWidgetDeactivated(null);
        coordinator.OnWidgetDeactivated(string.Empty);

        Assert.Equal(1, coordinator.OnScreenWidgetCount);
        Assert.True(pump.IsArmed);
    }

    [Fact]
    public void Duplicate_deactivate_disarms_once_and_stays_off()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.OnWidgetDeactivated("a");
        coordinator.OnWidgetDeactivated("a");

        Assert.False(pump.IsArmed);
        Assert.Equal(2, pump.DisarmCount); // idempotent by contract; the real pump absorbs the repeat
    }

    /// <summary>
    /// POLICY (see <see cref="WidgetProviderCoordinator.RehydrateFromHost"/>): a provider relaunched with
    /// widgets already pinned must repaint them. The Windows App SDK exposes no activation state on
    /// WidgetInfo and does not promise an Activate after recovery, so recovered widgets count as on screen
    /// and the host's first Deactivate corrects it. Getting this wrong in the other direction reproduces
    /// QA-9 silently.
    /// </summary>
    [Fact]
    public void Rehydration_arms_the_pump_so_a_relaunched_provider_keeps_repainting()
    {
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("a"));
        host.Existing.Add(Widget("b"));
        var (coordinator, pump) = NewCoordinator(host);

        coordinator.RehydrateFromHost();

        Assert.Equal(2, coordinator.OnScreenWidgetCount);
        Assert.True(pump.IsArmed);
    }

    [Fact]
    public void Rehydrated_widgets_are_corrected_by_the_hosts_first_deactivate()
    {
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("a"));
        var (coordinator, pump) = NewCoordinator(host);
        coordinator.RehydrateFromHost();

        coordinator.OnWidgetDeactivated("a");

        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.False(pump.IsArmed);
    }

    [Fact]
    public void Rehydration_that_recovers_nothing_because_of_a_host_failure_leaves_the_pump_off()
    {
        var host = new FakeWidgetHost { ThrowOnGetActiveWidgets = true };
        var (coordinator, pump) = NewCoordinator(host);

        coordinator.RehydrateFromHost();

        Assert.False(pump.IsArmed);
        Assert.Equal(0, pump.ArmCount);
    }

    [Fact]
    public void The_pump_drives_a_real_repaint_of_every_registered_widget()
    {
        var host = new FakeWidgetHost();
        var (coordinator, pump) = NewCoordinator(host);
        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetActivated(Widget("b"));
        var beforePump = host.Updates.Count;

        pump.FireRefresh();

        // Proves the delegate handed to the pump really is RefreshAll: every registered widget repainted.
        Assert.Equal(beforePump + 2, host.Updates.Count);
        Assert.Equal(2, host.UpdateCountFor("a"));
        Assert.Equal(2, host.UpdateCountFor("b"));
    }

    [Fact]
    public void Shutdown_disarms_and_disposes_the_pump()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.OnWidgetActivated(Widget("a"));

        coordinator.Shutdown();

        Assert.Equal(1, pump.DisarmCount);
        Assert.Equal(1, pump.DisposeCount);
        Assert.False(pump.IsArmed);
    }

    [Fact]
    public void A_widget_callback_after_shutdown_never_rearms_the_pump()
    {
        var (coordinator, pump) = NewCoordinator(new FakeWidgetHost());
        coordinator.Shutdown();

        coordinator.OnWidgetActivated(Widget("a"));

        Assert.False(pump.IsArmed);
        Assert.Equal(0, pump.ArmCount);
    }

    [Fact]
    public void A_pump_that_throws_never_breaks_widget_handling()
    {
        var host = new FakeWidgetHost();
        var (coordinator, pump) = NewCoordinator(host);
        pump.ThrowOnStateChange = true;

        coordinator.OnWidgetActivated(Widget("a"));
        coordinator.OnWidgetDeactivated("a");

        // The exception was contained (§16) and the widget was still painted by the host callback itself.
        Assert.Equal(1, host.UpdateCountFor("a"));
        Assert.Equal(1, pump.ArmCount);
        Assert.Equal(1, pump.DisarmCount);
    }

    [Fact]
    public void Concurrent_activations_and_deactivations_settle_on_the_correct_pump_state()
    {
        var host = new FakeWidgetHost();
        var (coordinator, pump) = NewCoordinator(host);
        var ids = Enumerable.Range(0, 32).Select(i => $"w{i}").ToArray();

        Parallel.ForEach(ids, id => coordinator.OnWidgetActivated(Widget(id)));
        Assert.True(pump.IsArmed);

        Parallel.ForEach(ids, coordinator.OnWidgetDeactivated);

        // The last transition wins deterministically: an empty on-screen set means a stopped pump.
        Assert.Equal(0, coordinator.OnScreenWidgetCount);
        Assert.False(pump.IsArmed);
    }

    // ---------------------------------------------------------------------------------------------
    // The REAL pump, in the loop, on a controllable change source and a fake clock. This is where the
    // "and then nothing repaints" claims live: driving the source directly makes them deterministic,
    // whereas the same claim over a real filesystem can only ever be "nothing arrived within N seconds",
    // which passes by accident on a slow machine and misses whatever arrives late.
    // ---------------------------------------------------------------------------------------------

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(60);

    private static (WidgetProviderCoordinator Coordinator, FakeSnapshotChangeSource Source, FakeTimeProvider Clock)
        NewCoordinatorWithRealPump(FakeWidgetHost host)
    {
        var source = new FakeSnapshotChangeSource();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new WidgetProviderCoordinator(
            host,
            NowhereReader(),
            clock,
            pumpFactory: refresh => new WidgetSnapshotChangeWatcher(refresh, source, clock, Debounce, Backstop));

        return (coordinator, source, clock);
    }

    /// <summary>
    /// One atomic commit legitimately produces several filesystem events. Exactly one repaint must reach
    /// the host — counted after the window has provably closed, not the instant the first paint is seen.
    /// </summary>
    [Fact]
    public void A_burst_of_snapshot_signals_repaints_each_widget_exactly_once()
    {
        var host = new FakeWidgetHost();
        var (coordinator, source, clock) = NewCoordinatorWithRealPump(host);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));
            Assert.Equal(1, host.UpdateCountFor("a")); // the host callback's own paint

            source.RaiseBurst(8); // temp created, renamed onto the destination, backup renamed, deleted...
            clock.Advance(Debounce);

            Assert.Equal(2, host.UpdateCountFor("a"));
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    /// <summary>
    /// With the board closed the provider is silent AND idle: the change source is stopped, so nothing is
    /// even read, and neither a stray signal nor any number of backstop intervals paints anything.
    /// </summary>
    [Fact]
    public void After_the_last_deactivate_no_signal_and_no_backstop_repaints_again()
    {
        var host = new FakeWidgetHost();
        var (coordinator, source, clock) = NewCoordinatorWithRealPump(host);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));
            coordinator.OnWidgetDeactivated("a");
            var painted = host.UpdateCountFor("a");

            Assert.False(source.IsWatching);
            Assert.Equal(1, source.StopCount);

            source.RaiseBurst(5);
            clock.Advance(Debounce * 4);
            clock.Advance(Backstop * 3);

            Assert.Equal(painted, host.UpdateCountFor("a"));
            Assert.Equal(0, coordinator.OnScreenWidgetCount);
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    /// <summary>Reopening the board resumes the pump on the same source.</summary>
    [Fact]
    public void Reopening_the_board_resumes_repainting_on_the_same_source()
    {
        var host = new FakeWidgetHost();
        var (coordinator, source, clock) = NewCoordinatorWithRealPump(host);
        try
        {
            coordinator.OnWidgetActivated(Widget("a"));
            coordinator.OnWidgetDeactivated("a");
            coordinator.OnWidgetActivated(Widget("a"));
            var painted = host.UpdateCountFor("a");

            Assert.True(source.IsWatching);
            source.Raise();
            clock.Advance(Debounce);

            Assert.Equal(painted + 1, host.UpdateCountFor("a"));
        }
        finally
        {
            coordinator.Shutdown();
        }
    }

    /// <summary>
    /// After <see cref="WidgetProviderCoordinator.Shutdown"/> the source is disposed and unhooked, and
    /// nothing the clock or a stray signal can deliver reaches the host.
    /// </summary>
    [Fact]
    public void After_shutdown_no_signal_and_no_timer_can_repaint()
    {
        var host = new FakeWidgetHost();
        var (coordinator, source, clock) = NewCoordinatorWithRealPump(host);
        coordinator.OnWidgetActivated(Widget("a"));
        var painted = host.UpdateCountFor("a");

        coordinator.Shutdown();

        Assert.Equal(1, source.DisposeCount);
        Assert.False(source.HasSubscribers);
        Assert.False(source.IsWatching);

        source.RaiseBurst(5);
        clock.Advance(Debounce * 4);
        clock.Advance(Backstop * 3);

        Assert.Equal(painted, host.UpdateCountFor("a"));
    }

    /// <summary>
    /// The rehydration policy, end to end through the real pump: a provider relaunched with widgets
    /// already pinned repaints them on the next snapshot commit, with no <c>Activate</c> from the host.
    /// </summary>
    [Fact]
    public void A_rehydrated_widget_repaints_on_the_next_snapshot_signal()
    {
        var host = new FakeWidgetHost();
        host.Existing.Add(Widget("a"));
        var (coordinator, source, clock) = NewCoordinatorWithRealPump(host);
        try
        {
            coordinator.RehydrateFromHost();
            Assert.Equal(1, host.UpdateCountFor("a"));
            Assert.True(source.IsWatching);

            source.Raise();
            clock.Advance(Debounce);

            Assert.Equal(2, host.UpdateCountFor("a"));
        }
        finally
        {
            coordinator.Shutdown();
        }
    }
}
