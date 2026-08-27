using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.Collectors.Workloads;

/// <summary>
/// Provides the fixed, read-only workload data sources (Docker + services) needed by the workload
/// collector, over a single authenticated SSH session (§44). Like the metrics ports it exposes no
/// arbitrary remote-command API: only the closed catalog commands run, and no caller text is ever
/// concatenated into a command. Which service commands run is derived from
/// <see cref="Server.OperatingSystem"/> inside the implementation; the mapping of raw output to a
/// <c>ServiceManager</c>/availability lives in the pure Core/Collectors layer, never here.
/// </summary>
public interface IWorkloadRemoteSource
{
    Task<WorkloadRemoteResult> CollectAsync(
        Server server,
        WorkloadRemoteRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>What to collect in one workload pass. Read-only; nothing here mutates remote state.</summary>
public sealed record WorkloadRemoteRequest
{
    /// <summary>Probe Docker (independent of the service manager, §69).</summary>
    public bool IncludeDocker { get; init; } = true;

    /// <summary>
    /// Reserved for per-container CPU/memory via <c>docker stats</c> (ADR-016 / §58). Deferred in M11 —
    /// the sampling cost is out of scope for a read-only inventory — so this is not collected yet.
    /// </summary>
    public bool IncludeContainerStats { get; init; }

    /// <summary>Deadline covering probe, authentication and every catalog command.</summary>
    public required TimeSpan Timeout { get; init; }
}

/// <summary>
/// The outcome of a workload pass. <see cref="ConnectionResult"/> carries the M3 SSH state (trust/auth/
/// transport); <see cref="Data"/> is present when the authenticated session ran, and <c>null</c> when
/// the session never reached command execution (e.g. host-key or auth failure).
/// </summary>
public sealed record WorkloadRemoteResult
{
    public required SshConnectionResult ConnectionResult { get; init; }

    public WorkloadRawData? Data { get; init; }
}

/// <summary>
/// Raw, per-command output of one workload pass. Each field is the outcome of a single fixed catalog
/// command (or <c>null</c> when not applicable to this server/OS). Availability classification and
/// parsing happen in the pure Collectors layer: the exit status and stderr carried here are what let a
/// pure classifier tell <c>NotInstalled</c> / <c>PermissionDenied</c> / <c>Unavailable</c> apart —
/// distinctions that stdout alone cannot express.
/// </summary>
public sealed record WorkloadRawData
{
    /// <summary><c>docker version --format '{{.Server.Version}}'</c> — availability + daemon version probe.</summary>
    public RemoteCommandOutcome? DockerVersion { get; init; }

    /// <summary><c>docker ps -a --no-trunc --format '{{json .}}'</c> — only run when the version probe succeeds.</summary>
    public RemoteCommandOutcome? DockerPs { get; init; }

    /// <summary>Reserved (ADR-016/§58): per-container stats; not collected in M11.</summary>
    public RemoteCommandOutcome? DockerStats { get; init; }

    /// <summary><c>systemctl list-units --type=service …</c> (Linux) — runtime state + systemd detection signal.</summary>
    public RemoteCommandOutcome? SystemdListUnits { get; init; }

    /// <summary><c>systemctl list-unit-files --type=service …</c> (Linux) — only run when list-units succeeds.</summary>
    public RemoteCommandOutcome? SystemdListUnitFiles { get; init; }

    /// <summary><c>launchctl print system</c> (macOS system domain, §24).</summary>
    public RemoteCommandOutcome? LaunchdPrintSystem { get; init; }
}

/// <summary>
/// The result of running one closed-catalog command: its exit status, bounded/strictly-decoded stdout
/// and stderr, and whether the output tripped the size cap. <see cref="WasExecuted"/> is <c>false</c>
/// when a command was intentionally skipped (e.g. Docker ps after a failed version probe). A strict
/// UTF-8 decode failure yields a <c>null</c> stream (unavailable), never a fabricated value.
/// </summary>
public sealed record RemoteCommandOutcome
{
    public required bool WasExecuted { get; init; }

    /// <summary>Process exit status; <c>null</c> when the command did not complete (e.g. output cap hit).</summary>
    public int? ExitStatus { get; init; }

    /// <summary>Strictly-decoded stdout; <c>""</c> when empty, <c>null</c> when undecodable or capped.</summary>
    public string? StandardOutput { get; init; }

    /// <summary>Strictly-decoded stderr (used for availability classification); may be <c>null</c>.</summary>
    public string? StandardError { get; init; }

    /// <summary>True when stdout exceeded the per-command byte cap; the output is then unavailable.</summary>
    public bool OutputExceededLimit { get; init; }

    /// <summary>A command that was not run.</summary>
    public static readonly RemoteCommandOutcome NotExecuted = new() { WasExecuted = false };
}
