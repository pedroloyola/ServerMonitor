namespace ServerMonitor.Core.Probes;

/// <summary>How one remote read ended.</summary>
public enum RemoteReadOutcomeKind
{
    /// <summary>The script ran and exited. <see cref="RemoteReadOutcome.ExitStatus"/> may be non-zero.</summary>
    Completed,

    /// <summary>A check failed before any SSH work; <see cref="RemoteReadOutcome.Refusal"/> says which.</summary>
    Refused,

    /// <summary>The script wrote more than the read's limit. No output is delivered.</summary>
    OutputExceededLimit,

    /// <summary>The script did not finish within the read's timeout.</summary>
    TimedOut,

    /// <summary>The server could not be reached, the script could not be run, or its output was not UTF-8.</summary>
    Failed,
}

/// <summary>Why a remote read was refused before any SSH work.</summary>
public enum RemoteReadRefusal
{
    /// <summary>No registered read has that id (or it was left out at start).</summary>
    UnknownRead,

    /// <summary>The server's operating system is not the read's.</summary>
    WrongOperatingSystem,

    /// <summary>The argument breaks the rule of the read's <see cref="RemoteReadArgumentKind"/>.</summary>
    InvalidArgument,

    /// <summary>The argument is well formed but is not in the latest list collected for that server.</summary>
    NotListed,

    /// <summary>No such server, it is not monitored, or it has no credential.</summary>
    ServerUnavailable,

    /// <summary>Another read is already running on that server.</summary>
    Busy,
}

/// <summary>
/// One remote read. Output is present only for <see cref="RemoteReadOutcomeKind.Completed"/>, and stays on this
/// computer. Standard error is never returned.
/// </summary>
/// <param name="Kind">How it ended.</param>
/// <param name="Refusal">Why it was refused, for <see cref="RemoteReadOutcomeKind.Refused"/> only.</param>
/// <param name="ExitStatus">The exit status when the script exited, otherwise null.</param>
/// <param name="StandardOutput">Standard output, for a completed read only.</param>
public sealed record RemoteReadOutcome(
    RemoteReadOutcomeKind Kind,
    RemoteReadRefusal? Refusal,
    int? ExitStatus,
    string? StandardOutput)
{
    /// <summary>A refusal, with no exit status and no output.</summary>
    public static RemoteReadOutcome Refused(RemoteReadRefusal refusal) =>
        new(RemoteReadOutcomeKind.Refused, refusal, null, null);

    /// <summary>Never prints the output, so an outcome that reaches a log by accident carries no server data.</summary>
    public override string ToString() =>
        $"RemoteReadOutcome {{ Kind = {Kind}, Refusal = {Refusal}, ExitStatus = {ExitStatus} }}";
}
