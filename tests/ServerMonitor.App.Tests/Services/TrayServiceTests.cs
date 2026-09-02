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

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));

        public Harness(int maxIconAttempts = 1)
        {
            Service = new TrayService(
                Icon,
                Window,
                Refresh,
                Alert,
                Lifecycle,
                NullLogger<TrayService>.Instance,
                Clock,
                maxIconAttempts,
                TimeSpan.FromSeconds(1));
        }
    }

    private sealed class FakeTrayIcon : ITrayIconAdapter
    {
        public event EventHandler? OpenRequested;
        public event EventHandler? RefreshAllRequested;
        public event EventHandler? ToggleCompactRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

        public int StartCount { get; private set; }
        public int SynchronousStopCount { get; private set; }
        public int AsyncStopCount { get; private set; }

        public void Start() => StartCount++;
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
        public int SettingsCount { get; private set; }
        public int CloseCount { get; private set; }
        public int BeginShutdownCount { get; private set; }

        public int ToggleCompactCount { get; private set; }

        public void Attach(Window window) { }

        public bool IsMaterialized => true;

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideToBackground() => HideToBackgroundCount++;

        public int HideToBackgroundCount { get; private set; }

        public void OpenBackgroundSettings() => OpenBackgroundSettingsCount++;

        public int OpenBackgroundSettingsCount { get; private set; }
        public void HideForMinimize() => HideCount++;
        public void RestoreAndActivate() => RestoreCount++;
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
