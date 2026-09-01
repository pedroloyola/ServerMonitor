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
}
