namespace ServerMonitor.Collectors.Workloads;

/// <summary>Tunables for <see cref="WorkloadCollector"/>. The timeout covers probe, auth and every command.</summary>
public sealed record WorkloadCollectorOptions
{
    public static readonly WorkloadCollectorOptions Default = new();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}
