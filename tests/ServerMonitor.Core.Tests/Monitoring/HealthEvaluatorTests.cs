using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.Core.Tests.Monitoring;

public sealed class HealthEvaluatorTests
{
    private static ServerMetricsSnapshot Snapshot(double? cpu = null, double? memory = null, double? disk = null) => new()
    {
        ServerId = Guid.NewGuid(),
        CollectedAt = DateTimeOffset.UnixEpoch,
        CpuUsagePercent = cpu,
        MemoryUsagePercent = memory,
        DiskUsagePercent = disk
    };

    [Theory]
    [InlineData(79, ServerHealth.Healthy)]
    [InlineData(80, ServerHealth.Warning)]
    [InlineData(94.9, ServerHealth.Warning)]
    [InlineData(95, ServerHealth.Critical)]
    public void Cpu_thresholds(double cpu, ServerHealth expected) =>
        Assert.Equal(expected, HealthEvaluator.EvaluateFromMetrics(Snapshot(cpu: cpu)));

    [Theory]
    [InlineData(79, ServerHealth.Healthy)]
    [InlineData(80, ServerHealth.Warning)]
    [InlineData(94.9, ServerHealth.Warning)]
    [InlineData(95, ServerHealth.Critical)]
    public void Memory_thresholds(double memory, ServerHealth expected) =>
        Assert.Equal(expected, HealthEvaluator.EvaluateFromMetrics(Snapshot(memory: memory)));

    [Theory]
    [InlineData(79, ServerHealth.Healthy)]
    [InlineData(80, ServerHealth.Warning)]
    [InlineData(89.9, ServerHealth.Warning)]
    [InlineData(90, ServerHealth.Critical)]
    public void Disk_thresholds(double disk, ServerHealth expected) =>
        Assert.Equal(expected, HealthEvaluator.EvaluateFromMetrics(Snapshot(disk: disk)));

    [Fact]
    public void Highest_severity_wins()
    {
        var health = HealthEvaluator.EvaluateFromMetrics(Snapshot(cpu: 20, memory: 45, disk: 92));
        Assert.Equal(ServerHealth.Critical, health);
    }

    [Fact]
    public void All_healthy_is_healthy()
    {
        var health = HealthEvaluator.EvaluateFromMetrics(Snapshot(cpu: 10, memory: 20, disk: 30));
        Assert.Equal(ServerHealth.Healthy, health);
    }

    [Fact]
    public void Unknown_metric_is_ignored_not_treated_as_zero()
    {
        // Only disk is known and critical; unknown cpu/memory must not pull it down to Healthy.
        var health = HealthEvaluator.EvaluateFromMetrics(Snapshot(disk: 96));
        Assert.Equal(ServerHealth.Critical, health);
    }

    [Fact]
    public void All_metrics_unknown_is_unknown()
    {
        Assert.Equal(ServerHealth.Unknown, HealthEvaluator.EvaluateFromMetrics(Snapshot()));
    }

    [Fact]
    public void Null_snapshot_is_unknown()
    {
        Assert.Equal(ServerHealth.Unknown, HealthEvaluator.EvaluateFromMetrics(null));
    }

    [Fact]
    public void Zero_is_real_data_and_healthy()
    {
        Assert.Equal(ServerHealth.Healthy, HealthEvaluator.EvaluateFromMetrics(Snapshot(cpu: 0, memory: 0, disk: 0)));
    }
}
