using ServerMonitor.Collectors.Linux;
using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Collectors.Tests.Linux.Fakes;
using ServerMonitor.Collectors.Tests.MacOS.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Collectors.Tests.Linux;

/// <summary>
/// M16.1: the two-phase collection seen from the collectors. The built-in result goes out once, through the callback,
/// before the session's second phase; a failed connection or built-in phase never calls back and is returned as before.
/// </summary>
public sealed class LinuxMetricsCollectorPhasesTests
{
    private static readonly LinuxMetricsRawData RawData = new()
    {
        MemInfo = "MemTotal: 16000000 kB\nMemAvailable: 10000000 kB\n",
        Hostname = "web-01\n"
    };

    private static Server LinuxServer() => new()
    {
        Id = Guid.NewGuid(),
        Name = "web-01",
        Host = "10.0.0.5",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private static LinuxMetricsRemoteResult Success() => new()
    {
        ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
        Data = RawData
    };

    private static LinuxMetricsRemoteResult ConnectionFailure() => new()
    {
        ConnectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.AuthenticationFailed,
            ErrorCode = SshConnectionErrorCode.AuthenticationFailed
        }
    };

    [Fact]
    public async Task CallbackPath_PublishesOnceBeforeThePhaseTwoEnds_AndReturnsTheSameResult()
    {
        var remote = new PhasedLinuxSource { Result = Success() };
        var collector = new LinuxMetricsCollector(remote);
        var published = new List<ServerMetricsCollectionResult>();
        var phases = new MetricsCollectionPhases(result =>
        {
            Assert.False(remote.PhaseTwoFinished);
            published.Add(result);
        }, Probes: null);

        var returned = await collector.CollectAsync(LinuxServer(), phases);

        var only = Assert.Single(published);
        Assert.True(only.IsSuccess);
        Assert.Same(only, returned);
        Assert.True(remote.PhaseTwoFinished);
    }

    [Fact]
    public async Task ALoadOnlyAnswer_IsPublishedInPhaseOne()
    {
        // Load average alone is a source (HasAnySource): it succeeds, so phase 1 publishes and phase 2 may run.
        var remote = new PhasedLinuxSource
        {
            Result = Success() with { Data = new LinuxMetricsRawData { LoadAverage = "0.10 0.20 0.30 1/100 4242\n" } }
        };
        var collector = new LinuxMetricsCollector(remote);
        var published = new List<ServerMetricsCollectionResult>();

        var returned = await collector.CollectAsync(LinuxServer(), new MetricsCollectionPhases(published.Add, Probes: null));

        Assert.Same(Assert.Single(published), returned);
        Assert.True(returned.IsSuccess);
    }

    [Fact]
    public async Task FailurePath_NeverCallsBack_AndReturnsTheMappedFailure()
    {
        var remote = new PhasedLinuxSource { Result = ConnectionFailure() };
        var collector = new LinuxMetricsCollector(remote);
        var calls = 0;

        var returned = await collector.CollectAsync(
            LinuxServer(), new MetricsCollectionPhases(_ => calls++, Probes: null));

        Assert.Equal(0, calls);
        Assert.False(returned.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, returned.ErrorCode);
    }

    [Fact]
    public async Task NoSourceAnswered_NeverCallsBack()
    {
        var remote = new PhasedLinuxSource
        {
            Result = Success() with { Data = new LinuxMetricsRawData() }
        };
        var collector = new LinuxMetricsCollector(remote);
        var calls = 0;

        var returned = await collector.CollectAsync(
            LinuxServer(), new MetricsCollectionPhases(_ => calls++, Probes: null));

        Assert.Equal(0, calls);
        Assert.Equal(MetricsCollectionErrorCode.NoMetricsAvailable, returned.ErrorCode);
    }

