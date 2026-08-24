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

    public async Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default)
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
            var remoteResult = await _remoteSource
                .CollectAsync(server, _options.CpuSampleInterval, _options.Timeout, cancellationToken)
                .ConfigureAwait(false);

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

    private ServerMetricsSnapshot BuildSnapshot(Guid serverId, LinuxMetricsRawData data)
    {
        var memory = MemInfoParser.Parse(data.MemInfo);
        var disk = DiskUsageParser.Parse(data.RootFileSystem);
        var os = OsReleaseParser.Parse(data.OsRelease);

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
            OperatingSystemVersion = os.Version
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
