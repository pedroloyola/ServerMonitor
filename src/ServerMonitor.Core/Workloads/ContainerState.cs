namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Lifecycle state of a Docker container, normalized from the engine's raw state string.
/// <see cref="Unknown"/> is the default so an unparsed/unexpected value never collapses into a
/// concrete state such as <see cref="Running"/> — unknown ≠ zero.
/// </summary>
public enum ContainerState
{
    Unknown = 0,
    Created,
    Running,
    Restarting,
    Paused,
    Exited,
    Dead,
    Removing
}
