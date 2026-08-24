using ServerMonitor.Collectors.Linux;
using ServerMonitor.Collectors.Tests.Linux.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Linux;

namespace ServerMonitor.Collectors.Tests.Linux;

public sealed class LinuxMetricsCollectorTests
{
    private static readonly LinuxMetricsRawData FullRawData = new()
    {
        FirstCpuStat = "cpu  100 0 0 800 0 0 0 0 0 0\n",
        SecondCpuStat = "cpu  150 0 0 850 0 0 0 0 0 0\n",
        MemInfo = "MemTotal: 16000000 kB\nMemAvailable: 10000000 kB\n",
        RootFileSystem = "Filesystem     1-blocks      Used   Available Capacity Mounted on\n" +
                          "/dev/sda1    21474836480 10737418240 10200547328      52% /\n",
        Uptime = "12345.67 98765.43\n",
        Hostname = "web-01\n",
        OsRelease = "NAME=\"Ubuntu\"\nVERSION_ID=\"22.04\"\n"
    };

    private static Server LinuxServer(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "web-01",
        Host = "10.0.0.5",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private static LinuxMetricsRemoteResult SuccessResult(LinuxMetricsRawData data) => new()
    {
        ConnectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            DetectedOperatingSystem = ServerOperatingSystem.Linux
        },
        Data = data
    };

    [Fact]
    public async Task CollectAsync_NonLinuxServer_FailsWithoutCallingRemoteSource()
    {
        var remote = new FakeLinuxMetricsRemoteSource();
        var collector = new LinuxMetricsCollector(remote);
        var server = LinuxServer() with { OperatingSystem = ServerOperatingSystem.MacOS };

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
        var remote = new FakeLinuxMetricsRemoteSource();
        var collector = new LinuxMetricsCollector(remote);
        var server = LinuxServer() with { OperatingSystem = operatingSystem };

        var result = await collector.CollectAsync(server);

        Assert.Equal(MetricsCollectionErrorCode.UnsupportedOperatingSystem, result.ErrorCode);
        Assert.Equal(0, remote.CallCount);
    }

    [Fact]
    public async Task CollectAsync_AlreadyCancelledToken_FailsWithoutCallingRemoteSource()
    {
        var remote = new FakeLinuxMetricsRemoteSource();
        var collector = new LinuxMetricsCollector(remote);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await collector.CollectAsync(LinuxServer(), cts.Token);

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(0, remote.CallCount);
    }

