using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

public sealed record ServerMetricsCollectionResult
{
    public ServerMetricsSnapshot? Snapshot { get; init; }

    public MetricsCollectionErrorCode ErrorCode { get; init; }

    public SshConnectionResult? ConnectionResult { get; init; }

    public bool IsSuccess => Snapshot is not null && ErrorCode == MetricsCollectionErrorCode.None;

    public static ServerMetricsCollectionResult Success(
        ServerMetricsSnapshot snapshot,
        SshConnectionResult connectionResult) => new()
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)),
        ConnectionResult = connectionResult ?? throw new ArgumentNullException(nameof(connectionResult))
    };

    public static ServerMetricsCollectionResult Failure(
        MetricsCollectionErrorCode errorCode,
        SshConnectionResult? connectionResult = null) => new()
    {
        ErrorCode = errorCode == MetricsCollectionErrorCode.None
            ? throw new ArgumentOutOfRangeException(nameof(errorCode))
            : errorCode,
        ConnectionResult = connectionResult
    };
}
