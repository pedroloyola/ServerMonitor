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

    public ServerOperatingSystem DetectedOperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public TimeSpan Duration { get; init; }

    public bool IsSuccess => State == ServerConnectionState.Connected;
}
