using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.7 B-23): "Testar ligação" from a CLOSED catalogue, never a network. Each outcome reports the real
/// <see cref="SshConnectionStage"/>s in order up to its reached stage and then answers with the result the real service
/// would give for that family (direct, jump, routed target, host key unknown / mismatch, cancelled, unexpected). The
/// host-key-unknown outcomes consult the REAL trust stores of the QA root: once the editor's "Confiar e ligar" wrote the
/// key, the retest succeeds - so the two-step trust flow is exercised end to end without SSH. A <c>held</c> outcome keeps
/// the test running (the "a testar" state) until Cancel or the per-process release signal <see cref="ReleaseEventName"/>
/// is set; there is no wall clock. Nothing here writes anything.
/// </summary>
internal sealed class QaScriptedSshConnectionService(
    string outcome,
    bool held,
    IHostKeyTrustStore directTrust,
    IRoutedHostKeyTrustStore routedTrust) : ISshConnectionService
{
    public static IReadOnlyList<string> Outcomes { get; } =
    [
        "ok-linux", "ok-macos", "ok-unknown-os",
        "fail-dns", "fail-port", "fail-timeout", "fail-protocol", "fail-auth", "fail-key", "fail-credential",
        "jump-unreachable", "jump-auth", "jump-credential", "target-unreachable", "tunnel",
        "hostkey-unknown-direct", "hostkey-unknown-jump", "hostkey-unknown-target",
        // UI.7B: the M14.4b-2 two-step trust in one run - the jump's key until trusted, then the target's, then OK.
        "hostkey-unknown-jump-then-target",
        "hostkey-mismatch-direct", "hostkey-mismatch-jump", "hostkey-mismatch-target",
        "cancelled-at-auth", "unexpected"
    ];

    /// <summary>The named event a QA driver sets to release a held test (per process: never another instance's).</summary>
    public static string ReleaseEventName => $"Local\\ServerMonitor-QA-Editor-Release-{Environment.ProcessId}";

    // Synthetic identities, so a mismatch shows two different fingerprints. Never a real host key.
    private static readonly HostKeyIdentity Presented = HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Fingerprint(0x51));
    private static readonly HostKeyIdentity Other = HostKeyIdentity.Create("ssh-ed25519", "SHA256:" + Fingerprint(0xA7));

    public string Outcome { get; } = outcome;

    public bool Held { get; } = held;

    public Task<SshConnectionResult> ConnectAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SshConnectionResult { State = ServerConnectionState.Error, ErrorCode = SshConnectionErrorCode.Unexpected });

    public Task<SshConnectionResult> DetectOperatingSystemAsync(SshConnectionRequest request, CancellationToken cancellationToken = default) =>
        ConnectAsync(request, cancellationToken);

    public async Task<SshConnectionResult> TestConnectionAsync(SshConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ResultForAsync(request.Server, cancellationToken);
        Report(request, result.ReachedStage);
        if (Held || Outcome == "cancelled-at-auth")
        {
            await WaitForReleaseAsync(cancellationToken);
        }

        return Outcome == "unexpected" ? throw new InvalidOperationException("QA scripted unexpected failure.") : result;
    }

    private async Task<SshConnectionResult> ResultForAsync(Server server, CancellationToken cancellationToken)
    {
        var direct = SshEndpoint.Create(server.Host, server.Port);
        var jump = server.Route?.Jump is { } hop ? SshEndpoint.Create(hop.Host, hop.Port) : null;
        var route = jump is null ? null : SshRoute.Create(jump, direct);
        switch (Outcome)
        {
            case "ok-linux": return Connected(ServerOperatingSystem.Linux);
            case "ok-macos": return Connected(ServerOperatingSystem.MacOS);
            case "ok-unknown-os":
                return new SshConnectionResult { State = ServerConnectionState.Connected, ReachedStage = SshConnectionStage.Authenticated };
            case "fail-dns": return Failure(SshConnectionErrorCode.DnsResolutionFailed, SshConnectionStage.None);
            case "fail-port": return Failure(SshConnectionErrorCode.ConnectionRefused, SshConnectionStage.None);
            case "fail-timeout": return Failure(SshConnectionErrorCode.ConnectionTimedOut, SshConnectionStage.None);
            case "fail-protocol": return Failure(SshConnectionErrorCode.ProtocolError, SshConnectionStage.PortReachable);
            case "fail-auth": return Failure(SshConnectionErrorCode.AuthenticationFailed, SshConnectionStage.HostKeyVerified);
            case "fail-key": return Failure(SshConnectionErrorCode.PrivateKeyInvalid, SshConnectionStage.HostKeyVerified);
            case "fail-credential": return Failure(SshConnectionErrorCode.CredentialUnavailable, SshConnectionStage.HostKeyVerified);
            case "jump-unreachable": return Failure(SshConnectionErrorCode.JumpConnectionFailed, SshConnectionStage.None);
            case "jump-auth": return Failure(SshConnectionErrorCode.JumpAuthenticationFailed, SshConnectionStage.None);
            case "jump-credential": return Failure(SshConnectionErrorCode.JumpCredentialUnavailable, SshConnectionStage.None);
            case "target-unreachable": return Failure(SshConnectionErrorCode.TargetUnreachableViaJump, SshConnectionStage.None);
            case "tunnel": return Failure(SshConnectionErrorCode.LocalTunnelFailed, SshConnectionStage.None);
            case "cancelled-at-auth": return Connected(ServerOperatingSystem.Linux) with { ReachedStage = SshConnectionStage.HostKeyVerified };
            case "unexpected": return Failure(SshConnectionErrorCode.Unexpected, SshConnectionStage.None);
            case "hostkey-unknown-direct" when route is null:
                return await directTrust.GetAsync(direct, cancellationToken) is null
                    ? Unknown(SshHostKeyHop.Direct, SshConnectionErrorCode.HostKeyUnknown, direct, null)
                    : Connected(ServerOperatingSystem.Linux);
            case "hostkey-unknown-jump" when jump is not null:
                return await directTrust.GetAsync(jump, cancellationToken) is null
                    ? Unknown(SshHostKeyHop.Jump, SshConnectionErrorCode.JumpHostKeyUnknown, jump, null)
                    : Connected(ServerOperatingSystem.Linux);
            case "hostkey-unknown-target" when route is not null:
                return await routedTrust.GetAsync(route, cancellationToken) is null
                    ? Unknown(SshHostKeyHop.Target, SshConnectionErrorCode.RoutedHostKeyUnknown, null, route)
                    : Connected(ServerOperatingSystem.Linux);
            case "hostkey-unknown-jump-then-target" when jump is not null && route is not null:
                if (await directTrust.GetAsync(jump, cancellationToken) is null)
                {
                    return Unknown(SshHostKeyHop.Jump, SshConnectionErrorCode.JumpHostKeyUnknown, jump, null);
                }

                return await routedTrust.GetAsync(route, cancellationToken) is null
                    ? Unknown(SshHostKeyHop.Target, SshConnectionErrorCode.RoutedHostKeyUnknown, null, route)
                    : Connected(ServerOperatingSystem.Linux);
            case "hostkey-mismatch-direct" when route is null:
                return Mismatch(SshHostKeyHop.Direct, SshConnectionErrorCode.HostKeyMismatch, SshConnectionStage.PortReachable) with
                {
                    HostKeyEndpoint = direct,
                    TrustedHostKey = new TrustedHostKey { Endpoint = direct, Identity = Other }
                };
            case "hostkey-mismatch-jump" when jump is not null:
                return Mismatch(SshHostKeyHop.Jump, SshConnectionErrorCode.JumpHostKeyMismatch, SshConnectionStage.None) with
                {
                    HostKeyEndpoint = jump,
                    TrustedHostKey = new TrustedHostKey { Endpoint = jump, Identity = Other }
                };
            case "hostkey-mismatch-target" when route is not null:
                return Mismatch(SshHostKeyHop.Target, SshConnectionErrorCode.RoutedHostKeyMismatch, SshConnectionStage.PortReachable) with
                {
                    HostKeyRoute = route,
                    TrustedRoutedHostKey = new TrustedRoutedHostKey { Route = route, Identity = Other }
                };
            default:
                // A hop outcome on a form without that hop (e.g. a jump outcome for a direct server).
                return Failure(SshConnectionErrorCode.InvalidConfiguration, SshConnectionStage.None);
        }
    }

    private static void Report(SshConnectionRequest request, SshConnectionStage reached)
    {
        for (var stage = SshConnectionStage.PortReachable; stage <= reached; stage++)
        {
            request.StageProgress?.Report(stage);
        }
    }

    private static async Task WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        using var release = new EventWaitHandle(false, EventResetMode.AutoReset, ReleaseEventName);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(release, (_, _) => signal.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await signal.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    private static SshConnectionResult Connected(ServerOperatingSystem system) => new()
    {
        State = ServerConnectionState.Connected,
        ReachedStage = SshConnectionStage.OperatingSystemIdentified,
        DetectedOperatingSystem = system
    };

    private static SshConnectionResult Failure(SshConnectionErrorCode code, SshConnectionStage reached) => new()
    {
        State = code is SshConnectionErrorCode.AuthenticationFailed or SshConnectionErrorCode.JumpAuthenticationFailed
            ? ServerConnectionState.AuthenticationFailed
            : ServerConnectionState.Error,
        ErrorCode = code,
        ReachedStage = reached
    };

    private static SshConnectionResult Unknown(SshHostKeyHop hop, SshConnectionErrorCode code, SshEndpoint? endpoint, SshRoute? route) => new()
    {
        State = ServerConnectionState.HostKeyUnknown,
        ErrorCode = code,
        ReachedStage = hop == SshHostKeyHop.Jump ? SshConnectionStage.None : SshConnectionStage.PortReachable,
        PresentedHostKey = Presented,
        HostKeyHop = hop,
        HostKeyEndpoint = endpoint,
        HostKeyRoute = route
    };

    private static SshConnectionResult Mismatch(SshHostKeyHop hop, SshConnectionErrorCode code, SshConnectionStage reached) => new()
    {
        State = ServerConnectionState.HostKeyMismatch,
        ErrorCode = code,
        ReachedStage = reached,
        PresentedHostKey = Presented,
        HostKeyHop = hop
    };

    private static string Fingerprint(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()).TrimEnd('=');
}
