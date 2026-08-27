namespace ServerMonitor.App.ViewModels;

/// <summary>
/// Normalized status severity shared by Docker containers and managed services, so one colour legend
/// (§52) and one accessible filter (§51) cover both. Deliberately not the brand accent: #1846E1 is
/// reserved for interaction, never for health (QUALITY_BAR §11). <see cref="Neutral"/> covers both
/// "stopped/inactive" and "unknown" — a neutral status is never coloured as healthy or failed.
/// </summary>
public enum WorkloadSeverity
{
    /// <summary>Stopped, inactive, or unknown — neutral grey.</summary>
    Neutral = 0,

    /// <summary>Running / healthy — green.</summary>
    Positive,

    /// <summary>Starting / restarting — amber.</summary>
    Warning,

    /// <summary>Failed / unhealthy — red.</summary>
    Negative
}
