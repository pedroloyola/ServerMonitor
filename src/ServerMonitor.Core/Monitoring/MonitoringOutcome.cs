using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// How the engine should react to a single collection result, independent of the many
/// underlying error codes. Keeps retry/health decisions in one testable place.
/// </summary>
public enum MonitoringOutcome
{
    /// <summary>A usable snapshot was produced.</summary>
    Success,

    /// <summary>A transient reachability problem worth a short retry, then Offline.</summary>
    Retryable,

    /// <summary>A stable problem (auth, host-key, bad config, unsupported OS): no retry, needs the user.</summary>
    NonRetryable,

    /// <summary>SSH worked but produced no parseable metrics: not offline, not retried in-cycle.</summary>
    NoData,

    /// <summary>The attempt was cancelled (shutdown or superseded): leave state untouched.</summary>
    Cancelled
}

/// <summary>
/// Maps a <see cref="ServerMetricsCollectionResult"/> to a <see cref="MonitoringOutcome"/>.
/// Because the collector collapses many SSH error codes into
/// <see cref="MetricsCollectionErrorCode.ConnectionFailed"/>, the precise decision is taken
/// from the carried <see cref="SshConnectionResult"/> when present. Auth/host-key/config
/// problems must never be retried aggressively or turned into Offline.
/// </summary>
public static class MonitoringOutcomeClassifier
{
    public static MonitoringOutcome Classify(ServerMetricsCollectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return MonitoringOutcome.Success;
        }

        return result.ErrorCode switch
        {
            MetricsCollectionErrorCode.Cancelled => MonitoringOutcome.Cancelled,
            MetricsCollectionErrorCode.NoMetricsAvailable => MonitoringOutcome.NoData,
            MetricsCollectionErrorCode.UnsupportedOperatingSystem => MonitoringOutcome.NonRetryable,
            MetricsCollectionErrorCode.InvalidConfiguration => MonitoringOutcome.NonRetryable,
            MetricsCollectionErrorCode.TimedOut => MonitoringOutcome.Retryable,
            MetricsCollectionErrorCode.ConnectionFailed => ClassifyConnection(result.ConnectionResult?.ErrorCode),
            _ => MonitoringOutcome.Retryable
        };
    }

    private static MonitoringOutcome ClassifyConnection(SshConnectionErrorCode? errorCode) => errorCode switch
    {
        SshConnectionErrorCode.AuthenticationFailed
            or SshConnectionErrorCode.HostKeyUnknown
            or SshConnectionErrorCode.HostKeyMismatch
            or SshConnectionErrorCode.InvalidConfiguration
            or SshConnectionErrorCode.CredentialNotConfigured
            or SshConnectionErrorCode.CredentialUnavailable
            or SshConnectionErrorCode.PrivateKeyUnavailable
            or SshConnectionErrorCode.PrivateKeyInvalid
            or SshConnectionErrorCode.UnsupportedAlgorithm => MonitoringOutcome.NonRetryable,
        SshConnectionErrorCode.Cancelled => MonitoringOutcome.Cancelled,
        _ => MonitoringOutcome.Retryable
    };
}