    [Fact]
    public async Task TheReservationIsHandedToTheSession()
    {
        var scheduler = new RemoteProbeScheduler([new OneProbe()], new HistoryGeneration());
        var reservation = scheduler.TryReserve(Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(reservation);
        var remote = new PhasedLinuxSource { Result = Success() };
        var collector = new LinuxMetricsCollector(remote);

        await collector.CollectAsync(LinuxServer(), new MetricsCollectionPhases(_ => { }, reservation));

        Assert.Same(reservation, remote.LastProbes);
        reservation.Complete();
    }

    [Fact]
    public async Task ACallbackThatThrows_DoesNotFailTheCollection()
    {
        // The isolation is the production one (the interface's phased path), not the fake's: the fake source here only
        // implements the one-phase call, so a callback exception that got through would make the collector return
        // Unexpected.
        var remote = new FakeLinuxMetricsRemoteSource { Result = Success() };
        var collector = new LinuxMetricsCollector(remote);
        var attempts = 0;

        var returned = await collector.CollectAsync(
            LinuxServer(),
            new MetricsCollectionPhases(_ =>
            {
                attempts++;
                throw new InvalidOperationException();
            }, Probes: null));

        Assert.Equal(1, attempts);
        Assert.True(returned.IsSuccess);
        Assert.Equal(1, remote.CallCount);
    }

    [Fact]
    public async Task OnePhaseSource_DefaultCallsBackOnceAfterTheSession()
    {
        // A source without a second phase (the interface default): the callback still fires, once.
        var remote = new FakeLinuxMetricsRemoteSource { Result = Success() };
        var collector = new LinuxMetricsCollector(remote);
        var published = new List<ServerMetricsCollectionResult>();

        var returned = await collector.CollectAsync(
            LinuxServer(), new MetricsCollectionPhases(published.Add, Probes: null));

        Assert.Same(Assert.Single(published), returned);
        Assert.Equal(1, remote.CallCount);
    }

    [Fact]
    public async Task OnePhaseSource_FailureNeverCallsBack()
    {
        var remote = new FakeLinuxMetricsRemoteSource { Result = ConnectionFailure() };
        var collector = new LinuxMetricsCollector(remote);
        var calls = 0;

        var returned = await collector.CollectAsync(
            LinuxServer(), new MetricsCollectionPhases(_ => calls++, Probes: null));

        Assert.Equal(0, calls);
        Assert.False(returned.IsSuccess);
    }

    [Fact]
    public async Task MacOs_CallbackPath_PublishesOnce()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = new MacOsMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
                Data = new MacOsMetricsRawData { Hostname = "mac-mini\n" }
            }
        };
        var collector = new MacOsMetricsCollector(remote);
        var published = new List<ServerMetricsCollectionResult>();
        var server = LinuxServer() with { OperatingSystem = ServerOperatingSystem.MacOS };

        var returned = await collector.CollectAsync(server, new MetricsCollectionPhases(published.Add, Probes: null));

        Assert.Same(Assert.Single(published), returned);
    }

    [Fact]
    public async Task MacOs_FailurePath_NeverCallsBack()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = new MacOsMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult
                {
                    State = ServerConnectionState.AuthenticationFailed,
                    ErrorCode = SshConnectionErrorCode.AuthenticationFailed
                }
            }
        };
        var collector = new MacOsMetricsCollector(remote);
        var calls = 0;
        var server = LinuxServer() with { OperatingSystem = ServerOperatingSystem.MacOS };

        var returned = await collector.CollectAsync(server, new MetricsCollectionPhases(_ => calls++, Probes: null));

        Assert.Equal(0, calls);
        Assert.False(returned.IsSuccess);
    }

    /// <summary>A source with a real second phase: calls back on success, then "runs probes", then returns.</summary>
    private sealed class PhasedLinuxSource : ILinuxMetricsRemoteSource
    {
        public required LinuxMetricsRemoteResult Result { get; init; }

        public bool PhaseTwoFinished { get; private set; }

        public RemoteProbeReservation? LastProbes { get; private set; }

        public Task<LinuxMetricsRemoteResult> CollectAsync(
            Server server,
            TimeSpan cpuSampleInterval,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The phased path must be used.");

        public async Task<LinuxMetricsRemoteResult> CollectAsync(
            Server server,
            TimeSpan cpuSampleInterval,
            TimeSpan timeout,
            Action<LinuxMetricsRemoteResult> onBaseResult,
            RemoteProbeReservation? probes,
            CancellationToken cancellationToken = default)
        {
            LastProbes = probes;
            if (Result.IsSuccess)
            {
                // Not caught here, so no test can pass on the fake's own isolation.
                onBaseResult(Result);
            }

            await Task.Yield();
            PhaseTwoFinished = true;
            return Result;
        }
    }

    private sealed class OneProbe : IRemoteProbe
    {
        public string Id => "test.probe";

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command => "true";

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }
}
