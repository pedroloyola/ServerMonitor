using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// Creates single-use SSH sessions. This seam keeps SSH.NET types out of the
/// application and enables deterministic orchestration tests without a server.
/// </summary>
public interface ISshSessionFactory
{
    ISshSession CreateHostKeyProbe(Server server, TimeSpan timeout);

    ISshSession CreatePasswordSession(Server server, string password, TimeSpan timeout);

    ISshSession CreatePrivateKeySession(
        Server server,
        string privateKeyPath,
        string? passphrase,
        TimeSpan timeout);
}

/// <summary>
/// Represents one connection attempt. It deliberately exposes no arbitrary
/// command execution API.
/// </summary>
public interface ISshSession : IDisposable
{
    Task<SshSessionResult> ConnectAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken);

    Task<SshSessionResult> DetectOperatingSystemAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken);
}

public sealed record SshSessionResult
{
    public required SshConnectionErrorCode ErrorCode { get; init; }

    public HostKeyIdentity? PresentedHostKey { get; init; }

    public ServerOperatingSystem DetectedOperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public string? ExceptionType { get; init; }

    public bool IsSuccess => ErrorCode == SshConnectionErrorCode.None;
}

public sealed class SshPrivateKeyLoadException : Exception
{
    public SshPrivateKeyLoadException(Exception innerException)
        : base("The private key could not be loaded.", innerException)
    {
    }
}
