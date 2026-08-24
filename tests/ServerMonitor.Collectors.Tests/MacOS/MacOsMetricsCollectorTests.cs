using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Collectors.Tests.Linux.Fakes;
using ServerMonitor.Collectors.Tests.MacOS.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Collectors.Tests.MacOS;

public sealed class MacOsMetricsCollectorTests
{
    private const long BootUnixSeconds = 1_700_000_000L;

    private static readonly DateTimeOffset BootInstant =
        DateTimeOffset.FromUnixTimeSeconds(BootUnixSeconds);

    private static readonly MacOsMetricsRawData FullRawData = new()
    {
        CpuTop = "CPU usage: 10.00% user, 5.00% sys, 85.00% idle\n",
        VmStat =
            "Mach Virtual Memory Statistics: (page size of 16384 bytes)\n" +
            "Pages free:                          100000.\n" +
            "Pages active:                        200000.\n" +
            "Pages inactive:                      150000.\n" +
            "Pages speculative:                    50000.\n" +
            "Pages wired down:                    100000.\n" +
            "Pages purgeable:                      10000.\n" +
            "Pages occupied by compressor:         50000.\n",
        PhysicalMemory = "17179869184\n",
        RootFileSystem =
            "Filesystem   1024-blocks       Used  Available Capacity  Mounted on\n" +
            "/dev/disk1s1   488245288  100000000  388245288      52%  /\n",
        BootTime = $"{{ sec = {BootUnixSeconds}, usec = 0 }} Tue Nov 14 22:13:20 2023\n",
        Hostname = "mac-studio\n",
        SwVers = "ProductName:\tmacOS\nProductVersion:\t14.5\nBuildVersion:\t23F79\n"
    };

    private static Server MacServer(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "mac-studio",
        Host = "10.0.0.42",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.MacOS,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private static MacOsMetricsRemoteResult SuccessResult(MacOsMetricsRawData data) => new()
    {
        ConnectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            DetectedOperatingSystem = ServerOperatingSystem.MacOS
        },
        Data = data
    };

