namespace ServerMonitor.Infrastructure.Collectors.MacOS;

/// <summary>
/// The fixed, code-controlled catalog of macOS metric commands. No user,
/// configuration or UI value is ever concatenated into these strings. All are
/// part of the macOS base system (no Homebrew, no GNU coreutils, BSD userland).
/// </summary>
internal static class MacOsMetricsCommandCatalog
{
    // top self-samples: -l 2 takes two samples ~1s apart; -n 0 omits the
    // process list. The second "CPU usage:" line reflects the interval.
    internal const string CpuTop = "top -l 2 -n 0";
    internal const string VmStat = "vm_stat";
    internal const string PhysicalMemory = "sysctl -n hw.memsize";
    // BSD df: -k reports 1024-byte blocks (no GNU -B); -P is the POSIX format.
    internal const string RootFileSystem = "df -P -k /";
    internal const string BootTime = "sysctl -n kern.boottime";
    internal const string Hostname = "hostname";
    internal const string SwVers = "sw_vers";

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        CpuTop,
        VmStat,
        PhysicalMemory,
        RootFileSystem,
        BootTime,
        Hostname,
        SwVers
    ]);
}
