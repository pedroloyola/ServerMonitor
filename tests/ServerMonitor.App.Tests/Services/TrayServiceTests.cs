using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.Services;

public sealed class TrayServiceTests
{
    [Fact]
    public async Task RepeatedStart_CreatesOneIconAndOneHandlerSet()
    {
        var harness = new Harness();

        await harness.Service.StartAsync(default);
        await harness.Service.StartAsync(default);
        harness.Icon.RaiseOpen();

        Assert.Equal(1, harness.Icon.StartCount);
        Assert.Equal(1, harness.Window.RestoreCount);
    }

    [Fact]
    public async Task CommandsUseWindowAndRefreshCoordinators()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);

        harness.Service.HandleWindowMinimized();
        harness.Icon.RaiseOpen();
        harness.Icon.RaiseSettings();
        harness.Icon.RaiseRefreshAll();
        await harness.Refresh.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, harness.Window.HideCount);
        Assert.Equal(1, harness.Window.RestoreCount);
        Assert.Equal(1, harness.Window.SettingsCount);
        Assert.Equal(1, harness.Refresh.RefreshCount);
    }

    [Fact]
    public async Task ToggleCompact_FromTray_TogglesModeOnTheOneWindow()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);

        harness.Icon.RaiseToggleCompact();

        Assert.Equal(1, harness.Window.ToggleCompactCount);
        // The tray never creates a second window; it only asks the controller to toggle the one.
        Assert.Equal(0, harness.Window.RestoreCount);
    }

    [Fact]
    public async Task RepeatedExit_RequestsTheAuthoritativeExitOnce()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);

        harness.Icon.RaiseExit();
        harness.Icon.RaiseExit();

        // "Sair do ServerAlyzer" no longer closes the window and rides Window.Closed (M13 S2 §C): it
        // calls the one authoritative exit, which is what makes the headless exit possible at all.
        Assert.Equal(1, harness.Lifecycle.ExitRequests);
        Assert.Equal(ExitReason.TrayExit, Assert.Single(harness.Lifecycle.ExitReasons));
        Assert.Equal(0, harness.Window.CloseCount);
    }

    [Fact]
    public async Task PrepareForShutdown_DisposesIconSynchronouslyAndIsIdempotent()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);

        harness.Service.PrepareForShutdown();
        harness.Service.PrepareForShutdown();
        await harness.Service.StopAsync(default);
        harness.Icon.RaiseOpen();
        harness.Service.HandleWindowMinimized();

        Assert.Equal(1, harness.Icon.SynchronousStopCount);
        Assert.Equal(0, harness.Icon.AsyncStopCount);
        Assert.Equal(1, harness.Window.BeginShutdownCount);
        Assert.Equal(1, harness.Alert.BeginShutdownCount);
        Assert.Equal(1, harness.Refresh.BeginShutdownCount);
        Assert.Equal(1, harness.Refresh.StopCount);
        Assert.Equal(0, harness.Window.RestoreCount);
        Assert.Equal(0, harness.Window.HideCount);
    }

    [Fact]
    public async Task HostStopWithoutWindowClose_UsesAsyncTrayCleanup()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);

        await harness.Service.StopAsync(default);
        await harness.Service.StopAsync(default);

        Assert.Equal(0, harness.Icon.SynchronousStopCount);
        Assert.Equal(1, harness.Icon.AsyncStopCount);
        Assert.Equal(1, harness.Refresh.StopCount);
    }

    private sealed class Harness
    {
        public FakeTrayIcon Icon { get; } = new();

        public FakeWindowController Window { get; } = new();

        public FakeRefreshAllCoordinator Refresh { get; } = new();

        public FakeAlertCoordinator Alert { get; } = new();

        public TrayService Service { get; }

        public FakeAppLifecycleController Lifecycle { get; } = new();

        public BackgroundDegradationNotice Degradation { get; } = new();

        /// <summary>Order of the two user-visible effects of a degradation.</summary>
        public List<string> Order { get; } = new();

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));

        public Harness(int maxIconAttempts = 1)
        {
            Degradation.Changed += (_, _) => Order.Add("degraded");
            Window.Restored += () => Order.Add("restore");
            Service = new TrayService(
                Icon,
                Window,
                Refresh,
                Alert,
                Lifecycle,
                Degradation,
                NullLogger<TrayService>.Instance,
                Clock,
                maxIconAttempts,
                TimeSpan.FromSeconds(1));
        }
    }

    // ---------------------------------------------------------------- M13 S2 §K: the only way out

    /// <summary>
    /// Vigil C2. The icon used to be fatal: a failure rethrew out of StartAsync and killed the app. In
    /// headless that is a process with no monitoring at all; continuing silently would be a process the
    /// user cannot stop. Neither is acceptable, so startup survives and the app degrades instead.
    /// </summary>
    [Fact]
    public async Task A_failing_tray_icon_does_not_abort_startup()
    {
        var harness = new Harness();
        harness.Icon.ThrowOnStart = true;

        var thrown = await Record.ExceptionAsync(() => harness.Service.StartAsync(default));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task A_failing_tray_icon_is_retried_before_giving_up()
    {
        var harness = new Harness(maxIconAttempts: 3);
        harness.Icon.ThrowOnStart = true;

        var start = harness.Service.StartAsync(default);
        // The retry delay is on the injected clock, so the wait is deterministic rather than timed.
        while (!start.IsCompleted)
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await start;
        Assert.Equal(3, harness.Icon.StartAttempts);
    }

    /// <summary>
    /// With no icon and a window available, the app surfaces the window and closing it becomes a true
    /// exit for the session — there is always at least one way out.
    /// </summary>
    [Fact]
    public async Task Without_an_icon_the_app_falls_back_to_a_visible_window()
    {
        var harness = new Harness();
        harness.Icon.ThrowOnStart = true;

        await harness.Service.StartAsync(default);

        Assert.True(harness.Service.ExitAffordanceDegraded);
        // No icon means BACKGROUND is no longer a legitimate state: the close button must exit instead.
        Assert.False(harness.Service.CanEnterBackground);
        Assert.Equal(1, harness.Window.RestoreCount);
        Assert.Equal(0, harness.Lifecycle.ExitRequests);

        // §13: surfacing a window nobody asked for is only acceptable WITH the explanation, and the
        // notice must be raised BEFORE the window appears so the InfoBar is already open when the user
        // looks at it.
        Assert.True(harness.Degradation.IsDegraded);
        Assert.Equal(["degraded", "restore"], harness.Order);
    }

    /// <summary>
    /// With no icon and no window either — a headless process whose UI cannot be materialized — the app
    /// exits rather than monitoring where the user has no way to stop it (the A12 zombie by another route).
    /// </summary>
    [Fact]
    public async Task Without_an_icon_and_without_a_window_the_app_exits()
    {
        var harness = new Harness();
        harness.Icon.ThrowOnStart = true;
        harness.Window.CanMaterialize = false;

        await harness.Service.StartAsync(default);

        Assert.Equal(1, harness.Lifecycle.ExitRequests);
        Assert.Equal(ExitReason.NoExitAffordance, Assert.Single(harness.Lifecycle.ExitReasons));
    }

    [Fact]
    public async Task A_healthy_icon_is_a_usable_exit_affordance()
    {
        var harness = new Harness();

        await harness.Service.StartAsync(default);

        Assert.True(harness.Service.CanEnterBackground);
        Assert.False(harness.Service.ExitAffordanceDegraded);
    }

    /// <summary>Vigil C3: the icon is removed by the committed exit, and only then.</summary>
    [Fact]
    public async Task The_icon_is_removed_only_by_the_committed_exit()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);
        Assert.Equal(0, harness.Icon.StopCount);

        harness.Service.RemoveIconForExit();
        harness.Service.RemoveIconForExit();

        Assert.Equal(1, harness.Icon.StopCount);
        Assert.False(harness.Service.CanEnterBackground);
    }

    private sealed class FakeTrayIcon : ITrayIconAdapter
    {
        public event EventHandler? OpenRequested;
        public event EventHandler? RefreshAllRequested;
        public event EventHandler? ToggleCompactRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

        public int StartCount { get; private set; }
        public int StartAttempts { get; private set; }
        public int SynchronousStopCount { get; private set; }
        public int AsyncStopCount { get; private set; }

        /// <summary>Shell_NotifyIcon failing, which is what §K is about.</summary>
        public bool ThrowOnStart { get; set; }

        public int StopCount => SynchronousStopCount;

        public void Start()
        {
            StartAttempts++;
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("the notification area is unavailable");
            }

            StartCount++;
        }
        public void StopSynchronously() => SynchronousStopCount++;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            AsyncStopCount++;
            return Task.CompletedTask;
        }

        public void RaiseOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseToggleCompact() => ToggleCompactRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseRefreshAll() => RefreshAllRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeWindowController : IApplicationWindowController
    {
        public bool IsAttached => true;
        public int HideCount { get; private set; }
        public int RestoreCount { get; private set; }

        /// <summary>False models a headless process whose window cannot be created at all.</summary>
        public bool CanMaterialize { get; set; } = true;
        public int SettingsCount { get; private set; }
        public int CloseCount { get; private set; }
        public int BeginShutdownCount { get; private set; }

        public int ToggleCompactCount { get; private set; }

        public void Attach(Window window) { }

        public bool IsMaterialized => CanMaterialize && RestoreCount > 0;

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideToBackground() => HideToBackgroundCount++;

        public int HideToBackgroundCount { get; private set; }

        public void OpenBackgroundSettings() => OpenBackgroundSettingsCount++;

        public int OpenBackgroundSettingsCount { get; private set; }
        public void HideForMinimize() => HideCount++;
        public event Action? Restored;

        public void RestoreAndActivate()
        {
            RestoreCount++;
            Restored?.Invoke();
        }
        public void OpenSettings() => SettingsCount++;
        public void ToggleCompactMode() => ToggleCompactCount++;
        public void RequestClose() => CloseCount++;
        public void BeginShutdown() => BeginShutdownCount++;
    }

    private sealed class FakeRefreshAllCoordinator : IRefreshAllCoordinator
    {
        public TaskCompletionSource Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshCount { get; private set; }
        public int BeginShutdownCount { get; private set; }
        public int StopCount { get; private set; }

        public Task<RefreshAllResult> RefreshAllAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            Called.TrySetResult();
            return Task.FromResult(new RefreshAllResult(0, 0, 0));
        }

        public void BeginShutdown() => BeginShutdownCount++;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAlertCoordinator : IServerAlertCoordinator
    {
        public int BeginShutdownCount { get; private set; }

        public void BeginShutdown() => BeginShutdownCount++;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
