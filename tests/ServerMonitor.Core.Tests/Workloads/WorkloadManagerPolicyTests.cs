using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Core.Tests.Workloads;

public sealed class WorkloadManagerPolicyTests
{
    [Fact]
    public void LinuxWithSystemd_Systemd()
    {
        Assert.Equal(ServiceManager.Systemd, WorkloadManagerPolicy.Resolve(ServerOperatingSystem.Linux, systemdDetected: true));
    }

    [Fact]
    public void LinuxWithoutSystemd_Unsupported()
    {
        // Non-systemd init (SysV/OpenRC/runit): no false systemd (§69).
        Assert.Equal(ServiceManager.Unsupported, WorkloadManagerPolicy.Resolve(ServerOperatingSystem.Linux, systemdDetected: false));
    }

    [Fact]
    public void MacOs_Launchd_RegardlessOfSystemdFlag()
    {
        Assert.Equal(ServiceManager.Launchd, WorkloadManagerPolicy.Resolve(ServerOperatingSystem.MacOS, systemdDetected: false));
        Assert.Equal(ServiceManager.Launchd, WorkloadManagerPolicy.Resolve(ServerOperatingSystem.MacOS, systemdDetected: true));
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Unknown)]
    [InlineData(ServerOperatingSystem.Auto)]
    public void UnknownOrUnresolved_Unsupported(ServerOperatingSystem os)
    {
        Assert.Equal(ServiceManager.Unsupported, WorkloadManagerPolicy.Resolve(os, systemdDetected: true));
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux, true)]
    [InlineData(ServerOperatingSystem.MacOS, true)]
    [InlineData(ServerOperatingSystem.Unknown, false)]
    [InlineData(ServerOperatingSystem.Auto, false)]
    public void SupportsServices_OnlyLinuxAndMac(ServerOperatingSystem os, bool expected)
    {
        Assert.Equal(expected, WorkloadManagerPolicy.SupportsServices(os));
    }
}
