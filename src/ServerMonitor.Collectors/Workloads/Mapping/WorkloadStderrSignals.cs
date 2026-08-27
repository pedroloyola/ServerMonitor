namespace ServerMonitor.Collectors.Workloads.Mapping;

/// <summary>
/// Read-only, English-only substring signals used to classify workload availability from a command's
/// stderr. The docker/systemd/launchctl CLIs are not localized, so these fixed substrings are stable.
/// They are a <i>secondary</i> signal — the exit status is primary — used only to distinguish causes
/// (not installed vs. permission vs. daemon-down) that a bare exit code cannot separate.
/// </summary>
internal static class WorkloadStderrSignals
{
    private static readonly StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    internal static bool CommandNotFound(string stderr) =>
        stderr.Contains("not found", Ci);

    internal static bool PermissionDenied(string stderr) =>
        stderr.Contains("permission denied", Ci);

    internal static bool DockerDaemonUnreachable(string stderr) =>
        stderr.Contains("Cannot connect to the Docker daemon", Ci) ||
        stderr.Contains("Is the docker daemon running", Ci);

    internal static bool SystemdNotBooted(string stderr) =>
        stderr.Contains("has not been booted with systemd", Ci) ||
        stderr.Contains("Failed to connect to bus", Ci);

    internal static bool SystemdAccessDenied(string stderr) =>
        stderr.Contains("Access denied", Ci) ||
        stderr.Contains("Interactive authentication required", Ci);

    // launchctl print system as a non-root user typically fails with EPERM/EIO or a domain error; these
    // are the specific error phrases for the R2 "system domain may be root-only" risk. Deliberately NOT
    // a bare "permission" substring — that is too broad to run over command output and, combined with the
    // exit-status gate in the mapper, would risk masking a legitimate listing. Only ever evaluated on a
    // command that already failed (non-zero exit), never on a successful dump.
    internal static bool LaunchdDenied(string text) =>
        text.Contains("Could not print domain", Ci) ||
        text.Contains("Operation not permitted", Ci) ||
        text.Contains("not permitted", Ci) ||
        text.Contains("Permission denied", Ci) ||
        text.Contains("requires root", Ci) ||
        text.Contains("Input/output error", Ci);
}
