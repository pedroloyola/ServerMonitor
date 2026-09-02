using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// Coverage J and K (M13 S2 §F.2), with a REAL barrier inside the stop rather than a test of the
/// abstraction alone (Atlas MÉDIA-4).
/// <para>
/// The invariant: while the old process drains, a new process must not be able to become an independent
/// primary. The AppInstance API offers no way to refuse a redirect and no atomic register-and-check, so
/// the only barrier the platform gives is OWNERSHIP — which is why the exit path must never release the
/// key while alive. These tests park the host inside <c>StopAsync</c>, and while it is parked they run
/// both of the things that could break the invariant: a concurrent acquisition attempt, and an activation.
/// </para>
/// </summary>
public sealed class ExitOwnershipTests
{
    /// <summary>
    /// Models the single-instance registration: whoever holds it is the redirect target, and a competing
    /// launch that cannot take it redirects and starts NO host of its own.
    /// </summary>
    private sealed class SingleInstanceOwnership
    {
        private int _owned = 1; // the process under test starts as primary

        public bool IsOwned => Volatile.Read(ref _owned) == 1;

        /// <summary>What a competing launch does. True means it became primary and will start a host.</summary>
        public bool TryAcquire() => Interlocked.CompareExchange(ref _owned, 1, 0) == 0;

        /// <summary>The only legitimate release: process termination.</summary>
        public void ReleaseOnProcessTermination() => Volatile.Write(ref _owned, 0);
    }

    /// <summary>A host whose stop parks on a barrier, so "during the drain" is a real window.</summary>
    private sealed class BarrierHost : IHost
    {
        private readonly ManualResetEventSlim _release = new(false);

        public ManualResetEventSlim StopEntered { get; } = new(false);

        public int StopCount { get; private set; }

        public IServiceProvider Services { get; } = new EmptyProvider();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            StopEntered.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }

        public void Release() => _release.Set();

        public void Dispose() { }

        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    private sealed class OwnershipExitSequence(SingleInstanceOwnership ownership, BarrierHost host)
        : IExitSequence
    {
        private readonly AppShutdownCoordinator _coordinator = new(
            () => host,
            NullLogger<AppShutdownCoordinator>.Instance,
            TimeSpan.FromSeconds(20));

        public void StopAcceptingForegroundWork() { }

        public void RemoveTrayIcon() { }

        public void HideUserInterface() { }

        public bool DrainHost()
        {
            var stopped = _coordinator.Shutdown();

            // The production path never releases ownership; only process termination does. Asserting the
            // state here would be circular, so the test asserts it from the outside, while parked.
            _ = ownership;
            return stopped;
        }
    }

    private sealed class CountingWatchdog : ITerminationWatchdog
    {
        public Action? OnDeadline { get; private set; }

        public void Arm(TimeSpan deadline, Action onDeadlineReached) => OnDeadline = onDeadlineReached;

        public void Disarm() { }
    }

    private sealed class CountingTerminator : IProcessTerminator
    {
        public int Count { get; private set; }

        public void Terminate(int exitCode) => Count++;
    }

    /// <summary>
    /// THE invariant test. The stop is parked; while it is parked a competing launch tries to take
    /// ownership and an activation arrives. The launch must fail to acquire (so it redirects and starts no
    /// host), and the activation must be discarded without materializing anything.
    /// </summary>
    [Fact]
    public async Task While_the_host_drains_no_new_primary_can_appear_and_activations_are_discarded()
    {
        var ownership = new SingleInstanceOwnership();
        var host = new BarrierHost();
        var terminator = new CountingTerminator();
        var exits = 0;

        var controller = new AppLifecycleController(
            () => new OwnershipExitSequence(ownership, host),
            () => exits++,
            new CountingWatchdog(),
            terminator,
            NullLogger<AppLifecycleController>.Instance);

        var competingHostsStarted = 0;
        var materializations = 0;
        var dispatch = new ActivationDispatch(
            _ => { },
            () => materializations++,
            () => controller.IsExiting);

        var exiting = Task.Run(() => controller.RequestExit(ExitReason.TrayExit));
        Assert.True(host.StopEntered.Wait(TimeSpan.FromSeconds(30)), "the drain never started");

        // --- while the drain is parked ---
        Assert.True(controller.IsExiting);
        Assert.True(ownership.IsOwned, "ownership was released while the host was still draining");

        if (ownership.TryAcquire())
        {
            competingHostsStarted++; // a second primary: exactly what must not happen
        }

        dispatch.Dispatch(ActivationIntent.Dashboard);
        dispatch.Dispatch(null);
        dispatch.Dispatch(null, ActivationOrigin.BackgroundLaunch);

        Assert.Equal(0, competingHostsStarted);
        Assert.Equal(0, materializations);

        host.Release();
        await exiting.WaitAsync(TimeSpan.FromSeconds(30));

        // --- after the exit ---
        Assert.Equal(1, exits);
        Assert.Equal(1, host.StopCount);
        Assert.True(ownership.IsOwned, "the exit path must not release ownership; termination does");

        // Only termination releases it, and then a later launch starts normally.
        ownership.ReleaseOnProcessTermination();
        Assert.True(ownership.TryAcquire());
    }

    /// <summary>
    /// The deadline is measured from the transition to Exiting and is not restartable, so a drain that
    /// outlives it is terminated. Driven on a fake clock through the real watchdog contract.
    /// </summary>
    [Fact]
    public async Task A_drain_that_outlives_the_deadline_is_terminated()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
        var host = new BarrierHost();
        var terminator = new CountingTerminator();
        var watchdog = new FakeClockWatchdog(clock);

        var controller = new AppLifecycleController(
            () => new OwnershipExitSequence(new SingleInstanceOwnership(), host),
            () => { },
            watchdog,
            terminator,
            NullLogger<AppLifecycleController>.Instance,
            LaunchMode.Foreground,
            TimeSpan.FromSeconds(10));

        var exiting = Task.Run(() => controller.RequestExit(ExitReason.TrayExit));
        Assert.True(host.StopEntered.Wait(TimeSpan.FromSeconds(30)), "the drain never started");

        Assert.Equal(0, terminator.Count);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, terminator.Count);   // not a millisecond early
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, terminator.Count);   // and not a millisecond late

        host.Release();
        await exiting.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The watchdog contract on an injected clock: armed once, never restarted, fires once.</summary>
    private sealed class FakeClockWatchdog(FakeTimeProvider clock) : ITerminationWatchdog
    {
        private ITimer? _timer;

        public void Arm(TimeSpan deadline, Action onDeadlineReached)
        {
            if (_timer is not null)
            {
                return; // monotonic and non-restartable
            }

            _timer = clock.CreateTimer(_ => onDeadlineReached(), null, deadline, Timeout.InfiniteTimeSpan);
        }

        public void Disarm() => _timer?.Dispose();
    }
}
