using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Probes;

/// <summary>
/// One extra read-only command run on a monitored server after the built-in metrics, in the same SSH session.
/// <para>
/// The scheduler reads every property ONCE, when it is created, and keeps those values: the command is a constant,
/// never built from server data, and a probe cannot change it later. A probe whose values break
/// <see cref="RemoteProbeLimits"/> is left out. The probe never sees the session: it only receives outcomes through
/// <see cref="Accept"/>.
/// </para>
/// <para>
/// Probes run in the second phase of a poll, after the built-in result was already published, so a slow probe never
/// delays freshness, health, history or the screen. The output stays on this computer: the scheduler and the SSH layer
/// never log it.
/// </para>
/// </summary>
public interface IRemoteProbe
{
    /// <summary>Stable id: 1-64 lowercase ASCII letters, digits, '-' or '.', with '.' only between non-empty segments.</summary>
    string Id { get; }

    /// <summary>The one operating system it runs on: <see cref="ServerOperatingSystem.Linux"/> or <see cref="ServerOperatingSystem.MacOS"/>.</summary>
    ServerOperatingSystem OperatingSystem { get; }

    /// <summary>The constant command line. Single line, at most <see cref="RemoteProbeLimits.MaxCommandLength"/> characters.</summary>
    string Command { get; }

    /// <summary>The most standard output kept, in bytes. More than that is reported as <see cref="RemoteProbeOutcomeKind.OutputExceededLimit"/>.</summary>
    int OutputLimitBytes { get; }

    /// <summary>How long one run may take before it is abandoned as <see cref="RemoteProbeOutcomeKind.TimedOut"/>.</summary>
    TimeSpan Timeout { get; }

    /// <summary>The least time between two runs on the same server. Zero means every poll that has room.</summary>
    TimeSpan MinimumInterval { get; }

    /// <summary>
    /// Receives one outcome. Called from a background thread, never concurrently for the same server. Must be quick;
    /// an exception thrown here is caught and ignored.
    /// </summary>
    void Accept(RemoteProbeOutcome outcome);
}
