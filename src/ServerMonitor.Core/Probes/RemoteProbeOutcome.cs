namespace ServerMonitor.Core.Probes;

/// <summary>How one probe run ended.</summary>
public enum RemoteProbeOutcomeKind
{
    /// <summary>The command ran and exited. <see cref="RemoteProbeOutcome.ExitStatus"/> may be non-zero.</summary>
    Completed,

    /// <summary>The command wrote more than the probe's limit. No output is delivered.</summary>
    OutputExceededLimit,

    /// <summary>The command did not finish within the probe's timeout.</summary>
    TimedOut,

    /// <summary>The command could not be run, or its output could not be read as UTF-8.</summary>
    Failed,
}

/// <summary>
/// One probe run on one server. Output is present only for <see cref="RemoteProbeOutcomeKind.Completed"/>, and stays on
/// this computer.
/// </summary>
/// <param name="ServerId">The server it ran on.</param>
/// <param name="ProbeId">The probe's id.</param>
/// <param name="HistoryGeneration">
/// The <see cref="History.HistoryGeneration"/> when the run started. Outcomes from an older generation are dropped
/// before they reach the probe; a probe that stores history may check it again when it writes.
/// </param>
/// <param name="CompletedAtUtc">When the run ended.</param>
/// <param name="Kind">How it ended.</param>
/// <param name="ExitStatus">The exit status when the command exited, otherwise null.</param>
/// <param name="StandardOutput">Standard output, for a completed run only.</param>
/// <param name="StandardError">Standard error (bounded by the SSH layer), for a completed run only.</param>
public sealed record RemoteProbeOutcome(
    Guid ServerId,
    string ProbeId,
    long HistoryGeneration,
    DateTimeOffset CompletedAtUtc,
    RemoteProbeOutcomeKind Kind,
    int? ExitStatus,
    string? StandardOutput,
    string? StandardError)
{
    /// <summary>Never prints the output, so an outcome that reaches a log by accident carries no server data.</summary>
    public override string ToString() =>
        $"RemoteProbeOutcome {{ ServerId = {ServerId}, ProbeId = {ProbeId}, Kind = {Kind}, ExitStatus = {ExitStatus} }}";
}

/// <summary>One probe to run in the current poll: the values the scheduler captured, never the probe itself.</summary>
/// <param name="ProbeId">The probe's id.</param>
/// <param name="Command">The constant command line.</param>
/// <param name="OutputLimitBytes">The most standard output to read, in bytes.</param>
/// <param name="Timeout">How long the run may take.</param>
public sealed record RemoteProbeRequest(string ProbeId, string Command, int OutputLimitBytes, TimeSpan Timeout);

/// <summary>What the SSH layer reports for one request.</summary>
/// <param name="WasExecuted">The command was started and its exit was observed.</param>
/// <param name="ExitStatus">Its exit status, when known.</param>
/// <param name="StandardOutput">Its standard output, or null when it was not valid UTF-8.</param>
/// <param name="StandardError">Its standard error, or null when it was not valid UTF-8.</param>
/// <param name="OutputExceededLimit">It wrote more than the request's limit.</param>
public sealed record RemoteProbeExecution(
    bool WasExecuted,
    int? ExitStatus,
    string? StandardOutput,
    string? StandardError,
    bool OutputExceededLimit)
{
    public override string ToString() =>
        $"RemoteProbeExecution {{ WasExecuted = {WasExecuted}, ExitStatus = {ExitStatus}, OutputExceededLimit = {OutputExceededLimit} }}";
}
