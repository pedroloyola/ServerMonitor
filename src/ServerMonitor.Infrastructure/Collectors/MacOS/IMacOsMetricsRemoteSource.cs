using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.Collectors.MacOS;

/// <summary>
/// Provides the fixed macOS data sources needed by the metrics collector.
/// Mirrors <c>ILinuxMetricsRemoteSource</c>: it intentionally exposes no
/// arbitrary remote-command API. The macOS CPU sample is produced by a single
/// self-sampling command (top -l 2), so there is no external sample interval.
/// </summary>
public interface IMacOsMetricsRemoteSource
{
    Task<MacOsMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record MacOsMetricsRemoteResult
{
    public required SshConnectionResult ConnectionResult { get; init; }

    public MacOsMetricsRawData? Data { get; init; }

    public bool IsSuccess => ConnectionResult.IsSuccess && Data is
    {
        CpuTop: not null
    } or
    {
        VmStat: not null
    } or
    {
        PhysicalMemory: not null
    } or
    {
        RootFileSystem: not null
    } or
    {
        BootTime: not null
    } or
    {
        Hostname: not null
    } or
    {
        SwVers: not null
    };
}

public sealed record MacOsMetricsRawData
{
    public string? CpuTop { get; init; }

    public string? VmStat { get; init; }

    public string? PhysicalMemory { get; init; }

    public string? RootFileSystem { get; init; }

    public string? BootTime { get; init; }

    public string? Hostname { get; init; }

    public string? SwVers { get; init; }
}