    [Fact]
    public async Task CollectAsync_RemoteSourceThrowsOperationCanceled_MapsToCancelled()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_RemoteSourceThrowsUnexpectedException_MapsToUnexpectedWithoutCrashing()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            ExceptionToThrow = new InvalidOperationException("boom")
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.Unexpected, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_RemoteSourceThrowsDuringDataAccess_MapsToUnexpectedWithoutCrashing()
    {
        // Simulates a defect surfacing deep in result processing rather
        // than in the remote call itself: the collector must not propagate it.
        var remote = new FakeLinuxMetricsRemoteSource
        {
            ExceptionToThrow = new NullReferenceException()
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

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
        var remote = new FakeLinuxMetricsRemoteSource
        {
            Result = new LinuxMetricsRemoteResult { ConnectionResult = connectionResult }
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, result.ErrorCode);
        Assert.Same(connectionResult, result.ConnectionResult);
    }

    [Fact]
    public async Task CollectAsync_InvalidConfiguration_PreservesTypedMetricsError()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            Result = new LinuxMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult
                {
                    State = ServerConnectionState.Error,
                    ErrorCode = SshConnectionErrorCode.InvalidConfiguration
                }
            }
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.Equal(MetricsCollectionErrorCode.InvalidConfiguration, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_ConnectionTimedOut_MapsToTimedOut()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            Result = new LinuxMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult
                {
                    State = ServerConnectionState.TimedOut,
                    ErrorCode = SshConnectionErrorCode.ConnectionTimedOut
                }
            }
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.Equal(MetricsCollectionErrorCode.TimedOut, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_ConnectionCancelledState_MapsToCancelled()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            Result = new LinuxMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult
                {
                    State = ServerConnectionState.Cancelled,
                    ErrorCode = SshConnectionErrorCode.Cancelled
                }
            }
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_ConnectedButNoData_MapsToNoMetricsAvailable()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            Result = new LinuxMetricsRemoteResult
            {
                ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
                Data = null
            }
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.NoMetricsAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_AllSourcesUnparseable_MapsToNoMetricsAvailable()
    {
        var remote = new FakeLinuxMetricsRemoteSource
        {
            Result = SuccessResult(new LinuxMetricsRawData())
        };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.NoMetricsAvailable, result.ErrorCode);
    }

    [Fact]
    public async Task CollectAsync_FullData_BuildsCompleteSnapshot()
    {
        var serverId = Guid.NewGuid();
        var remote = new FakeLinuxMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var fixedNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var collector = new LinuxMetricsCollector(remote, new FixedTimeProvider(fixedNow));

        var result = await collector.CollectAsync(LinuxServer(serverId));

        Assert.True(result.IsSuccess);
        var snapshot = result.Snapshot!;
        Assert.Equal(serverId, snapshot.ServerId);
        Assert.Equal(fixedNow, snapshot.CollectedAt);
        Assert.Equal(50d, snapshot.CpuUsagePercent!.Value, precision: 6);
        Assert.Equal(16000000L * 1024, snapshot.MemoryTotalBytes);
        Assert.Equal(6000000L * 1024, snapshot.MemoryUsedBytes);
        Assert.Equal(21474836480L, snapshot.DiskTotalBytes);
        Assert.Equal(10737418240L, snapshot.DiskUsedBytes);
        Assert.Equal(TimeSpan.FromSeconds(12345.67), snapshot.Uptime);
        Assert.Equal("web-01", snapshot.Hostname);
        Assert.Equal("Ubuntu", snapshot.OperatingSystemName);
        Assert.Equal("22.04", snapshot.OperatingSystemVersion);
    }

    [Fact]
    public async Task CollectAsync_PartialData_KeepsUnparseableFieldsNullWithoutFailingWholeResult()
    {
        var data = FullRawData with { MemInfo = null, RootFileSystem = "garbage" };
        var remote = new FakeLinuxMetricsRemoteSource { Result = SuccessResult(data) };
        var collector = new LinuxMetricsCollector(remote);

        var result = await collector.CollectAsync(LinuxServer());

        Assert.True(result.IsSuccess);
        var snapshot = result.Snapshot!;
        Assert.Null(snapshot.MemoryTotalBytes);
        Assert.Null(snapshot.MemoryUsedBytes);
        Assert.Null(snapshot.DiskTotalBytes);
        Assert.NotNull(snapshot.Hostname);
        Assert.NotNull(snapshot.CpuUsagePercent);
    }

    [Fact]
    public async Task CollectAsync_PassesConfiguredOptionsToRemoteSource()
    {
        var remote = new FakeLinuxMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var options = new LinuxMetricsCollectorOptions
        {
            CpuSampleInterval = TimeSpan.FromMilliseconds(500),
            Timeout = TimeSpan.FromSeconds(10)
        };
        var collector = new LinuxMetricsCollector(remote, options: options);

        await collector.CollectAsync(LinuxServer());

        Assert.Equal(TimeSpan.FromMilliseconds(500), remote.LastCpuSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), remote.LastTimeout);
    }

    [Fact]
    public async Task CollectAsync_DefaultOptions_Use500MsSampleIntervalAndTenSecondTimeout()
    {
        var remote = new FakeLinuxMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var collector = new LinuxMetricsCollector(remote);

        await collector.CollectAsync(LinuxServer());

        Assert.Equal(TimeSpan.FromMilliseconds(500), remote.LastCpuSampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), remote.LastTimeout);
    }

    [Fact]
    public async Task CollectAsync_NullServer_ThrowsArgumentNullException()
    {
        var remote = new FakeLinuxMetricsRemoteSource();
        var collector = new LinuxMetricsCollector(remote);

        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(null!));
    }

    [Fact]
    public async Task CollectAsync_PropagatesCancellationTokenToRemoteSource()
    {
        var remote = new FakeLinuxMetricsRemoteSource { Result = SuccessResult(FullRawData) };
        var collector = new LinuxMetricsCollector(remote);
        using var cts = new CancellationTokenSource();

        await collector.CollectAsync(LinuxServer(), cts.Token);

        Assert.Equal(cts.Token, remote.LastCancellationToken);
    }
}