    [Fact]
    public async Task CollectAsync_NonMacOsServer_FailsWithoutCallingRemoteSource()
    {
        var remote = new FakeMacOsMetricsRemoteSource();
        var collector = new MacOsMetricsCollector(remote);
        var server = MacServer() with { OperatingSystem = ServerOperatingSystem.Linux };

        var result = await collector.CollectAsync(server);

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.UnsupportedOperatingSystem, result.ErrorCode);
        Assert.Equal(0, remote.CallCount);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Auto)]
    [InlineData(ServerOperatingSystem.Unknown)]
    public async Task CollectAsync_AutoOrUnknownServer_FailsWithoutCallingRemoteSource(
        ServerOperatingSystem operatingSystem)
    {
        var remote = new FakeMacOsMetricsRemoteSource();
        var collector = new MacOsMetricsCollector(remote);
        var server = MacServer() with { OperatingSystem = operatingSystem };

        var result = await collector.CollectAsync(server);

        Assert.Equal(MetricsCollectionErrorCode.UnsupportedOperatingSystem, result.ErrorCode);
        Assert.Equal(0, remote.CallCount);
    }

    [Fact]
    public async Task CollectAsync_AlreadyCancelledToken_FailsWithoutCallingRemoteSource()
    {
        var remote = new FakeMacOsMetricsRemoteSource();
        var collector = new MacOsMetricsCollector(remote);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await collector.CollectAsync(MacServer(), cts.Token);

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(0, remote.CallCount);
    }

    [Fact]
    public async Task CollectAsync_RemoteSourceThrowsOperationCanceled_MapsToCancelled()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_RemoteSourceThrowsUnexpectedException_MapsToUnexpectedWithoutCrashing()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            ExceptionToThrow = new InvalidOperationException("boom")
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.Unexpected, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_ConnectionFailed_MapsToConnectionFailedAndCarriesConnectionResult()
    {
        var connectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.AuthenticationFailed,
            ErrorCode = SshConnectionErrorCode.AuthenticationFailed
        };
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = new MacOsMetricsRemoteResult { ConnectionResult = connectionResult }
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, result.ErrorCode);
        Assert.Same(connectionResult, result.ConnectionResult);
    }

    [Fact]
    public async Task CollectAsync_ConnectionTimedOut_MapsToTimedOut()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = new MacOsMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult
                {
                    State = ServerConnectionState.TimedOut,
                    ErrorCode = SshConnectionErrorCode.ConnectionTimedOut
                }
            }
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.Equal(MetricsCollectionErrorCode.TimedOut, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_InvalidConfiguration_PreservesTypedMetricsError()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = new MacOsMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult
                {
                    State = ServerConnectionState.Error,
                    ErrorCode = SshConnectionErrorCode.InvalidConfiguration
                }
            }
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.Equal(MetricsCollectionErrorCode.InvalidConfiguration, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_ConnectedButNoData_MapsToNoMetricsAvailable()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = new MacOsMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
                Data = null
            }
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.NoMetricsAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_AllSourcesUnparseable_MapsToNoMetricsAvailable()
    {
        var remote = new FakeMacOsMetricsRemoteSource
        {
            Result = SuccessResult(new MacOsMetricsRawData())
        };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.NoMetricsAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_FullData_BuildsCompleteSnapshot()
    {
        var serverId = Guid.NewGuid();
        var remote = new FakeMacOsMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var fixedNow = BootInstant + TimeSpan.FromDays(3);
        var collector = new MacOsMetricsCollector(remote, new FixedTimeProvider(fixedNow));

        var result = await collector.CollectAsync(MacServer(serverId));

        Assert.True(result.IsSuccess);
        var snapshot = result.Snapshot!;
        Assert.Equal(serverId, snapshot.ServerId);
        Assert.Equal(fixedNow, snapshot.CollectedAt);
        Assert.Equal(15d, snapshot.CpuUsagePercent!.Value, precision: 6);
        Assert.Equal(17179869184L, snapshot.MemoryTotalBytes);
        Assert.Equal((200000L + 100000L + 50000L) * 16384L, snapshot.MemoryUsedBytes);
        Assert.Equal(488245288L * 1024, snapshot.DiskTotalBytes);
        Assert.Equal(100000000L * 1024, snapshot.DiskUsedBytes);
        Assert.Equal(52d, snapshot.DiskUsagePercent!.Value, precision: 6);
        Assert.Equal(TimeSpan.FromDays(3), snapshot.Uptime);
        Assert.Equal("mac-studio", snapshot.Hostname);
        Assert.Equal("macOS", snapshot.OperatingSystemName);
        Assert.Equal("14.5", snapshot.OperatingSystemVersion);
    }

    [Fact]
    public async Task CollectAsync_BootTimeInTheFuture_LeavesUptimeNull()
    {
        var remote = new FakeMacOsMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        // "now" precedes boot time: a negative uptime must not be surfaced.
        var fixedNow = BootInstant - TimeSpan.FromMinutes(5);
        var collector = new MacOsMetricsCollector(remote, new FixedTimeProvider(fixedNow));

        var result = await collector.CollectAsync(MacServer());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Snapshot!.Uptime);
    }

    [Fact]
    public async Task CollectAsync_PartialData_KeepsUnparseableFieldsNullWithoutFailingWholeResult()
    {
        var data = FullRawData with { VmStat = null, RootFileSystem = "garbage" };
        var remote = new FakeMacOsMetricsRemoteSource { Result = SuccessResult(data) };
        var collector = new MacOsMetricsCollector(remote);

        var result = await collector.CollectAsync(MacServer());

        Assert.True(result.IsSuccess);
        var snapshot = result.Snapshot!;
        Assert.Null(snapshot.MemoryTotalBytes);
        Assert.Null(snapshot.MemoryUsedBytes);
        Assert.Null(snapshot.DiskTotalBytes);
        Assert.NotNull(snapshot.Hostname);
        Assert.NotNull(snapshot.CpuUsagePercent);
    }

    [Fact]
    public async Task CollectAsync_PassesConfiguredTimeoutToRemoteSource()
    {
        var remote = new FakeMacOsMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var options = new MacOsMetricsCollectorOptions { Timeout = TimeSpan.FromSeconds(20) };
        var collector = new MacOsMetricsCollector(remote, options: options);

        await collector.CollectAsync(MacServer());

        Assert.Equal(TimeSpan.FromSeconds(20), remote.LastTimeout);
    }

    [Fact]
    public async Task CollectAsync_DefaultOptions_UseFifteenSecondTimeout()
    {
        var remote = new FakeMacOsMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var collector = new MacOsMetricsCollector(remote);

        await collector.CollectAsync(MacServer());

        Assert.Equal(TimeSpan.FromSeconds(15), remote.LastTimeout);
    }

    [Fact]
    public async Task CollectAsync_NullServer_ThrowsArgumentNullException()
    {
        var remote = new FakeMacOsMetricsRemoteSource();
        var collector = new MacOsMetricsCollector(remote);

        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(null!));
    }

    [Fact]
    public async Task CollectAsync_PropagatesCancellationTokenToRemoteSource()
    {
        var remote = new FakeMacOsMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var collector = new MacOsMetricsCollector(remote);
        using var cts = new CancellationTokenSource();

        await collector.CollectAsync(MacServer(), cts.Token);

        Assert.Equal(cts.Token, remote.LastCancellationToken);
    }
}
