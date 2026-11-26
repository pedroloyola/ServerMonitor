using System.Text;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// How one registered read is run: ONE exec of the same constant <see cref="ProbeShellInvocation.CommandText"/> as a
/// probe, with standard input holding one fixed first line, <c>set -- '&lt;argument&gt;'</c>, then the registered script
/// byte for byte. Only the POSIX <c>sh</c> reads it: the login shell never sees the argument, the server's process list
/// shows only <c>sh -s</c>, and the script is never edited. The argument is one single-quoted word, and
/// <see cref="RemoteReadArgument"/> makes a <c>'</c> impossible inside it, so <c>sh</c> takes it literally: <c>$#</c> is 1
/// and <c>"$1"</c> is the argument.
/// </summary>
internal static class RemoteReadInvocation
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Builds the exec for one read. Throws, without echoing the argument, when the argument is not safe.</summary>
    internal static RemoteReadExec Build(RemoteReadRegistration read, string argument)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new RemoteReadExec(
            ProbeShellInvocation.CommandText,
            StandardInput(read.ArgumentKind, argument, read.Command),
            read.OutputLimitBytes,
            read.Timeout);
    }

    /// <summary>The bytes written to standard input: the fixed first line, then the script, ending in one LF.</summary>
    internal static byte[] StandardInput(RemoteReadArgumentKind kind, string argument, string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        // Checked again here, at the last step, so no caller can ever put a quote or a line break in the first line.
        if (!RemoteReadArgument.IsValid(kind, argument))
        {
            throw new ArgumentException("The read argument does not meet its rule.", nameof(argument));
        }

        return Utf8.GetBytes("set -- '" + argument + "'\n" + (script.EndsWith('\n') ? script : script + "\n"));
    }
}

/// <summary>
/// One exec for a read: the command text, the standard input bytes, the output cap and the timeout.
/// <see cref="ToString"/> carries neither the standard input nor the argument in it.
/// </summary>
internal sealed record RemoteReadExec(string CommandText, byte[] StandardInput, int OutputLimitBytes, TimeSpan Timeout)
{
    public override string ToString() =>
        $"RemoteReadExec {{ CommandText = {CommandText}, OutputLimitBytes = {OutputLimitBytes}, Timeout = {Timeout} }}";
}

/// <summary>What the session reports for one read. Standard error is never kept.</summary>
internal sealed record RemoteReadExecution(
    bool WasExecuted,
    int? ExitStatus,
    string? StandardOutput,
    bool OutputExceededLimit,
    bool TimedOut)
{
    public override string ToString() =>
        $"RemoteReadExecution {{ WasExecuted = {WasExecuted}, ExitStatus = {ExitStatus}, OutputExceededLimit = {OutputExceededLimit}, TimedOut = {TimedOut} }}";
}

/// <summary>
/// Runs one read exec on a server in its own SSH session, by the same path as a collection (host key check, jump host,
/// credential freed in the same <c>finally</c>). The seam the runner's tests replace.
/// </summary>
internal interface IRemoteReadTransport
{
    Task<RemoteReadTransportResult> ExecuteAsync(
        Core.Models.Server server,
        RemoteReadExec exec,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken);
}

/// <summary>The connection's outcome and, when the session ran the exec, what it reported.</summary>
internal sealed record RemoteReadTransportResult(Core.Enums.SshConnectionErrorCode ErrorCode, RemoteReadExecution? Execution);
