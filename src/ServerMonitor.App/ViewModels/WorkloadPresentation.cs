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

    /// <summary>
    /// UI.3 health column key. Only a running (or restarting) container has a meaningful health verdict;
    /// a stopped one reads "—". <c>None</c> = the image defines no check ("Sem verificação"), which is not
    /// the same as <c>Unknown</c> ("—"). Returns <c>null</c> for "—" so callers can speak "unknown".
    /// </summary>
    public static string? ContainerHealthDisplayKey(ContainerState state, ContainerHealth health)
    {
        if (state is not (ContainerState.Running or ContainerState.Restarting))
        {
            return null;
        }

        return health switch
        {
            ContainerHealth.None => "WorkloadContainerHealthNone",
            ContainerHealth.Healthy or ContainerHealth.Unhealthy or ContainerHealth.Starting =>
                $"WorkloadContainerHealth{health}",
            _ => null
        };
    }

    /// <summary>UI.3 service state key (D-UI3-6): Running → "Ativo", Stopped → "Inativo", others unchanged.</summary>
    public static string ServiceStateDisplayKey(ServiceState state) => state switch
    {
        ServiceState.Running => "WorkloadServiceStatusActive",
        ServiceState.Stopped => "WorkloadServiceStatusInactive",
        _ => $"WorkloadServiceState{state}"
    };

    /// <summary>UI.3 startup key (D-UI3-6); <c>null</c> (shown as "—") when not reported or unknown.</summary>
    public static string? ServiceStartupDisplayKey(ServiceStartupState? startup) => startup switch
    {
        ServiceStartupState.Enabled => "WorkloadServiceStartupAutomatic",
        ServiceStartupState.Static => "WorkloadServiceStartupStaticDisplay",
        ServiceStartupState.Disabled => "WorkloadServiceStartupManual",
        ServiceStartupState.Masked => "WorkloadServiceStartupBlocked",
        _ => null
    };

    /// <summary>
    /// Plural form key: "<paramref name="baseKey"/>One" for exactly 1, else "…Other". Counts of 0 never
    /// reach plural copy (segments and badges are omitted at 0), which keeps pt-BR's CLDR 0/1 rule moot.
    /// </summary>
    public static string PluralKey(string baseKey, int count) => count == 1 ? baseKey + "One" : baseKey + "Other";

    /// <summary>
    /// Container lifecycle bucket for the UI.3 section summary ("Docker · 5 em execução · 1 parado"). Counts by
    /// lifecycle, not severity: an unhealthy but running container is "em execução" (its problem is the badge).
    /// </summary>
    public static string ContainerLifecycleKey(ContainerState state) => state switch
    {
        ContainerState.Running => "WorkloadLifecycleRunning",
        ContainerState.Restarting => "WorkloadLifecycleRestarting",
        ContainerState.Paused => "WorkloadLifecyclePaused",
        ContainerState.Dead => "WorkloadLifecycleDead",
        ContainerState.Created or ContainerState.Exited or ContainerState.Removing => "WorkloadLifecycleStopped",
        _ => "WorkloadLifecycleUnknown"
    };

    /// <summary>Service lifecycle bucket for the UI.3 section summary ("systemd · 5 ativos · 1 falha").</summary>
    public static string ServiceLifecycleKey(ServiceState state) => state switch
    {
        ServiceState.Running => "WorkloadServiceCountActive",
        ServiceState.Failed => "WorkloadServiceCountFailed",
        ServiceState.Starting => "WorkloadServiceCountStarting",
        ServiceState.Stopping => "WorkloadServiceCountStopping",
        ServiceState.Stopped => "WorkloadServiceCountInactive",
        _ => "WorkloadServiceCountUnknown"
    };

    /// <summary>Summary segment order: active first, failures right after so they are never read last (H-02).</summary>
    public static readonly IReadOnlyList<string> ContainerLifecycleOrder =
    [
        "WorkloadLifecycleRunning",
        "WorkloadLifecycleDead",
        "WorkloadLifecycleRestarting",
        "WorkloadLifecyclePaused",
        "WorkloadLifecycleStopped",
        "WorkloadLifecycleUnknown"
    ];

    public static readonly IReadOnlyList<string> ServiceLifecycleOrder =
    [
        "WorkloadServiceCountActive",
        "WorkloadServiceCountFailed",
        "WorkloadServiceCountStarting",
        "WorkloadServiceCountStopping",
        "WorkloadServiceCountInactive",
        "WorkloadServiceCountUnknown"
    ];

    /// <summary>
    /// UI.3 global search: one text over container name + image and service name + description. Ordinal
    /// ignore-case is culture-invariant (no tr-TR dotless-i surprises) and matches the ASCII-heavy unit
    /// and image names; an empty/whitespace query matches everything.
    /// </summary>
    public static bool Matches(string query, params string?[] fields)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            || fields.Any(field => field?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>Stable primary sort key for services: failed first, then running, then everything else (§49).</summary>
    public static int ServiceSortRank(ServiceState state) => state switch
    {
        ServiceState.Failed => 0,
        ServiceState.Running => 1,
        _ => 2
    };
}
