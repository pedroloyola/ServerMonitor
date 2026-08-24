namespace ServerMonitor.Collectors.Linux;

public sealed record LinuxMetricsCollectorOptions
{
    public static readonly LinuxMetricsCollectorOptions Default = new();

    public TimeSpan CpuSampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
