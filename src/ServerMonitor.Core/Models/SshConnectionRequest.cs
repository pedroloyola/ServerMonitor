using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Models;

public sealed class SshConnectionRequest
{
    public required Server Server { get; init; }

    public SecretValue? CredentialOverride { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public override string ToString() => $"SshConnectionRequest {{ ServerId = {Server.Id}, CredentialOverride = [REDACTED], Timeout = {Timeout} }}";
}
