namespace ServerMonitor.Core.Models;

public sealed record TrustedHostKey
{
    public required SshEndpoint Endpoint { get; init; }

    public required HostKeyIdentity Identity { get; init; }

    public DateTimeOffset ConfirmedAt { get; init; }
}
