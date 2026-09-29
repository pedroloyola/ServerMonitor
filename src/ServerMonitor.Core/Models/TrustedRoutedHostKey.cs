namespace ServerMonitor.Core.Models;

public sealed record TrustedRoutedHostKey
{
    public required SshRoute Route { get; init; }

    public required HostKeyIdentity Identity { get; init; }

    public DateTimeOffset ConfirmedAt { get; init; }
}
