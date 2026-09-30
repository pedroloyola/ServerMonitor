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

    /// <summary>
    /// A <c>none</c>-auth host-key probe of a route's JUMP host, dialled direct at its own endpoint. No
    /// credential is involved; the presented key is checked against the direct store by the caller.
    /// </summary>
    ISshSession CreateJumpHostKeyProbe(SshDialTarget jump, TimeSpan timeout);
}

/// <summary>Where an SSH client dials and as whom. Never a secret.</summary>
internal readonly record struct SshDialTarget(string Host, int Port, string Username);

internal enum SshLoginKind
{
    None,
    Password,
    PrivateKey
}

/// <summary>
/// The credential material for one SSH login. Built only after the host key of the hop it is for has been
/// verified against the right store; never logged (<see cref="ToString"/> is redacted).
/// </summary>
internal sealed record SshLogin(
    SshLoginKind Kind,
    string? Password = null,
    string? PrivateKeyPath = null,
    string? Passphrase = null)
{
    public static SshLogin None { get; } = new(SshLoginKind.None);

    public override string ToString() => $"SshLogin {{ Kind = {Kind}, Secret = [REDACTED] }}";
}

/// <summary>
/// Creates an SSH.NET-backed session for an explicit dial target (the tunnel's loopback end), optionally
/// bracketing its TCP connect with a <see cref="ISshConnectGate"/>.
/// </summary>
internal interface ISshDialSessionFactory
{
    ISshSession Create(SshDialTarget dial, SshLogin login, TimeSpan timeout, ISshConnectGate? gate);
}

/// <summary>
/// Called immediately before and after a session's TCP/SSH connect. The jump tunnel arms its originator
/// gate for exactly one connection in <see cref="BeforeConnect"/> and seals it in <see cref="AfterConnect"/>.
/// </summary>
internal interface ISshConnectGate
{
    void BeforeConnect();

    void AfterConnect();
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

    /// <summary>The HostKeyReceived handler refused the presented key (never sent credentials).</summary>
    public bool HostKeyRejected { get; init; }

    /// <summary>
    /// The peer's SSH identification string was received (<c>ConnectionInfo.ServerVersion</c> set). Through a
    /// jump, false means the tunnel never carried a target: channel refused, jump-side connect failure, or the
    /// target closed before its banner (measured, M14.4b-2).
    /// </summary>
    public bool IdentificationReceived { get; init; }

    /// <summary>
    /// M14.5: the SSH connect, including user authentication, completed on this session; set even when a later
    /// step (a command, cancellation) fails. Lets "Test connection" report the step it reached, never guessed.
    /// </summary>
    public bool AuthenticationCompleted { get; init; }

    /// <summary>
    /// M14.5 D-1: the TCP connection was established, proved by the peer's identification, its key, or a failure
    /// SSH.NET only raises on an established connection (<see cref="SshExceptionMapper.ProvesEstablishedConnection"/>).
    /// False means "not proven", never "not connected". Through a jump tunnel it only proves the LOCAL forwarder.
    /// </summary>
    public bool ConnectionEstablished { get; init; }

    public bool IsSuccess => ErrorCode == SshConnectionErrorCode.None;
}

internal sealed class SshPrivateKeyLoadException : Exception
{
    public SshPrivateKeyLoadException(Exception innerException)
        : base("The private key could not be loaded.", innerException)
    {
    }
}
