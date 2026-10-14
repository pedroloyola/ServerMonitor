using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// REL.0B (Atlas finding 2): the stop of a user-started local-network search, driven through the REAL production
/// chain - <see cref="TrayAffordanceLifecycle"/> (the guarded close-to-tray and minimize-to-tray operations) → the
/// hide capability that the REAL <see cref="ApplicationWindowController"/> connects → its hide cores →
/// <see cref="LocalNetworkSearchLifetime"/> → the REAL <see cref="ServerDiscoveryService"/> over a fake browser (no
/// socket). No window and no dispatcher are attached: the stop must not depend on either. The source-text guard in
/// DiscoveryOnDemandTests stays as an advisory second line.
/// </summary>
public sealed class DiscoveryHideStopTests
{
    [Theory]
    [InlineData(TrayGuardedOperation.EnterBackground)]
    [InlineData(TrayGuardedOperation.HideForMinimize)]
    public async Task Close_or_minimize_to_tray_stops_the_running_search_and_unsubscribes(TrayGuardedOperation operation)
    {
        var h = new Harness();
        await using var _ = h.Service;
        await h.Service.StartSearchAsync();
        Assert.True(h.Service.IsSearching);
        Assert.Equal(1, h.Browser.FoundSubscriberCount);

        h.Lifecycle.Perform(operation);

        await WaitUntilAsync(() => !h.Service.IsSearching && h.Browser.StopCount == 1);
        Assert.Equal(0, h.Browser.FoundSubscriberCount);
        Assert.Equal(0, h.Browser.UpdatedSubscriberCount);
        Assert.Equal(0, h.Browser.RemovedSubscriberCount);
        Assert.Equal(DiscoverySearchEnd.Stopped, h.Service.LastSearchEnd);
        Assert.Empty(h.Service.GetDiscovered());
        Assert.Equal(1, h.Browser.StartCount);
        if (operation == TrayGuardedOperation.EnterBackground)
        {
            Assert.Equal(AppLifecycleState.Background, h.AppLifecycle.State);
        }
    }

    [Theory]
    [InlineData(TrayGuardedOperation.EnterBackground)]
    [InlineData(TrayGuardedOperation.HideForMinimize)]
    public async Task Hiding_while_a_start_is_in_flight_ends_with_no_listener(TrayGuardedOperation operation)
    {
        var store = new GatedIgnoredDeviceStore(open: false);
        var h = new Harness(store);
        await using var _ = h.Service;

        var start = h.Service.StartSearchAsync();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        h.Lifecycle.Perform(operation);
        store.Release();
        await start.WaitAsync(TimeSpan.FromSeconds(30));

        await WaitUntilAsync(() => !h.Service.IsSearching && h.Browser.StopCount == h.Browser.StartCount);
        Assert.Equal(1, h.Browser.StartCount);
        Assert.Equal(0, h.Browser.FoundSubscriberCount);
    }

    [Fact]
    public async Task A_refused_background_entry_leaves_the_search_alone_because_the_window_stays()
    {
        // No tray affordance: close-to-tray is refused and the app exits instead (the host then stops discovery);
        // minimize is refused and the window simply stays. Neither path hides, so neither stops here.
        var h = new Harness(state: TrayAffordanceState.Unavailable);
        await using var _ = h.Service;
        await h.Service.StartSearchAsync();

        h.Lifecycle.Perform(TrayGuardedOperation.HideForMinimize);
        await Task.Delay(200);

        Assert.True(h.Service.IsSearching);
        Assert.Equal(0, h.Browser.StopCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        // The stop runs off the calling thread (LocalNetworkSearchLifetime hops to the pool); this bounds the wait.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the search was not stopped through the hide path");
    }

    private sealed class Harness
    {
        public Harness(GatedIgnoredDeviceStore? store = null, TrayAffordanceState state = TrayAffordanceState.Available)
        {
            Browser = new FakeMdnsServiceBrowser();
            Service = new ServerDiscoveryService(Browser, store ?? new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance);
            Controller = new ApplicationWindowController(
                new FakeNavigationService(), new InertModeCoordinator(), Service, NullLogger<ApplicationWindowController>.Instance);
            AppLifecycle = new FakeAppLifecycleController();
            Lifecycle = new TrayAffordanceLifecycle(
                new AvailableSource(state), Controller, new BackgroundDegradationNotice(), AppLifecycle, new SilentPresenter(),
                NullLogger<TrayAffordanceLifecycle>.Instance);

            // Production wiring: the controller creates the capability and pushes it into its two receivers. The exit
            // sequence's collaborators are never reached by these tests.
            var exit = new ExitSequence(null!, null!, null!, null!, Controller, null!, NullLogger<ExitSequence>.Instance);
            Controller.ConnectHideCapability(Lifecycle, exit);
        }

        public FakeMdnsServiceBrowser Browser { get; }

        public ServerDiscoveryService Service { get; }

        public ApplicationWindowController Controller { get; }

        public FakeAppLifecycleController AppLifecycle { get; }

        public TrayAffordanceLifecycle Lifecycle { get; }
    }

    /// <summary>Performs the guarded operation like the real state machine when the affordance is established.</summary>
    private sealed class AvailableSource(TrayAffordanceState state) : ITrayAffordanceSource
    {
        private ITrayGuardedOperations? _operations;

        public event EventHandler? StateChanged { add { } remove { } }

        public TrayAffordanceState State => state;

        public void SetLossConsumer(ITrayLossConsumer consumer) { }

        public void SetGuardedOperations(ITrayGuardedOperations operations) => _operations = operations;

        public void Perform(TrayGuardedOperation operation)
        {
            if (state != TrayAffordanceState.Available)
            {
                _operations!.Refuse(operation);
                return;
            }

            switch (operation)
            {
                case TrayGuardedOperation.EnterBackground:
                    _operations!.EnterBackground();
                    break;
                case TrayGuardedOperation.HideForMinimize:
                    _operations!.HideForMinimize();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }
    }

    private sealed class SilentPresenter : IBackgroundNoticePresenter
    {
        public bool TryShowOnce() => false;
    }

    private sealed class InertModeCoordinator : IWindowModeCoordinator
    {
        public WindowMode CurrentMode => WindowMode.Standard;

        public bool CompactAlwaysOnTop => false;

        public bool IsApplyingBounds => false;

        public event EventHandler<WindowMode>? ModeChanged { add { } remove { } }

        public void Initialize() { }

        public void SwitchTo(WindowMode mode) { }

        public void Toggle() { }

        public void SetCompactAlwaysOnTop(bool enabled) { }

        public void CaptureCurrentBounds() { }

        public void PersistCurrentBounds() { }

        public WindowSizeConstraints CurrentSizeLimits() => WindowSizeConstraints.Compact;

        public bool HoldCompactRestored() => false;
    }
}
