namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// Percent thresholds used to derive <see cref="Enums.ServerHealth"/> from a snapshot.
/// Values are inclusive lower bounds: a metric at exactly the threshold already counts.
/// The policy is a record so it can be made user-configurable later without touching
/// the evaluator; no threshold UI is exposed in this milestone.
/// </summary>
public sealed record MonitoringThresholds
{
    public double CpuWarning { get; init; } = 80d;

    public double CpuCritical { get; init; } = 95d;

    public double MemoryWarning { get; init; } = 80d;

    public double MemoryCritical { get; init; } = 95d;

    public double DiskWarning { get; init; } = 80d;

    public double DiskCritical { get; init; } = 90d;

    public static MonitoringThresholds Default { get; } = new();
}
