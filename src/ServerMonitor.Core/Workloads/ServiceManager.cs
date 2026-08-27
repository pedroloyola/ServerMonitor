namespace ServerMonitor.Core.Workloads;

/// <summary>
/// The service manager used to observe a server's services. Resolved by
/// <see cref="WorkloadManagerPolicy"/> from the server's operating system (§69). <see cref="Unsupported"/>
/// is the default so an unknown OS, or a Linux init system we do not read, never fabricates false
/// portability (e.g. pretending macOS has systemd). Docker is observed independently of this value.
/// </summary>
public enum ServiceManager
{
    /// <summary>No supported service manager (unknown OS, or non-systemd Linux init).</summary>
    Unsupported = 0,

    /// <summary>Linux with systemd (read-only: <c>systemctl</c>).</summary>
    Systemd,

    /// <summary>macOS (read-only: <c>launchctl</c>).</summary>
    Launchd
}
