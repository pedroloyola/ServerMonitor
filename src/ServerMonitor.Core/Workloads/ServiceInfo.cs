namespace ServerMonitor.Core.Workloads;

/// <summary>
/// A single managed service, read-only (M11). Platform-specific fields are nullable by design so the
/// model never invents portability that a given manager does not provide (§60/§61): systemd exposes a
/// description and sub-state, launchd does not. Strings are sanitized at the parser boundary.
/// </summary>
public sealed record ServiceInfo
{
    /// <summary>Stable identifier: systemd unit id ("ssh.service") or launchd label (sanitized).</summary>
    public required string Id { get; init; }

    /// <summary>Short human-readable name derived from <see cref="Id"/> (sanitized).</summary>
    public required string Name { get; init; }

    /// <summary>systemd "Description"; <c>null</c> on managers that do not provide one (e.g. launchd).</summary>
    public string? DisplayName { get; init; }

    public required ServiceState State { get; init; }

    /// <summary>Raw manager sub-state, sanitized (systemd "running"/"dead"/"exited"); <c>null</c> elsewhere.</summary>
    public string? SubState { get; init; }

    /// <summary>Boot-startup configuration when known; <c>null</c> when the manager/service does not expose it.</summary>
    public ServiceStartupState? StartupState { get; init; }
}
