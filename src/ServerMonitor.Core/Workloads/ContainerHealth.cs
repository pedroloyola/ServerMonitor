namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Health-check status of a Docker container. <see cref="None"/> (container declares no health check)
/// is deliberately distinct from <see cref="Unknown"/> (not determined): "no health check configured"
/// is not the same as "we don't know".
/// </summary>
public enum ContainerHealth
{
    /// <summary>Not determined (unparsed/unexpected).</summary>
    Unknown = 0,

    /// <summary>The container defines no health check.</summary>
    None,

    /// <summary>Health check is running but has not yet passed.</summary>
    Starting,

    Healthy,
    Unhealthy
}
