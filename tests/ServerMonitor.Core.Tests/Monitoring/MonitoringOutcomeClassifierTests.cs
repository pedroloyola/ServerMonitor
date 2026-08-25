using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.Core.Tests.Monitoring;

public sealed class MonitoringOutcomeClassifierTests
{
    private static ServerMetricsCollectionResult Success()
    {
        var snapshot = new ServerMetricsSnapshot
        {
            ServerId = Guid.NewGuid(),
            CollectedAt = DateTimeOffset.UnixEpoch,
            CpuUsagePercent = 5
        };
        return ServerMetricsCollectionResult.Success(
            snapshot,
            new SshConnectionResult { State = ServerConnectionState.Connected });
    }

    private static ServerMetricsCollectionResult ConnectionFailed(SshConnectionErrorCode code) =>
        ServerMetricsCollectionResult.Failure(
            MetricsCollectionErrorCode.ConnectionFailed,
            new SshConnectionResult { State = ServerConnectionState.Error, ErrorCode = code });

    [Fact]
    public void Success_is_success() =>
        Assert.Equal(MonitoringOutcome.Success, MonitoringOutcomeClassifier.Classify(Success()));

    [Fact]
    public void Cancelled_is_cancelled() =>
        Assert.Equal(
            MonitoringOutcome.Cancelled,
            MonitoringOutcomeClassifier.Classify(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled)));

    [Fact]
    public void NoMetrics_is_no_data() =>
        Assert.Equal(
            MonitoringOutcome.NoData,
            MonitoringOutcomeClassifier.Classify(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.NoMetricsAvailable)));

    [Fact]
    public void Timed_out_is_retryable() =>
        Assert.Equal(
            MonitoringOutcome.Retryable,
            MonitoringOutcomeClassifier.Classify(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.TimedOut)));

    [Fact]
    public void Unsupported_os_is_non_retryable() =>
        Assert.Equal(
            MonitoringOutcome.NonRetryable,
            MonitoringOutcomeClassifier.Classify(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.UnsupportedOperatingSystem)));

    [Fact]
    public void Invalid_configuration_is_non_retryable() =>
        Assert.Equal(
            MonitoringOutcome.NonRetryable,
            MonitoringOutcomeClassifier.Classify(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.InvalidConfiguration)));

    [Theory]
    [InlineData(SshConnectionErrorCode.AuthenticationFailed)]
    [InlineData(SshConnectionErrorCode.HostKeyUnknown)]
    [InlineData(SshConnectionErrorCode.HostKeyMismatch)]
    [InlineData(SshConnectionErrorCode.CredentialNotConfigured)]
    [InlineData(SshConnectionErrorCode.CredentialUnavailable)]
    [InlineData(SshConnectionErrorCode.PrivateKeyUnavailable)]
    [InlineData(SshConnectionErrorCode.PrivateKeyInvalid)]
    [InlineData(SshConnectionErrorCode.UnsupportedAlgorithm)]
    [InlineData(SshConnectionErrorCode.InvalidConfiguration)]
    public void Stable_ssh_problems_are_non_retryable(SshConnectionErrorCode code) =>
        Assert.Equal(MonitoringOutcome.NonRetryable, MonitoringOutcomeClassifier.Classify(ConnectionFailed(code)));

    [Theory]
    [InlineData(SshConnectionErrorCode.DnsResolutionFailed)]
    [InlineData(SshConnectionErrorCode.ConnectionRefused)]
    [InlineData(SshConnectionErrorCode.HostUnreachable)]
    [InlineData(SshConnectionErrorCode.NetworkUnavailable)]
    [InlineData(SshConnectionErrorCode.ConnectionTimedOut)]
    [InlineData(SshConnectionErrorCode.RemoteDisconnected)]
    [InlineData(SshConnectionErrorCode.ProtocolError)]
    public void Transient_ssh_problems_are_retryable(SshConnectionErrorCode code) =>
        Assert.Equal(MonitoringOutcome.Retryable, MonitoringOutcomeClassifier.Classify(ConnectionFailed(code)));

    [Fact]
    public void Cancelled_connection_is_cancelled() =>
        Assert.Equal(MonitoringOutcome.Cancelled, MonitoringOutcomeClassifier.Classify(ConnectionFailed(SshConnectionErrorCode.Cancelled)));
}
