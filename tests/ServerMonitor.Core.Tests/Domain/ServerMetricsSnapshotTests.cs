using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Tests.Domain;

public sealed class ServerMetricsSnapshotTests
{
    [Fact]
    public void Zero_is_available_data_while_null_is_unknown()
    {
        var snapshot = new ServerMetricsSnapshot
        {
            ServerId = Guid.NewGuid(),
            CollectedAt = DateTimeOffset.UtcNow,
            CpuUsagePercent = 0
        };

        Assert.True(snapshot.HasAnyData);
        Assert.Equal(0, snapshot.CpuUsagePercent);
        Assert.Null(snapshot.MemoryUsagePercent);
    }

    [Fact]
    public void Empty_snapshot_has_no_available_data()
    {
        var snapshot = new ServerMetricsSnapshot
        {
            ServerId = Guid.NewGuid(),
            CollectedAt = DateTimeOffset.UtcNow
        };

        Assert.False(snapshot.HasAnyData);
    }

    [Fact]
    public void Successful_result_requires_a_snapshot_and_connection_result()
    {
        var snapshot = new ServerMetricsSnapshot
        {
            ServerId = Guid.NewGuid(),
            CollectedAt = DateTimeOffset.UtcNow,
            Hostname = "ubuntu"
        };
        var connection = new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            ErrorCode = SshConnectionErrorCode.None
        };

        var result = ServerMetricsCollectionResult.Success(snapshot, connection);

        Assert.True(result.IsSuccess);
        Assert.Same(snapshot, result.Snapshot);
        Assert.Same(connection, result.ConnectionResult);
    }

    [Fact]
    public void Failure_rejects_none_as_an_error_code()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.None));
    }
}
