using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// Coverage A, B and L: what the close button does (M13 S2 §D). This is the decision that closes
/// M13-QA-8 — with background monitoring on, X hides the Dashboard and the monitoring host is never
/// asked to stop, so the widget snapshot keeps moving.
/// </summary>
public sealed class WindowCloseCoordinatorTests
{
    private sealed class RecordingWindowController : IApplicationWindowController
    {
        public int HideToBackgroundCount { get; private set; }

        public int RestoreCount { get; private set; }

        public bool IsAttached => true;

        public bool IsMaterialized => true;

        public void Attach(Window window) { }

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideForMinimize() { }

        /// <summary>Ordered trace, so a test can assert WHERE the hide happened, not only that it did.</summary>
        public List<string> Calls { get; } = [];

        public void HideToBackground()
        {
            HideToBackgroundCount++;
            Calls.Add("hide");
        }

        public void RestoreAndActivate() => RestoreCount++;

        public void OpenSettings() { }

        public void OpenBackgroundSettings() { }

        public void ToggleCompactMode() { }

        public void RequestClose() { }

        public void BeginShutdown() { }
    }

    private sealed class RecordingNoticePresenter : IBackgroundNoticePresenter
    {
        public int Attempts { get; private set; }

        public bool TryShowOnce()
        {
            Attempts++;
            return true;
        }
    }

    private sealed class Harness
    {
        public FakeAppLifecycleController Lifecycle { get; }

        public FakeBackgroundMonitoringSettingsService Settings { get; }

        public RecordingWindowController Window { get; } = new();

        public RecordingNoticePresenter Notice { get; } = new();

        public bool HasExitAffordance { get; set; } = true;

        /// <summary>The operations the coordinator asked for, as values. It supplies no code.</summary>
        public List<TrayGuardedOperation> Performed { get; } = new();

        public WindowCloseCoordinator Coordinator { get; }

        public Harness(
            bool backgroundEnabled = true,
            AppLifecycleState state = AppLifecycleState.Foreground)
        {
            Lifecycle = new FakeAppLifecycleController(state);
            Settings = new FakeBackgroundMonitoringSettingsService(backgroundEnabled);
            Coordinator = new WindowCloseCoordinator(
                Lifecycle,
                Settings,
                operation =>
                {
                    // THE HARNESS IS NOW THE PERFORMER, because the coordinator no longer is. It receives
                    // a VALUE naming the operation and performs it, or performs the fallback -- exactly
                    // the two outcomes the machine chooses between, with no third and no silent one.
                    Performed.Add(operation);

                    if (!HasExitAffordance)
                    {
                        Lifecycle.RequestExit(ExitReason.UserClosedWindow);
                        return;
                    }

                    Window.HideToBackground();
                    Lifecycle.EnterBackground();
                    Notice.TryShowOnce();
                },
                NullLogger<WindowCloseCoordinator>.Instance);
        }
    }

