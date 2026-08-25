using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using System.Diagnostics;

namespace ServerMonitor.App.Tests.Services;

public sealed class AppShutdownCoordinatorTests
{
    [Fact]
    public void RepeatedAndConcurrentShutdown_StopsAndDisposesHostExactlyOnce()
    {
        var host = new RecordingHost();
        var coordinator = new AppShutdownCoordinator(
            () => host,
            NullLogger<AppShutdownCoordinator>.Instance,
            TimeSpan.FromSeconds(1));

        Parallel.For(0, 16, _ => coordinator.Shutdown());
        coordinator.Shutdown();

        Assert.Equal(1, host.StopCount);
        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void NonCooperativeStop_ReturnsWithinBoundAndDefersDisposeUntilStopCompletes()
    {
        var host = new BlockingHost();
        var timeout = TimeSpan.FromMilliseconds(50);
        var coordinator = new AppShutdownCoordinator(
            () => host,
            NullLogger<AppShutdownCoordinator>.Instance,
            timeout);
        var stopwatch = Stopwatch.StartNew();

        coordinator.Shutdown();

        stopwatch.Stop();
        Assert.True(host.StopStarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, host.DisposeCount);

        host.ReleaseStop();

        Assert.True(SpinWait.SpinUntil(
            () => host.DisposeCount == 1,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(1, host.StopCount);
        Assert.Equal(1, host.DisposeCount);
    }

    private sealed class RecordingHost : IHost
    {
        private int _stopCount;
        private int _disposeCount;

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public int StopCount => Volatile.Read(ref _stopCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCount);
            return Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    private sealed class BlockingHost : IHost
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopCount;
        private int _disposeCount;

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ManualResetEventSlim StopStarted { get; } = new(false);

        public int StopCount => Volatile.Read(ref _stopCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCount);
            StopStarted.Set();
            // Deliberately ignore cancellation to exercise the coordinator's hard time bound.
            await _release.Task.ConfigureAwait(false);
        }

        public void ReleaseStop() => _release.TrySetResult();

        public void Dispose() => Interlocked.Increment(ref _disposeCount);

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
