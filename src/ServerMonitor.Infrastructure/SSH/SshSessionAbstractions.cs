using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// Creates single-use SSH sessions. This seam keeps SSH.NET types out of the
/// application and enables deterministic orchestration tests without a server.
/// </summary>
internal interface ISshSessionFactory
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
internal interface ISshSession : IDisposable
{
    Task<SshSessionResult> ConnectAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken);

    Task<SshSessionResult> DetectOperatingSystemAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken);

    Task<SshSessionResult> CollectLinuxMetricsAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        TimeSpan cpuSampleInterval,
        CancellationToken cancellationToken);

    Task<SshSessionResult> CollectMacOsMetricsAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken);
}

internal sealed record SshSessionResult
{
    public required SshConnectionErrorCode ErrorCode { get; init; }

    public HostKeyIdentity? PresentedHostKey { get; init; }

    public ServerOperatingSystem DetectedOperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public LinuxMetricsRawData? LinuxMetrics { get; init; }

    public MacOsMetricsRawData? MacOsMetrics { get; init; }

    public string? ExceptionType { get; init; }

    public bool IsSuccess => ErrorCode == SshConnectionErrorCode.None;
}

internal sealed class SshPrivateKeyLoadException : Exception
{
    public SshPrivateKeyLoadException(Exception innerException)
        : base("The private key could not be loaded.", innerException)
    {
    }
}
