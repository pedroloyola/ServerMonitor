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
                Window,
                Notice,
                enterBackground =>
                {
                    // The commit runs the act itself and returns NOTHING, so the harness models the real
                    // contract rather than a permission the coordinator could keep. The markers make the
                    // ORDER visible: the hide has to happen BETWEEN them, because a coordinator that
                    // hides after the commit has an interval again, and an interval is the whole defect.
                    if (!HasExitAffordance)
                    {
                        return;
                    }

                    Window.Calls.Add("commit:enter");
                    enterBackground();
                    Window.Calls.Add("commit:leave");
                },
                NullLogger<WindowCloseCoordinator>.Instance);
        }
    }

    /// <summary>
    /// The hide happens INSIDE the commit, not after it.
    /// </summary>
    /// <remarks>
    /// Asserting only that the window was hidden cannot tell the two apart: a coordinator that asks
    /// permission and hides afterwards hides it too, and that is exactly the sequence that left the
    /// process alive, invisible and with no way out when the affordance was lost in between.
    /// </remarks>
    [Fact]
    public void The_hide_happens_inside_the_commit_and_not_after_it()
    {
        var h = new Harness(backgroundEnabled: true) { HasExitAffordance = true };

        h.Coordinator.HandleCloseRequest();

        var entered = h.Window.Calls.IndexOf("commit:enter");
        var hidden = h.Window.Calls.IndexOf("hide");
        var left = h.Window.Calls.IndexOf("commit:leave");

        Assert.True(entered >= 0 && left > entered, $"[{string.Join(", ", h.Window.Calls)}]");
        Assert.InRange(hidden, entered + 1, left - 1);
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
    /// </summary>
    [Fact]
    public void The_notice_cannot_delay_or_cancel_the_hide()
    {
        var h = new Harness(backgroundEnabled: true);
        var order = new List<string>();
        var coordinator = new WindowCloseCoordinator(
            h.Lifecycle,
            h.Settings,
            new OrderRecordingWindowController(order),
            new ThrowingNoticePresenter(order),
            enterBackground => enterBackground(),
            NullLogger<WindowCloseCoordinator>.Instance);

        var thrown = Record.Exception(() => coordinator.HandleCloseRequest());

        Assert.NotNull(thrown); // the throw escapes to the window handler, which contains it
        Assert.Equal(["hide", "notice"], order); // and the hide already happened first
    }

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
