using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Models;

public sealed class SshConnectionRequest
{
    public required Server Server { get; init; }

    public SecretValue? CredentialOverride { get; init; }

    /// <summary>
    /// An unsaved secret for the route's jump host (editor "test connection"). Independent of
    /// <see cref="CredentialOverride"/>. Consumed by the ProxyJump transport (M14.4b-2).
    /// </summary>
    public SecretValue? JumpCredentialOverride { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// M14.5 — optional live progress for "Test connection": reported once per <see cref="SshConnectionStage"/> as
    /// it completes, in order, never skipping one. Monitoring and collection never set it.
    /// </summary>
    public IProgress<SshConnectionStage>? StageProgress { get; init; }

    public override string ToString() => $"SshConnectionRequest {{ ServerId = {Server.Id}, CredentialOverride = [REDACTED], JumpCredentialOverride = [REDACTED], Timeout = {Timeout} }}";
}
