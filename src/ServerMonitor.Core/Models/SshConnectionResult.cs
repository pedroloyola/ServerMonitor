using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

public sealed record SshConnectionResult
{
    public required ServerConnectionState State { get; init; }

    public SshConnectionErrorCode ErrorCode { get; init; }

    public HostKeyIdentity? PresentedHostKey { get; init; }

    public TrustedHostKey? TrustedHostKey { get; init; }

    /// <summary>
    /// Which hop presented <see cref="PresentedHostKey"/> (M14.4b-2). Decides the only store a confirmed key
    /// may be written to; see <see cref="SshHostKeyHop"/>.
    /// </summary>
    public SshHostKeyHop HostKeyHop { get; init; } = SshHostKeyHop.Direct;

    /// <summary>
    /// The DIRECT-store identity of the key's hop: the server's endpoint for <see cref="SshHostKeyHop.Direct"/>,
    /// the jump host's endpoint for <see cref="SshHostKeyHop.Jump"/>. Null for <see cref="SshHostKeyHop.Target"/>.
    /// </summary>
    public SshEndpoint? HostKeyEndpoint { get; init; }

    /// <summary>The routed identity (jump endpoint, logical target) when <see cref="HostKeyHop"/> is Target.</summary>
    public SshRoute? HostKeyRoute { get; init; }

    /// <summary>The routed store's entry when a Target key mismatches it.</summary>
    public TrustedRoutedHostKey? TrustedRoutedHostKey { get; init; }

    /// <summary>
    /// M14.5 — the last "Test connection" step that completed (see <see cref="SshConnectionStage"/>). The step
    /// after it is the one that failed. Set by the connection service from observation, never guessed.
    /// <para>
    /// Monotonic race exception: the stage never drops below a step already reported through
    /// <see cref="SshConnectionRequest.StageProgress"/>. So when the host key changes between the probe and the
    /// authenticated connection (<see cref="SshConnectionErrorCode.HostKeyMismatch"/>), or the jump fails during
    /// the routed target's authentication (a <c>Jump*</c> code or
    /// <see cref="SshConnectionErrorCode.TargetUnreachableViaJump"/>), this can be
    /// <see cref="SshConnectionStage.HostKeyVerified"/>. Consumers decide the failed step by the
    /// <see cref="ErrorCode"/> family FIRST (host-key codes: the identity step; jump codes: the jump), and only
    /// then by this stage.
    /// </para>
    /// </summary>
    public SshConnectionStage ReachedStage { get; init; } = SshConnectionStage.None;

    public ServerOperatingSystem DetectedOperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public TimeSpan Duration { get; init; }

    public bool IsSuccess => State == ServerConnectionState.Connected;
}
