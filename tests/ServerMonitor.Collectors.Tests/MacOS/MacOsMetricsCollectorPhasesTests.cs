using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Collectors.Tests.MacOS;

/// <summary>
/// M16.1: the two-phase collection seen from the macOS collector, mirroring <c>LinuxMetricsCollectorPhasesTests</c>.
/// The built-in result goes out once, through the callback, before the session's second phase; a failed connection or
/// built-in phase never calls back and is returned as before.
/// </summary>
public sealed class MacOsMetricsCollectorPhasesTests
{
    private static readonly MacOsMetricsRawData RawData = new()
    {
        CpuTop = "CPU usage: 3.00% user, 5.00% sys, 92.00% idle\n",
        Hostname = "mac-mini\n"
    };

    private static Server MacServer() => new()
    {
        Id = Guid.NewGuid(),
        Name = "mac-mini",
        Host = "10.0.0.6",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.MacOS,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private static MacOsMetricsRemoteResult Success(MacOsMetricsRawData? data = null) => new()
    {
        ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
        Data = data ?? RawData
    };

    private static MacOsMetricsRemoteResult ConnectionFailure() => new()
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
        var remote = new PhasedMacOsSource { Result = Success() };
        var collector = new MacOsMetricsCollector(remote);
        var published = new List<ServerMetricsCollectionResult>();
        var phases = new MetricsCollectionPhases(result =>
        {
            Assert.False(remote.PhaseTwoFinished);
            published.Add(result);
        }, Probes: null);

        var returned = await collector.CollectAsync(MacServer(), phases);

        var only = Assert.Single(published);
        Assert.True(only.IsSuccess);
        Assert.Same(only, returned);
        Assert.True(remote.PhaseTwoFinished);
    }

    [Fact]
    public async Task ALoadOnlyAnswer_IsPublishedInPhaseOne()
    {
        // Load average alone is a source (HasAnySource): it succeeds, so phase 1 publishes and phase 2 may run.
        var remote = new PhasedMacOsSource { Result = Success(new MacOsMetricsRawData { LoadAverage = "{ 0.10 0.20 0.30 }\n" }) };
        var collector = new MacOsMetricsCollector(remote);
        var published = new List<ServerMetricsCollectionResult>();

        var returned = await collector.CollectAsync(MacServer(), new MetricsCollectionPhases(published.Add, Probes: null));

        Assert.Same(Assert.Single(published), returned);
        Assert.True(returned.IsSuccess);
    }

    [Fact]
    public async Task FailurePath_NeverCallsBack_AndReturnsTheMappedFailure()
    {
        var remote = new PhasedMacOsSource { Result = ConnectionFailure() };
        var collector = new MacOsMetricsCollector(remote);
        var calls = 0;

        var returned = await collector.CollectAsync(
            MacServer(), new MetricsCollectionPhases(_ => calls++, Probes: null));

        Assert.Equal(0, calls);
        Assert.False(returned.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, returned.ErrorCode);
    }

    [Fact]
    public async Task NoSourceAnswered_NeverCallsBack()
    {
        var remote = new PhasedMacOsSource { Result = Success(new MacOsMetricsRawData()) };
        var collector = new MacOsMetricsCollector(remote);
        var calls = 0;

        var returned = await collector.CollectAsync(
            MacServer(), new MetricsCollectionPhases(_ => calls++, Probes: null));

        Assert.Equal(0, calls);
        Assert.Equal(MetricsCollectionErrorCode.NoMetricsAvailable, returned.ErrorCode);
    }

    [Fact]
    public async Task TheReservationIsHandedToTheSession()
    {
        var scheduler = new RemoteProbeScheduler([new OneProbe()], new HistoryGeneration());
        var reservation = scheduler.TryReserve(Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(reservation);
        var remote = new PhasedMacOsSource { Result = Success() };
        var collector = new MacOsMetricsCollector(remote);

        await collector.CollectAsync(MacServer(), new MetricsCollectionPhases(_ => { }, reservation));

        Assert.Same(reservation, remote.LastProbes);
        reservation.Complete();
    }

    [Fact]
    public async Task ACallbackThatThrows_DoesNotFailTheCollection()
    {
        var remote = new PhasedMacOsSource { Result = Success() };
        var collector = new MacOsMetricsCollector(remote);

        var returned = await collector.CollectAsync(
            MacServer(), new MetricsCollectionPhases(_ => throw new InvalidOperationException(), Probes: null));

        Assert.True(returned.IsSuccess);
        Assert.True(remote.PhaseTwoFinished);
    }

    /// <summary>A source with a real second phase: calls back on success, then "runs probes", then returns.</summary>
    private sealed class PhasedMacOsSource : IMacOsMetricsRemoteSource
    {
        public required MacOsMetricsRemoteResult Result { get; init; }

        public bool PhaseTwoFinished { get; private set; }

        public RemoteProbeReservation? LastProbes { get; private set; }

        public Task<MacOsMetricsRemoteResult> CollectAsync(
            Server server,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The phased path must be used.");

        public async Task<MacOsMetricsRemoteResult> CollectAsync(
            Server server,
            TimeSpan timeout,
            Action<MacOsMetricsRemoteResult> onBaseResult,
            RemoteProbeReservation? probes,
            CancellationToken cancellationToken = default)
        {
            LastProbes = probes;
            if (Result.IsSuccess)
            {
                try
                {
                    onBaseResult(Result);
                }
                catch (Exception)
                {
                    // As the session does: a callback that throws is ignored.
                }
            }

            await Task.Yield();
            PhaseTwoFinished = true;
            return Result;
        }
    }

    private sealed class OneProbe : IRemoteProbe
    {
        public string Id => "test.probe";

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.MacOS;

        public string Command => "true";

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }
}
