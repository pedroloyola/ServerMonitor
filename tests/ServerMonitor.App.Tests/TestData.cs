using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests;

internal static class TestData
{
    public static Server LinuxServer(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "web-01",
        Host = "10.0.0.5",
        Port = 22,
        Username = "deploy",
        OperatingSystem = ServerOperatingSystem.Linux,
        AuthenticationMethod = AuthenticationMethod.Password
    };

    public static SshConnectionResult Connected() => new()
    {
        State = ServerConnectionState.Connected,
        ErrorCode = SshConnectionErrorCode.None
    };

    public static ServerMetricsSnapshot Snapshot(
        Guid serverId,
        double? cpu = null,
        double? memoryPercent = null,
        long? memoryUsed = null,
        long? memoryTotal = null,
        double? diskPercent = null,
        long? diskUsed = null,
        long? diskTotal = null,
        TimeSpan? uptime = null,
        string? hostname = null,
        string? osName = null,
        string? osVersion = null,
        DateTimeOffset? collectedAt = null) => new()
    {
        ServerId = serverId,
        CollectedAt = collectedAt ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        CpuUsagePercent = cpu,
        MemoryUsagePercent = memoryPercent,
        MemoryUsedBytes = memoryUsed,
        MemoryTotalBytes = memoryTotal,
        DiskUsagePercent = diskPercent,
        DiskUsedBytes = diskUsed,
        DiskTotalBytes = diskTotal,
        Uptime = uptime,
        Hostname = hostname,
        OperatingSystemName = osName,
        OperatingSystemVersion = osVersion
    };

    public static ServerMetricsCollectionResult Success(ServerMetricsSnapshot snapshot) =>
        ServerMetricsCollectionResult.Success(snapshot, Connected());

    public static ServerMetricsCollectionResult Failure(
        MetricsCollectionErrorCode errorCode,
        SshConnectionResult? connection = null) =>
        ServerMetricsCollectionResult.Failure(errorCode, connection);
}
