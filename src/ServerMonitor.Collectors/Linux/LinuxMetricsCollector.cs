using ServerMonitor.Collectors.Linux.Parsing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Linux;

namespace ServerMonitor.Collectors.Linux;

/// <summary>
/// Collects metrics from a Linux server over the fixed commands exposed by
/// ILinuxMetricsRemoteSource. Rejects any non-Linux server up front, without
/// issuing a remote call.
/// </summary>
public sealed class LinuxMetricsCollector : IServerMetricsCollector
{
    private readonly ILinuxMetricsRemoteSource _remoteSource;
    private readonly TimeProvider _timeProvider;
    private readonly LinuxMetricsCollectorOptions _options;

    public LinuxMetricsCollector(
        ILinuxMetricsRemoteSource remoteSource,
        TimeProvider? timeProvider = null,
        LinuxMetricsCollectorOptions? options = null)
    {
        _remoteSource = remoteSource ?? throw new ArgumentNullException(nameof(remoteSource));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? LinuxMetricsCollectorOptions.Default;
    }

    public Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default) =>
        CollectCoreAsync(server, null, cancellationToken);

    public Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        MetricsCollectionPhases phases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(phases);
        return CollectCoreAsync(server, phases, cancellationToken);
    }

    private async Task<ServerMetricsCollectionResult> CollectCoreAsync(
        Server server,
        MetricsCollectionPhases? phases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.OperatingSystem != ServerOperatingSystem.Linux)
        {
            return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.UnsupportedOperatingSystem);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled);
        }

        try
        {
            if (phases is null)
            {
                var remoteResult = await _remoteSource
                    .CollectAsync(server, _options.CpuSampleInterval, _options.Timeout, cancellationToken)
                    .ConfigureAwait(false);
                return Map(server, remoteResult);
            }

            // Phase 1 is mapped and handed on from the session's callback; the returned result then only means the
            // session (and its probes) is over, so it is returned without being mapped or published again.
            ServerMetricsCollectionResult? published = null;
            var finalResult = await _remoteSource
                .CollectAsync(
                    server,
                    _options.CpuSampleInterval,
                    _options.Timeout,
                    baseResult =>
                    {
                        published = Map(server, baseResult);
                        phases.OnBaseResult(published);
                    },
                    phases.Probes,
                    cancellationToken)
                .ConfigureAwait(false);
            return published ?? Map(server, finalResult);
        }
        catch (OperationCanceledException)
        {
            return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled);
        }
        catch (Exception)
        {
            // The remote source and parsers are not expected to throw, but a
            // collector must never crash its caller over a single server.
            return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected);
        }
    }

    private ServerMetricsCollectionResult Map(Server server, LinuxMetricsRemoteResult remoteResult)
    {
        if (!remoteResult.ConnectionResult.IsSuccess)
        {
            return ServerMetricsCollectionResult.Failure(
                MapConnectionError(remoteResult.ConnectionResult.ErrorCode),
                remoteResult.ConnectionResult);
        }

        if (remoteResult.Data is not { } data)
        {
            return ServerMetricsCollectionResult.Failure(
                MetricsCollectionErrorCode.NoMetricsAvailable,
                remoteResult.ConnectionResult);
        }

        var snapshot = BuildSnapshot(server.Id, data);
        if (!snapshot.HasAnyData)
        {
            return ServerMetricsCollectionResult.Failure(
                MetricsCollectionErrorCode.NoMetricsAvailable,
                remoteResult.ConnectionResult);
        }

        return ServerMetricsCollectionResult.Success(snapshot, remoteResult.ConnectionResult);
    }

    private ServerMetricsSnapshot BuildSnapshot(Guid serverId, LinuxMetricsRawData data)
    {
        var memory = MemInfoParser.Parse(data.MemInfo);
        var disk = DiskUsageParser.Parse(data.RootFileSystem);
        var os = OsReleaseParser.Parse(data.OsRelease);
        var load = ProcLoadAvgParser.Parse(data.LoadAverage);

        return new ServerMetricsSnapshot
        {
            ServerId = serverId,
            CollectedAt = _timeProvider.GetUtcNow(),
            CpuUsagePercent = ProcStatCpuParser.CalculateUsagePercent(data.FirstCpuStat, data.SecondCpuStat),
            MemoryUsedBytes = memory.UsedBytes,
            MemoryTotalBytes = memory.TotalBytes,
            MemoryUsagePercent = memory.UsagePercent,
            DiskUsedBytes = disk.UsedBytes,
            DiskTotalBytes = disk.TotalBytes,
            DiskUsagePercent = disk.UsagePercent,
            Uptime = ProcUptimeParser.Parse(data.Uptime),
            Hostname = HostnameParser.Parse(data.Hostname),
            OperatingSystemName = os.Name,
            OperatingSystemVersion = os.Version,
            LoadAverage1 = load?.OneMinute,
            LoadAverage5 = load?.FiveMinutes,
            LoadAverage15 = load?.FifteenMinutes,
            LogicalCpuCount = ProcStatCpuParser.CountLogicalCpus(data.SecondCpuStat ?? data.FirstCpuStat)
        };
    }

    private static MetricsCollectionErrorCode MapConnectionError(SshConnectionErrorCode errorCode) => errorCode switch
    {
        SshConnectionErrorCode.InvalidConfiguration => MetricsCollectionErrorCode.InvalidConfiguration,
        SshConnectionErrorCode.Cancelled => MetricsCollectionErrorCode.Cancelled,
        SshConnectionErrorCode.ConnectionTimedOut => MetricsCollectionErrorCode.TimedOut,
        _ => MetricsCollectionErrorCode.ConnectionFailed
    };
}
