namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Whether the service manager (systemd/launchd) could be observed on a server, and if not, why.
/// Mirrors <see cref="DockerAvailability"/> but is a distinct type so Docker and services fail
/// independently (§38): "Docker unavailable" and "services unreadable" are never conflated.
/// <see cref="Unknown"/> is the default — unknown ≠ a real availability.
/// </summary>
public enum WorkloadServiceAvailability
{
    /// <summary>Not probed yet (initial/transient), or the SSH session itself did not complete.</summary>
    Unknown = 0,

    /// <summary>Probed successfully; the service list is valid (possibly empty).</summary>
    Available,

    /// <summary>No supported service manager on this host (e.g. Linux without systemd).</summary>
    NotInstalled,

    /// <summary>The SSH user cannot query the service manager.</summary>
    PermissionDenied,

    /// <summary>The manager exists but did not respond.</summary>
    Unavailable,

    /// <summary>An unexpected collection/parse failure (transient); distinct from a known cause above.</summary>
    Error
}
