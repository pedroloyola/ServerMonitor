namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Runtime state of a managed service, normalized across systemd/launchd. <see cref="Unknown"/> is the
/// default so an unparsed value never collapses into <see cref="Running"/> — unknown ≠ zero.
/// </summary>
public enum ServiceState
{
    Unknown = 0,
    Running,
    Stopped,
    Failed,
    Starting,
    Stopping
}
