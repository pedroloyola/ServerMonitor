using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;

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

    /// <summary>
    /// Two-phase collection in ONE session, as in <c>ILinuxMetricsRemoteSource</c>: <paramref name="onBaseResult"/>
    /// receives the built-in result as soon as it is ready, only when it succeeded; then the due probes run before
    /// the session closes. When <paramref name="onBaseResult"/> was called, the returned result must not be published
    /// again. This default runs no probes and calls back after the session closed.
    /// </summary>
    async Task<MacOsMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan timeout,
        Action<MacOsMetricsRemoteResult> onBaseResult,
        RemoteProbeReservation? probes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBaseResult);
        var result = await CollectAsync(server, timeout, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            RemoteResultCallback.Invoke(onBaseResult, result);
        }

        return result;
    }
}

public sealed record MacOsMetricsRemoteResult
{
    public required SshConnectionResult ConnectionResult { get; init; }

    public MacOsMetricsRawData? Data { get; init; }

    public bool IsSuccess => ConnectionResult.IsSuccess && Data is { HasAnySource: true };
}

public sealed record MacOsMetricsRawData
{
    /// <summary>At least one source answered. Without any, the collection counts as failed.</summary>
    public bool HasAnySource =>
        CpuTop is not null
        || VmStat is not null
        || PhysicalMemory is not null
        || RootFileSystem is not null
        || BootTime is not null
        || Hostname is not null
        || SwVers is not null;

    public string? CpuTop { get; init; }

    public string? VmStat { get; init; }

    public string? PhysicalMemory { get; init; }

    public string? RootFileSystem { get; init; }

    public string? BootTime { get; init; }

    public string? Hostname { get; init; }

    public string? SwVers { get; init; }
}
