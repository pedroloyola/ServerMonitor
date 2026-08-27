using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// Pure presentation mapping for the Workloads UI: domain state → <see cref="WorkloadSeverity"/> (the one
/// colour legend, §52) and the stable sort keys (§49). Kept deterministic and side-effect free so it is
/// unit-testable without any UI. unknown ≠ zero: an unrecognized/unknown state maps to
/// <see cref="WorkloadSeverity.Neutral"/>, never to a healthy or failed colour.
/// </summary>
public static class WorkloadPresentation
{
    /// <summary>
    /// Container status colour (§52). Health outranks lifecycle for the two health verdicts that change
    /// the story — an <c>Unhealthy</c> running container is red, a <c>Starting</c> one is amber — while a
    /// plain running container (no check, or already healthy) is green.
    /// </summary>
    public static WorkloadSeverity SeverityFor(ContainerInfo container)
    {
        if (container.Health == ContainerHealth.Unhealthy)
        {
            return WorkloadSeverity.Negative;
        }

        if (container.State == ContainerState.Dead)
        {
            return WorkloadSeverity.Negative;
        }

        if (container.State == ContainerState.Restarting || container.Health == ContainerHealth.Starting)
        {
            return WorkloadSeverity.Warning;
        }

        if (container.State == ContainerState.Running)
        {
            return WorkloadSeverity.Positive;
        }

        // Created / Paused / Exited / Removing / Unknown → neutral (stopped/inactive/unknown).
        return WorkloadSeverity.Neutral;
    }

    /// <summary>Service status colour (§52).</summary>
    public static WorkloadSeverity SeverityFor(ServiceState state) => state switch
    {
        ServiceState.Failed => WorkloadSeverity.Negative,
        ServiceState.Running => WorkloadSeverity.Positive,
        ServiceState.Starting or ServiceState.Stopping => WorkloadSeverity.Warning,
        _ => WorkloadSeverity.Neutral // Stopped / Unknown.
    };

    /// <summary>
    /// Per-field severity for the container <b>lifecycle</b> text alone, so the visible state label is
    /// coloured by what it actually says (a running container's "Em execução" never turns red just because
    /// its health check is failing — the health text carries that). Drives M-01 emphasis.
    /// </summary>
    public static WorkloadSeverity StateSeverityFor(ContainerState state) => state switch
    {
        ContainerState.Dead => WorkloadSeverity.Negative,
        ContainerState.Restarting => WorkloadSeverity.Warning,
        ContainerState.Running => WorkloadSeverity.Positive,
        _ => WorkloadSeverity.Neutral
    };

    /// <summary>Per-field severity for the container <b>health</b> text alone (M-01).</summary>
    public static WorkloadSeverity HealthSeverityFor(ContainerHealth health) => health switch
    {
        ContainerHealth.Unhealthy => WorkloadSeverity.Negative,
        ContainerHealth.Starting => WorkloadSeverity.Warning,
        ContainerHealth.Healthy => WorkloadSeverity.Positive,
        _ => WorkloadSeverity.Neutral // None / Unknown.
    };

    /// <summary>
    /// Stable primary sort key for containers: running first, everything else after (§49). Ties are broken
    /// by name at the call site with a stable <c>ThenBy</c>, so the order never jitters between refreshes.
    /// </summary>
    public static int ContainerSortRank(ContainerState state) => state == ContainerState.Running ? 0 : 1;

    /// <summary>Stable primary sort key for services: failed first, then running, then everything else (§49).</summary>
    public static int ServiceSortRank(ServiceState state) => state switch
    {
        ServiceState.Failed => 0,
        ServiceState.Running => 1,
        _ => 2
    };
}
