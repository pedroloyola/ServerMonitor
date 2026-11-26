using Renci.SshNet;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// The part of one SSH exec that <see cref="SshNetSession"/> uses to run a command with standard input: start it, read
/// its two output streams, write its standard input and read its exit status. Production wraps an
/// <see cref="SshCommand"/>; a test substitutes it to drive the real session code without a server.
/// </summary>
internal interface IRemoteCommand : IDisposable
{
    Stream OutputStream { get; }

    Stream ExtendedOutputStream { get; }

    int? ExitStatus { get; }

    Task ExecuteAsync(CancellationToken cancellationToken);

    Stream CreateInputStream();
}

/// <summary>The production <see cref="IRemoteCommand"/>: one <see cref="SshCommand"/> of the session's client.</summary>
internal sealed class SshNetRemoteCommand(SshCommand command) : IRemoteCommand
{
    public Stream OutputStream => command.OutputStream;

    public Stream ExtendedOutputStream => command.ExtendedOutputStream;

    public int? ExitStatus => command.ExitStatus;

    public Task ExecuteAsync(CancellationToken cancellationToken) => command.ExecuteAsync(cancellationToken);

    public Stream CreateInputStream() => command.CreateInputStream();

    public void Dispose() => command.Dispose();

    internal static IRemoteCommand Create(SshClient client, string commandText, TimeSpan commandTimeout)
    {
        var command = client.CreateCommand(commandText);
        command.CommandTimeout = commandTimeout;
        return new SshNetRemoteCommand(command);
    }
}
