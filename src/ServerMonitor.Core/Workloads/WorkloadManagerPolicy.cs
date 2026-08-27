using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Pure, deterministic OS routing for services observability (§69), in one testable place (mirrors
/// <c>MonitoringOutcomeClassifier</c>/<c>RefreshIntervalPolicy</c>):
/// <list type="bullet">
/// <item>Linux with systemd → <see cref="ServiceManager.Systemd"/>.</item>
/// <item>Linux with a non-supported init (no systemd) → <see cref="ServiceManager.Unsupported"/> — no false systemd.</item>
/// <item>macOS → <see cref="ServiceManager.Launchd"/>.</item>
/// <item>Unknown/undetected OS → <see cref="ServiceManager.Unsupported"/> — no inference.</item>
/// </list>
/// Docker is observed independently of the service manager and is never gated by this decision.
/// </summary>
public static class WorkloadManagerPolicy
{
    /// <summary>
    /// Resolves the service manager from the (already OS-detected) operating system and, for Linux, a
    /// read-only systemd-presence probe result supplied by the infrastructure layer. For macOS the probe
    /// is irrelevant; for Auto/Unknown the manager is <see cref="ServiceManager.Unsupported"/>.
    /// </summary>
    public static ServiceManager Resolve(ServerOperatingSystem operatingSystem, bool systemdDetected) =>
        operatingSystem switch
        {
            ServerOperatingSystem.MacOS => ServiceManager.Launchd,
            ServerOperatingSystem.Linux => systemdDetected ? ServiceManager.Systemd : ServiceManager.Unsupported,
            _ => ServiceManager.Unsupported // Auto (unresolved) or Unknown: no inference.
        };

    /// <summary>
    /// True when the OS can, in principle, host a supported service manager — i.e. worth running the
    /// systemd probe. macOS always supports launchd; Linux is conditional (probe decides); everything
    /// else is unsupported. Lets the collector skip the probe entirely for non-candidates.
    /// </summary>
    public static bool SupportsServices(ServerOperatingSystem operatingSystem) =>
        operatingSystem is ServerOperatingSystem.MacOS or ServerOperatingSystem.Linux;
}
