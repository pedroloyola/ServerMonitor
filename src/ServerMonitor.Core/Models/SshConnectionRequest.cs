using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Models;

public sealed class SshConnectionRequest
{
    public required Server Server { get; init; }

    public SecretValue? CredentialOverride { get; init; }

    /// <summary>
    /// An unsaved secret for the route's jump host (editor "test connection"). Independent of
    /// <see cref="CredentialOverride"/>. Not consumed until the ProxyJump transport exists (M14.4b-2).
    /// </summary>
    public SecretValue? JumpCredentialOverride { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public override string ToString() => $"SshConnectionRequest {{ ServerId = {Server.Id}, CredentialOverride = [REDACTED], JumpCredentialOverride = [REDACTED], Timeout = {Timeout} }}";
}
