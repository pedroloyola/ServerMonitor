using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;

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

    /// <summary>
    /// Two-phase collection in ONE session: <paramref name="onBaseResult"/> receives the built-in result as soon as it
    /// is ready, only when it succeeded; then the due probes of <paramref name="probes"/> run before the session
    /// closes. The returned task means "the session is over". When <paramref name="onBaseResult"/> was called, the
    /// returned result is the same one and must not be published again; when it was not, publish the returned result
    /// as usual. This default runs no probes and calls back after the session closed.
    /// </summary>
    async Task<LinuxMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan cpuSampleInterval,
        TimeSpan timeout,
        Action<LinuxMetricsRemoteResult> onBaseResult,
        RemoteProbeReservation? probes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBaseResult);
        var result = await CollectAsync(server, cpuSampleInterval, timeout, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            RemoteResultCallback.Invoke(onBaseResult, result);
        }

        return result;
    }
}

public sealed record LinuxMetricsRemoteResult
{
    public required SshConnectionResult ConnectionResult { get; init; }

    public LinuxMetricsRawData? Data { get; init; }

    public bool IsSuccess => ConnectionResult.IsSuccess && Data is { HasAnySource: true };
}

public sealed record LinuxMetricsRawData
{
    /// <summary>At least one source answered. Without any, the collection counts as failed.</summary>
    public bool HasAnySource =>
        FirstCpuStat is not null
        || SecondCpuStat is not null
        || MemInfo is not null
        || RootFileSystem is not null
        || Uptime is not null
        || Hostname is not null
        || OsRelease is not null;

    public string? FirstCpuStat { get; init; }

    public string? SecondCpuStat { get; init; }

    public string? MemInfo { get; init; }

    public string? RootFileSystem { get; init; }

    public string? Uptime { get; init; }

    public string? Hostname { get; init; }

    public string? OsRelease { get; init; }
}