    /// <summary>
    /// O1, SIXTH RING: this coordinator CANNOT hide a window, whatever it learns.
    /// </summary>
    /// <remarks>
    /// The previous version of this test proved the hide happened inside the commit — which mattered
    /// while the coordinator still did the hiding. It does not any more, and that is a stronger place to
    /// be: five corrections in this slice removed a way of OBTAINING the permission and left the ACTION
    /// reachable, so knowing the affordance held was always enough to use it later. The action is now
    /// unreachable from here, so there is nothing to learn and nothing to replay.
    /// <para>
    /// The one delegate it does hold carries a VALUE in and returns nothing, so it is a request and not a
    /// place for this class to run code inside the authorisation.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_coordinator_cannot_hide_a_window_at_all()
    {
        var parameters = typeof(WindowCloseCoordinator).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IApplicationWindowController));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IBackgroundNoticePresenter));

        var perform = Assert.Single(
            parameters.Where(p => typeof(Delegate).IsAssignableFrom(p.ParameterType)));
        var invoke = perform.ParameterType.GetMethod("Invoke")!;

        Assert.Equal(typeof(void), invoke.ReturnType);
        Assert.Equal(
            typeof(TrayGuardedOperation),
            Assert.Single(invoke.GetParameters()).ParameterType);
    }

    /// <summary>The request names the operation as a value, and the coordinator supplies no code.</summary>
    [Fact]
    public void The_close_asks_for_the_background_operation_by_name()
    {
        var h = new Harness(backgroundEnabled: true) { HasExitAffordance = true };

        h.Coordinator.HandleCloseRequest();

        Assert.Equal([TrayGuardedOperation.EnterBackground], h.Performed);
    }

    /// <summary>A: X with background monitoring ON. Hide, keep everything running, never exit.</summary>
    [Fact]
    public void Closing_with_background_enabled_hides_and_never_exits()
    {
        var h = new Harness(backgroundEnabled: true);

        var cancelled = h.Coordinator.HandleCloseRequest();

        Assert.True(cancelled); // the platform must NOT destroy the window
        Assert.Equal(1, h.Window.HideToBackgroundCount);
        Assert.Equal(0, h.Lifecycle.ExitRequests);
        Assert.Equal(AppLifecycleState.Background, h.Lifecycle.State);
        Assert.Equal(1, h.Notice.Attempts);
    }

    /// <summary>B: X with background monitoring OFF. One authoritative exit, no window destruction.</summary>
    [Fact]
    public void Closing_with_background_disabled_requests_the_authoritative_exit()
    {
        var h = new Harness(backgroundEnabled: false);

        var cancelled = h.Coordinator.HandleCloseRequest();

        Assert.True(cancelled);
        Assert.Equal(1, h.Lifecycle.ExitRequests);
        Assert.Equal(ExitReason.UserClosedWindow, Assert.Single(h.Lifecycle.ExitReasons));
        Assert.Equal(0, h.Window.HideToBackgroundCount);
        Assert.Equal(0, h.Notice.Attempts); // nothing to explain: the app is closing
    }

    /// <summary>
    /// §K: background is only a legitimate state while a true exit is reachable. Without a tray icon,
    /// hiding would strand a monitoring process the user cannot stop, so X exits instead.
    /// </summary>
    [Fact]
    public void Closing_without_an_exit_affordance_exits_even_with_background_enabled()
    {
        var h = new Harness(backgroundEnabled: true) { HasExitAffordance = false };

        var cancelled = h.Coordinator.HandleCloseRequest();

        Assert.True(cancelled);
        Assert.Equal(1, h.Lifecycle.ExitRequests);
        Assert.Equal(0, h.Window.HideToBackgroundCount);
    }

    /// <summary>The only close that is allowed through is the one Application.Exit() performs itself.</summary>
    [Fact]
    public void Closing_while_exiting_lets_the_window_go()
    {
        var h = new Harness(state: AppLifecycleState.Exiting);

        var cancelled = h.Coordinator.HandleCloseRequest();

        Assert.False(cancelled);
        Assert.Equal(0, h.Window.HideToBackgroundCount);
        Assert.Equal(0, h.Notice.Attempts);
    }

    /// <summary>The notice is attempted once per close; the presenter itself is what makes it once ever.</summary>
    [Fact]
    public void Repeated_closes_hide_every_time_and_never_exit()
    {
        var h = new Harness(backgroundEnabled: true);

        h.Coordinator.HandleCloseRequest();
        h.Lifecycle.EnterForeground();
        h.Coordinator.HandleCloseRequest();

        Assert.Equal(2, h.Window.HideToBackgroundCount);
        Assert.Equal(0, h.Lifecycle.ExitRequests);
    }

    /// <summary>
    /// The hide is never delayed or cancelled by the notice: the window is already hidden by the time the
    /// presenter runs, and a presenter that throws cannot change that.
    /// <para>
    /// MOVED to <c>TrayAffordanceLifecycleTests</c> in round 8, with the operation itself. It is asserted
    /// where the hide now happens; leaving a copy here would test the harness rather than the code.
    /// </para>
    /// </summary>

    private sealed class OrderRecordingWindowController(List<string> order) : IApplicationWindowController
    {
        public bool IsAttached => true;

        public bool IsMaterialized => true;

        public void Attach(Window window) { }

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideForMinimize() { }

        public void HideToBackground() => order.Add("hide");

        public void RestoreAndActivate() { }

        public void OpenSettings() { }

        public void OpenBackgroundSettings() { }

        public void ToggleCompactMode() { }

        public void RequestClose() { }

        public void BeginShutdown() { }
    }

    private sealed class ThrowingNoticePresenter(List<string> order) : IBackgroundNoticePresenter
    {
        public bool TryShowOnce()
        {
            order.Add("notice");
            throw new InvalidOperationException("notifications unavailable");
        }
    }
}
