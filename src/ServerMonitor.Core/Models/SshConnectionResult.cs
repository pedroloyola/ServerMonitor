using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

public sealed record SshConnectionResult
{
    public required ServerConnectionState State { get; init; }

    public SshConnectionErrorCode ErrorCode { get; init; }

    public HostKeyIdentity? PresentedHostKey { get; init; }

    public TrustedHostKey? TrustedHostKey { get; init; }

    public ServerOperatingSystem DetectedOperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public TimeSpan Duration { get; init; }

    public bool IsSuccess => State == ServerConnectionState.Connected;
}
