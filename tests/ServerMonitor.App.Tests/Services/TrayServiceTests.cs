using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;

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
    public async Task RepeatedExit_RequestsAuthoritativeWindowCloseOnce()
    {
        var harness = new Harness();
        await harness.Service.StartAsync(default);

        harness.Icon.RaiseExit();
        harness.Icon.RaiseExit();

        Assert.Equal(1, harness.Window.CloseCount);
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

        public Harness()
        {
            Service = new TrayService(
                Icon,
                Window,
                Refresh,
                Alert,
                NullLogger<TrayService>.Instance);
        }
    }

    private sealed class FakeTrayIcon : ITrayIconAdapter
    {
        public event EventHandler? OpenRequested;
        public event EventHandler? RefreshAllRequested;
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

        public void Attach(Window window) { }
        public void HideForMinimize() => HideCount++;
        public void RestoreAndActivate() => RestoreCount++;
        public void OpenSettings() => SettingsCount++;
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
