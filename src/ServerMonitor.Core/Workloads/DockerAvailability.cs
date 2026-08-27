namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Whether the Docker engine could be observed on a server, and if not, why. Read-only observability
/// (M11): the app never administers Docker. <see cref="Unknown"/> is the default so a value that was
/// never probed is never mistaken for a concrete state — unknown ≠ a real availability.
/// </summary>
public enum DockerAvailability
{
    /// <summary>Not probed yet (initial/transient), or the SSH session itself did not complete.</summary>
    Unknown = 0,

    /// <summary>Probed successfully; the container list is valid (possibly empty).</summary>
    Available,

    /// <summary>The Docker CLI/daemon is absent on the host (Docker not installed).</summary>
    NotInstalled,

    /// <summary>The SSH user cannot reach the Docker socket/daemon (e.g. not in the docker group).</summary>
    PermissionDenied,

    /// <summary>Docker is installed but the daemon is stopped or not responding.</summary>
    Unavailable,

    /// <summary>An unexpected collection/parse failure (transient); distinct from a known cause above.</summary>
    Error
}
