namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Whether a service is configured to start at boot. Platform-specific and often unavailable, so it is
/// carried as a nullable field on <see cref="ServiceInfo"/> (no false portability, §60/§61).
/// <see cref="Unknown"/> is the default when a manager reports a value we do not recognize.
/// </summary>
public enum ServiceStartupState
{
    Unknown = 0,
    Enabled,
    Disabled,

    /// <summary>systemd "static" — cannot be enabled/disabled directly.</summary>
    Static,

    /// <summary>systemd "masked" — fully disabled via a symlink to /dev/null.</summary>
    Masked
}
