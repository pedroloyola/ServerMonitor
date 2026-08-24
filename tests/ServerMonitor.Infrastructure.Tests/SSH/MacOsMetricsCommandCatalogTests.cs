using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class MacOsMetricsCommandCatalogTests
{
    [Fact]
    public void Catalog_contains_only_the_seven_reviewed_literal_commands()
    {
        Assert.Equal(
            [
                "top -l 2 -n 0",
                "vm_stat",
                "sysctl -n hw.memsize",
                "df -P -k /",
                "sysctl -n kern.boottime",
                "hostname",
                "sw_vers"
            ],
            MacOsMetricsCommandCatalog.All);
    }

    [Fact]
    public void Catalog_commands_do_not_use_gnu_only_flags()
    {
        // -B is a GNU df byte-size flag absent from BSD/macOS df.
        Assert.All(MacOsMetricsCommandCatalog.All, command => Assert.DoesNotContain("-B", command));
    }
}
