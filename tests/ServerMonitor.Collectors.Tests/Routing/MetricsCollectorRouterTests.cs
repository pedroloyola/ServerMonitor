using ServerMonitor.Collectors.Linux;
using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Collectors.Tests.Linux.Fakes;
using ServerMonitor.Collectors.Tests.MacOS.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Collectors.Tests.Routing;

public sealed class MetricsCollectorRouterTests
{
    private static Server Server(ServerOperatingSystem operatingSystem) => new()
    {
        Id = Guid.NewGuid(),
        Name = "host",
        Host = "10.0.0.9",
        Port = 22,
        Username = "deploy",
        OperatingSystem = operatingSystem,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    private static FakeLinuxMetricsRemoteSource LinuxRemoteWithHostname() => new()
    {
        Result = new LinuxMetricsRemoteResult
        {
            ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
            Data = new LinuxMetricsRawData { Hostname = "linux-host\n" }
        }
    };

    private static FakeMacOsMetricsRemoteSource MacRemoteWithHostname() => new()
    {
        Result = new MacOsMetricsRemoteResult
        {
            ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
            Data = new MacOsMetricsRawData { Hostname = "mac-host\n" }
        }
    };

    private static MetricsCollectorRouter CreateRouter(
        FakeLinuxMetricsRemoteSource linuxRemote,
        FakeMacOsMetricsRemoteSource macRemote,
        FakeSshConnectionService connectionService) =>
        new(
            new LinuxMetricsCollector(linuxRemote),
            new MacOsMetricsCollector(macRemote),
            connectionService);

    [Fact]
    public async Task LinuxServer_RoutesToLinuxCollector_WithoutDetection()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService();
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.True(result.IsSuccess);
        Assert.Equal("linux-host", result.Snapshot!.Hostname);
        Assert.Equal(1, linux.CallCount);
        Assert.Equal(0, mac.CallCount);
        Assert.Equal(0, ssh.DetectCallCount);
    }

    [Fact]
    public async Task MacOsServer_RoutesToMacCollector_WithoutDetection()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService();
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.MacOS));

        Assert.True(result.IsSuccess);
        Assert.Equal("mac-host", result.Snapshot!.Hostname);
        Assert.Equal(1, mac.CallCount);
        Assert.Equal(0, linux.CallCount);
        Assert.Equal(0, ssh.DetectCallCount);
    }

    [Fact]
    public async Task AutoServer_DetectedAsDarwin_RoutesToMacCollector()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService
        {
            DetectionResult = new SshConnectionResult
            {
                State = ServerConnectionState.Connected,
                DetectedOperatingSystem = ServerOperatingSystem.MacOS
            }
        };
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.True(result.IsSuccess);
        Assert.Equal("mac-host", result.Snapshot!.Hostname);
        Assert.Equal(1, ssh.DetectCallCount);
        Assert.Equal(1, mac.CallCount);
        Assert.Equal(0, linux.CallCount);
    }

    [Fact]
    public async Task AutoServer_DetectedAsLinux_RoutesToLinuxCollector()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService
        {
            DetectionResult = new SshConnectionResult
            {
                State = ServerConnectionState.Connected,
                DetectedOperatingSystem = ServerOperatingSystem.Linux
            }
        };
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.True(result.IsSuccess);
        Assert.Equal("linux-host", result.Snapshot!.Hostname);
        Assert.Equal(1, ssh.DetectCallCount);
        Assert.Equal(1, linux.CallCount);
        Assert.Equal(0, mac.CallCount);
    }

    [Fact]
    public async Task AutoServer_DetectionReturnsUnknown_FailsAsUnsupported()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService
        {
            DetectionResult = new SshConnectionResult
            {
                State = ServerConnectionState.Connected,
                DetectedOperatingSystem = ServerOperatingSystem.Unknown
            }
        };
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.UnsupportedOperatingSystem, result.ErrorCode);
        Assert.Equal(0, linux.CallCount);
        Assert.Equal(0, mac.CallCount);
    }

    [Fact]
    public async Task AutoServer_DetectionFails_MapsConnectionErrorAndCarriesResult()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var detection = new SshConnectionResult
        {
            State = ServerConnectionState.AuthenticationFailed,
            ErrorCode = SshConnectionErrorCode.AuthenticationFailed
        };
        var ssh = new FakeSshConnectionService { DetectionResult = detection };
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, result.ErrorCode);
        Assert.Same(detection, result.ConnectionResult);
        Assert.Equal(0, linux.CallCount);
        Assert.Equal(0, mac.CallCount);
    }

    [Fact]
    public async Task AutoServer_DetectionTimesOut_MapsToTimedOut()
    {
        var ssh = new FakeSshConnectionService
        {
            DetectionResult = new SshConnectionResult
            {
                State = ServerConnectionState.TimedOut,
                ErrorCode = SshConnectionErrorCode.ConnectionTimedOut
            }
        };
        var router = CreateRouter(LinuxRemoteWithHostname(), MacRemoteWithHostname(), ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.Equal(MetricsCollectionErrorCode.TimedOut, result.ErrorCode);
    }

    [Fact]
    public async Task AutoServer_DetectionThrowsOperationCanceled_MapsToCancelled()
    {
        var ssh = new FakeSshConnectionService { DetectionException = new OperationCanceledException() };
        var router = CreateRouter(LinuxRemoteWithHostname(), MacRemoteWithHostname(), ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task AutoServer_DetectionThrowsUnexpected_MapsToUnexpectedWithoutCrashing()
    {
        var ssh = new FakeSshConnectionService { DetectionException = new InvalidOperationException("boom") };
        var router = CreateRouter(LinuxRemoteWithHostname(), MacRemoteWithHostname(), ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.Unexpected, result.ErrorCode);
    }

    [Fact]
    public async Task AutoServer_AlreadyCancelledToken_FailsWithoutDetection()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService();
        var router = CreateRouter(linux, mac, ssh);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Auto), cts.Token);

        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(0, ssh.DetectCallCount);
    }

    [Fact]
    public async Task UnknownServer_FailsAsUnsupported_WithoutDetectionOrCollectors()
    {
        var linux = LinuxRemoteWithHostname();
        var mac = MacRemoteWithHostname();
        var ssh = new FakeSshConnectionService();
        var router = CreateRouter(linux, mac, ssh);

        var result = await router.CollectAsync(Server(ServerOperatingSystem.Unknown));

        Assert.Equal(MetricsCollectionErrorCode.UnsupportedOperatingSystem, result.ErrorCode);
        Assert.Equal(0, ssh.DetectCallCount);
        Assert.Equal(0, linux.CallCount);
        Assert.Equal(0, mac.CallCount);
    }

    [Fact]
    public async Task NullServer_ThrowsArgumentNullException()
    {
        var router = CreateRouter(
            LinuxRemoteWithHostname(),
            MacRemoteWithHostname(),
            new FakeSshConnectionService());

        await Assert.ThrowsAsync<ArgumentNullException>(() => router.CollectAsync(null!));
    }
}
