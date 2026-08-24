using ServerMonitor.Collectors.Linux.Parsing;
using ServerMonitor.Collectors.MacOS.Parsing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Collectors.MacOS;

/// <summary>
/// Collects metrics from a macOS server over the fixed commands exposed by
/// IMacOsMetricsRemoteSource, normalizing them into the same
/// <see cref="ServerMetricsSnapshot"/> used by Linux. Rejects any non-macOS
/// server up front, without issuing a remote call. The hostname is parsed with
/// the shared, OS-agnostic <see cref="HostnameParser"/>.
/// </summary>
public sealed class MacOsMetricsCollector : IServerMetricsCollector
{
    private readonly IMacOsMetricsRemoteSource _remoteSource;
    private readonly TimeProvider _timeProvider;
    private readonly MacOsMetricsCollectorOptions _options;

    public MacOsMetricsCollector(
        IMacOsMetricsRemoteSource remoteSource,
        TimeProvider? timeProvider = null,
        MacOsMetricsCollectorOptions? options = null)
    {
        _remoteSource = remoteSource ?? throw new ArgumentNullException(nameof(remoteSource));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? MacOsMetricsCollectorOptions.Default;
    }

    public async Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.OperatingSystem != ServerOperatingSystem.MacOS)
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
                .CollectAsync(server, _options.Timeout, cancellationToken)
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

    private ServerMetricsSnapshot BuildSnapshot(Guid serverId, MacOsMetricsRawData data)
    {
        var memory = MacMemoryParser.Parse(data.VmStat, data.PhysicalMemory);
        var disk = MacDiskUsageParser.Parse(data.RootFileSystem);
        var os = SwVersParser.Parse(data.SwVers);

        return new ServerMetricsSnapshot
        {
            ServerId = serverId,
            CollectedAt = _timeProvider.GetUtcNow(),
            CpuUsagePercent = MacCpuParser.CalculateUsagePercent(data.CpuTop),
            MemoryUsedBytes = memory.UsedBytes,
            MemoryTotalBytes = memory.TotalBytes,
            MemoryUsagePercent = memory.UsagePercent,
            DiskUsedBytes = disk.UsedBytes,
            DiskTotalBytes = disk.TotalBytes,
            DiskUsagePercent = disk.UsagePercent,
            Uptime = ComputeUptime(data.BootTime),
            Hostname = HostnameParser.Parse(data.Hostname),
            OperatingSystemName = os.ProductName,
            OperatingSystemVersion = os.ProductVersion
        };
    }

    private TimeSpan? ComputeUptime(string? bootTimeOutput)
    {
        if (BootTimeParser.Parse(bootTimeOutput) is not { } bootTime)
        {
            return null;
        }

        var uptime = _timeProvider.GetUtcNow() - bootTime;
        return uptime < TimeSpan.Zero ? null : uptime;
    }

    private static MetricsCollectionErrorCode MapConnectionError(SshConnectionErrorCode errorCode) => errorCode switch
    {
        SshConnectionErrorCode.InvalidConfiguration => MetricsCollectionErrorCode.InvalidConfiguration,
        SshConnectionErrorCode.Cancelled => MetricsCollectionErrorCode.Cancelled,
        SshConnectionErrorCode.ConnectionTimedOut => MetricsCollectionErrorCode.TimedOut,
        _ => MetricsCollectionErrorCode.ConnectionFailed
    };
}
