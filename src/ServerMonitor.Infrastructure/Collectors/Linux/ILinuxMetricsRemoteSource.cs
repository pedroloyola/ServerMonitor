using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.Collectors.Linux;

/// <summary>
/// Provides the fixed Linux data sources needed by the metrics collector.
/// It intentionally exposes no arbitrary remote-command API.
/// </summary>
public interface ILinuxMetricsRemoteSource
{
    Task<LinuxMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan cpuSampleInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record LinuxMetricsRemoteResult
{
    public required SshConnectionResult ConnectionResult { get; init; }

    public LinuxMetricsRawData? Data { get; init; }

    public bool IsSuccess => ConnectionResult.IsSuccess && Data is
    {
        FirstCpuStat: not null
    } or
    {
        SecondCpuStat: not null
    } or
    {
        MemInfo: not null
    } or
    {
        RootFileSystem: not null
    } or
    {
        Uptime: not null
    } or
    {
        Hostname: not null
    } or
    {
        OsRelease: not null
    };
}

public sealed record LinuxMetricsRawData
{
    public string? FirstCpuStat { get; init; }

    public string? SecondCpuStat { get; init; }

    public string? MemInfo { get; init; }

    public string? RootFileSystem { get; init; }

    public string? Uptime { get; init; }

    public string? Hostname { get; init; }

    public string? OsRelease { get; init; }
}
