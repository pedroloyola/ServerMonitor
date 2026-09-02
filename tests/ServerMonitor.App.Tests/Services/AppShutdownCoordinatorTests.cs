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
        Assert.True(coordinator.Shutdown());

        Assert.Equal(1, host.StopCount);

        // Disposal is deliberately off the critical path now (M13 S2 §F.3): it is synchronous and
        // unbounded, so it runs on a background thread that the exit never waits for. It still happens
        // exactly once, just not before Shutdown returns.
        Assert.True(SpinWait.SpinUntil(() => host.DisposeCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, host.DisposeCount);
    }

    /// <summary>
    /// Atlas ALTA-2: the 5 s bound used to cover only <c>StopAsync</c>, and a timeout then handed the host
    /// to a deferred, unbounded <c>Dispose</c> — which could hold the process in a dying state forever,
    /// the zombie by another route. A stop that does not finish now means the services are still running,
    /// so disposal is not attempted AT ALL, then or later.
    /// </summary>
    [Fact]
    public void NonCooperativeStop_ReturnsWithinBoundAndNeverDisposes()
    {
        var host = new BlockingHost();
        var timeout = TimeSpan.FromMilliseconds(50);
        var coordinator = new AppShutdownCoordinator(
            () => host,
            NullLogger<AppShutdownCoordinator>.Instance,
            timeout);
        var stopwatch = Stopwatch.StartNew();

        var stopped = coordinator.Shutdown();

        stopwatch.Stop();
        Assert.False(stopped); // the caller is told, and exits anyway
        Assert.True(host.StopStarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, host.DisposeCount);

        host.ReleaseStop();

        // Even once the stop finally completes, disposal must NOT be started behind the exit's back.
        Assert.False(
            SpinWait.SpinUntil(() => host.DisposeCount > 0, TimeSpan.FromMilliseconds(500)),
            "a stop that timed out must never lead to an unbounded disposal");
        Assert.Equal(1, host.StopCount);
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
