using ServerMonitor.Collectors.Linux;
using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Collectors;

/// <summary>
/// The single <see cref="IServerMetricsCollector"/> the store and UI consume.
/// It keeps Linux and macOS as nothing more than different collectors feeding
/// the same domain: it selects the OS-specific collector by
/// <see cref="Server.OperatingSystem"/>. An <c>Auto</c> server is resolved once
/// via the M3 host detection (Darwin → macOS, Linux → Linux) and then routed;
/// anything else is an unsupported-OS failure without any remote command.
/// </summary>
public sealed class MetricsCollectorRouter : IServerMetricsCollector
{
    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(10);

    private readonly LinuxMetricsCollector _linuxCollector;
    private readonly MacOsMetricsCollector _macOsCollector;
    private readonly ISshConnectionService _connectionService;

    public MetricsCollectorRouter(
        LinuxMetricsCollector linuxCollector,
        MacOsMetricsCollector macOsCollector,
        ISshConnectionService connectionService)
    {
        _linuxCollector = linuxCollector ?? throw new ArgumentNullException(nameof(linuxCollector));
        _macOsCollector = macOsCollector ?? throw new ArgumentNullException(nameof(macOsCollector));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    }

    public async Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var operatingSystem = server.OperatingSystem;
        if (operatingSystem == ServerOperatingSystem.Auto)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled);
            }

            SshConnectionResult detection;
            try
            {
                detection = await _connectionService
                    .DetectOperatingSystemAsync(
                        new SshConnectionRequest { Server = server, Timeout = DetectionTimeout },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled);
            }
            catch (Exception)
            {
                return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected);
            }

            if (!detection.IsSuccess)
            {
                return ServerMetricsCollectionResult.Failure(MapConnectionError(detection.ErrorCode), detection);
            }

            operatingSystem = detection.DetectedOperatingSystem;
        }

        return operatingSystem switch
        {
            ServerOperatingSystem.Linux => await _linuxCollector
                .CollectAsync(server with { OperatingSystem = ServerOperatingSystem.Linux }, cancellationToken)
                .ConfigureAwait(false),
            ServerOperatingSystem.MacOS => await _macOsCollector
                .CollectAsync(server with { OperatingSystem = ServerOperatingSystem.MacOS }, cancellationToken)
                .ConfigureAwait(false),
            _ => ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.UnsupportedOperatingSystem)
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
