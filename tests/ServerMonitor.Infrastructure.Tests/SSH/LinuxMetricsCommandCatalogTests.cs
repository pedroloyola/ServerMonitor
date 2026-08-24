using ServerMonitor.Infrastructure.Collectors.Linux;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class LinuxMetricsCommandCatalogTests
{
    [Fact]
    public void Catalog_contains_only_the_six_reviewed_literal_commands()
    {
        Assert.Equal(
            [
                "cat /proc/stat",
                "cat /proc/meminfo",
                "LC_ALL=C df -P -B1 /",
                "cat /proc/uptime",
                "cat /proc/sys/kernel/hostname",
                "cat /etc/os-release"
            ],
            LinuxMetricsCommandCatalog.All);
    }
}
