using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.Collectors.Workloads;

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

    Task<SshSessionResult> CollectWorkloadsAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        WorkloadCollectionPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>
/// What one read-only workload pass should collect. Docker is independent of the service manager (§69).
/// The service manager is chosen from <see cref="OperatingSystem"/>, which is the server's <i>configured</i>
/// OS; when it is <see cref="ServerOperatingSystem.Auto"/> (or Unknown) the session resolves the effective
/// OS in-band via <c>uname -s</c> — no extra SSH session — before selecting the service commands. This is
/// command <i>selection</i>, not the <c>ServiceManager</c> routing decision, which stays in the Core policy.
/// </summary>
internal readonly record struct WorkloadCollectionPlan
{
    public bool IncludeDocker { get; init; }

    public bool IncludeContainerStats { get; init; }

    public ServerOperatingSystem OperatingSystem { get; init; }
}

internal sealed record SshSessionResult
{
    public required SshConnectionErrorCode ErrorCode { get; init; }

    public HostKeyIdentity? PresentedHostKey { get; init; }

    public ServerOperatingSystem DetectedOperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public LinuxMetricsRawData? LinuxMetrics { get; init; }

    public MacOsMetricsRawData? MacOsMetrics { get; init; }

    public WorkloadRawData? Workloads { get; init; }

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
