namespace ServerMonitor.Infrastructure.Collectors.Linux;

internal static class LinuxMetricsCommandCatalog
{
    internal const string CpuStat = "cat /proc/stat";
    internal const string MemInfo = "cat /proc/meminfo";
    internal const string RootFileSystem = "LC_ALL=C df -P -B1 /";
    internal const string Uptime = "cat /proc/uptime";
    internal const string Hostname = "cat /proc/sys/kernel/hostname";
    internal const string OsRelease = "cat /etc/os-release";

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        CpuStat,
        MemInfo,
        RootFileSystem,
        Uptime,
        Hostname,
        OsRelease
    ]);
}
