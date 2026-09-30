using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Infrastructure.SSH;

public sealed class SshConnectionService
    : ISshConnectionService, ILinuxMetricsRemoteSource, IMacOsMetricsRemoteSource, IWorkloadRemoteSource
{
    private readonly IHostKeyTrustStore _hostKeyTrustStore;
    private readonly IRoutedHostKeyTrustStore _routedHostKeyTrustStore;
    private readonly IServerCredentialStore _credentialStore;
    private readonly ILogger<SshConnectionService> _logger;
    private readonly ISshSessionFactory _sessionFactory;
    private readonly IJumpTunnelFactory _tunnelFactory;
    private readonly TimeProvider _timeProvider;

    public SshConnectionService(
        IHostKeyTrustStore hostKeyTrustStore,
        IRoutedHostKeyTrustStore routedHostKeyTrustStore,
        IServerCredentialStore credentialStore,
        ILogger<SshConnectionService> logger)
        : this(hostKeyTrustStore, routedHostKeyTrustStore, credentialStore, logger, new SshNetSessionFactory(), null)
    {
    }

    internal SshConnectionService(
        IHostKeyTrustStore hostKeyTrustStore,
        IRoutedHostKeyTrustStore routedHostKeyTrustStore,
        IServerCredentialStore credentialStore,
        ILogger<SshConnectionService> logger,
        ISshSessionFactory sessionFactory,
        IJumpTunnelFactory? tunnelFactory,
        TimeProvider? timeProvider = null)
    {
        _hostKeyTrustStore = hostKeyTrustStore ?? throw new ArgumentNullException(nameof(hostKeyTrustStore));
        _routedHostKeyTrustStore = routedHostKeyTrustStore ?? throw new ArgumentNullException(nameof(routedHostKeyTrustStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _tunnelFactory = tunnelFactory ?? new SshNetJumpTunnelFactory(
            _sessionFactory as ISshDialSessionFactory ?? new SshNetSessionFactory(),
            _logger);
        // The operation deadline's clock; production is always the system clock (tests drive it explicitly).
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SshConnectionResult> ConnectAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, SshSessionOperation.Connect, TimeSpan.Zero, null, default, cancellationToken);

    public Task<SshConnectionResult> TestConnectionAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, SshSessionOperation.DetectOperatingSystem, TimeSpan.Zero, null, default, cancellationToken);

    public Task<SshConnectionResult> DetectOperatingSystemAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, SshSessionOperation.DetectOperatingSystem, TimeSpan.Zero, null, default, cancellationToken);

    public async Task<LinuxMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan cpuSampleInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        LinuxMetricsRawData? data = null;
        var request = new SshConnectionRequest
        {
            Server = server,
            Timeout = timeout
        };

        if (cpuSampleInterval <= TimeSpan.Zero || cpuSampleInterval > TimeSpan.FromSeconds(5))
        {
            return new LinuxMetricsRemoteResult
            {
                ConnectionResult = Complete(
                    server,
                    SshConnectionErrorCode.InvalidConfiguration,
                    TimeSpan.Zero,
                    timeout)
            };
        }

        var connectionResult = await ExecuteAsync(
                request,
                SshSessionOperation.CollectLinuxMetrics,
                cpuSampleInterval,
                session => data = session.LinuxMetrics,
                default,
                cancellationToken)
            .ConfigureAwait(false);

        return new LinuxMetricsRemoteResult
        {
            ConnectionResult = connectionResult,
            Data = data
        };
    }

    public async Task<MacOsMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        MacOsMetricsRawData? data = null;
        var request = new SshConnectionRequest
        {
            Server = server,
            Timeout = timeout
        };

        var connectionResult = await ExecuteAsync(
                request,
                SshSessionOperation.CollectMacOsMetrics,
                TimeSpan.Zero,
                session => data = session.MacOsMetrics,
                default,
                cancellationToken)
            .ConfigureAwait(false);

        return new MacOsMetricsRemoteResult
        {
            ConnectionResult = connectionResult,
            Data = data
        };
    }

    public async Task<WorkloadRemoteResult> CollectAsync(
        Server server,
        WorkloadRemoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(request);

        WorkloadRawData? data = null;
        var connectionRequest = new SshConnectionRequest
        {
            Server = server,
            Timeout = request.Timeout
        };

        // Docker is probed independently of the OS (§69). The service manager commands are selected inside
        // the session from the effective OS: the configured OS is passed through, and an Auto/Unknown
        // server is resolved there via uname (no extra session). Mapping the OS to a ServiceManager stays
        // in the Core policy.
        var plan = new WorkloadCollectionPlan
        {
            IncludeDocker = request.IncludeDocker,
            IncludeContainerStats = request.IncludeContainerStats,
            OperatingSystem = server.OperatingSystem
        };

        var connectionResult = await ExecuteAsync(
                connectionRequest,
                SshSessionOperation.CollectWorkloads,
                TimeSpan.Zero,
                session => data = session.Workloads,
                plan,
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkloadRemoteResult
        {
            ConnectionResult = connectionResult,
            Data = data
        };
    }

    private async Task<SshConnectionResult> ExecuteAsync(
        SshConnectionRequest request,
        SshSessionOperation operation,
        TimeSpan cpuSampleInterval,
        Action<SshSessionResult>? captureSession,
        WorkloadCollectionPlan workloadPlan,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // M14.5: every result carries the last step that was OBSERVED to complete; the optional live progress
        // sees exactly those steps, in order. Monitoring and collection requests never carry a progress sink.
        var stages = new StageTracker(request?.StageProgress, _logger);

        // A routed server is ONLY ever dialled through its jump host. Anything carrying a route - valid or
        // not - takes the routed path, which fails closed; it never falls through to a direct dial.
        if (request?.Server is { Route: not null })
        {
            return await ExecuteRoutedAsync(
                    request,
                    operation,
                    cpuSampleInterval,
                    captureSession,
                    workloadPlan,
                    stopwatch,
                    stages,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!TryValidate(request, out var endpoint))
        {
            return Complete(
                request?.Server,
                SshConnectionErrorCode.InvalidConfiguration,
                stopwatch.Elapsed,
                request?.Timeout ?? TimeSpan.Zero,
                stages: stages);
        }

        using var timeoutSource = new CancellationTokenSource(request.Timeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            var trustedHostKey = await _hostKeyTrustStore
                .GetAsync(endpoint, linkedSource.Token)
                .ConfigureAwait(false);

            var probe = await ProbeHostKeyAsync(
                    request.Server,
                    trustedHostKey,
                    request.Timeout,
                    linkedSource.Token)
                .ConfigureAwait(false);

            AdvanceAfterProbe(stages, probe, trustedHostKey?.Identity, establishedConnectionCounts: true);

            if (trustedHostKey is null && probe.PresentedHostKey is not null)
            {
                return Complete(
                    request.Server,
                    SshConnectionErrorCode.HostKeyUnknown,
                    stopwatch.Elapsed,
                    request.Timeout,
                    probe.PresentedHostKey,
                    hostKeyEndpoint: endpoint,
                    stages: stages);
            }

            if (trustedHostKey is not null &&
                probe.PresentedHostKey is not null &&
                !trustedHostKey.Identity.Matches(probe.PresentedHostKey))
            {
                return Complete(
                    request.Server,
                    SshConnectionErrorCode.HostKeyMismatch,
                    stopwatch.Elapsed,
                    request.Timeout,
                    probe.PresentedHostKey,
                    trustedHostKey,
                    hostKeyEndpoint: endpoint,
                    stages: stages);
            }

            if (probe.PresentedHostKey is null ||
                probe.ErrorCode is not (SshConnectionErrorCode.None or SshConnectionErrorCode.AuthenticationFailed))
            {
                return Complete(
                    request.Server,
                    NormalizeCancellation(probe.ErrorCode, cancellationToken, timeoutSource),
                    stopwatch.Elapsed,
                    request.Timeout,
                    probe.PresentedHostKey,
                    trustedHostKey,
                    exceptionType: probe.ExceptionType,
                    stages: stages);
            }

            var server = request.Server;
            var (sessionResult, _) = await ConnectAuthenticatedAsync(
                    request,
                    identity => trustedHostKey!.Identity.Matches(identity),
                    login => login.Kind == SshLoginKind.Password
                        ? _sessionFactory.CreatePasswordSession(server, login.Password!, request.Timeout)
                        : _sessionFactory.CreatePrivateKeySession(
                            server,
                            login.PrivateKeyPath!,
                            login.Passphrase,
                            request.Timeout),
                    operation,
                    cpuSampleInterval,
                    workloadPlan,
                    linkedSource.Token)
                .ConfigureAwait(false);

            captureSession?.Invoke(sessionResult);
            AdvanceAfterAuthenticatedSession(stages, sessionResult);

            return Complete(
                request.Server,
                NormalizeCancellation(sessionResult.ErrorCode, cancellationToken, timeoutSource),
                stopwatch.Elapsed,
                request.Timeout,
                sessionResult.PresentedHostKey,
                trustedHostKey,
                sessionResult.DetectedOperatingSystem,
                sessionResult.ExceptionType,
                stages: stages);
        }
        catch (OperationCanceledException exception)
        {
            var error = cancellationToken.IsCancellationRequested
                ? SshConnectionErrorCode.Cancelled
                : SshConnectionErrorCode.ConnectionTimedOut;
            return Complete(
                request.Server,
                error,
                stopwatch.Elapsed,
                request.Timeout,
                exceptionType: exception.GetType().Name,
                stages: stages);
        }
        catch (Exception exception)
        {
            return Complete(
                request.Server,
                SshExceptionMapper.Map(exception),
                stopwatch.Elapsed,
                request.Timeout,
                exceptionType: exception.GetType().Name,
                stages: stages);
        }
    }

    /// <summary>
    /// After a host-key probe (direct, or the routed TARGET through the tunnel): the port answered only if the
    /// peer presented a key, its SSH identification line arrived or (direct only, M14.5 D-1) the probe failed in
    /// a way that proves an established TCP connection (<see cref="SshSessionResult.ConnectionEstablished"/>).
    /// A probe that failed before any of these (DNS, refused, unreachable, timeout) proves nothing and stays at
    /// <see cref="SshConnectionStage.None"/>. The key is verified ONLY when the presented key matches the stored
    /// trusted key; first sight (no trusted key) never counts.
    /// </summary>
    private static void AdvanceAfterProbe(
        StageTracker stages,
        SshSessionResult probe,
        HostKeyIdentity? trustedIdentity,
        bool establishedConnectionCounts)
    {
        if (probe.PresentedHostKey is null
            && !probe.IdentificationReceived
            && !(establishedConnectionCounts && probe.ConnectionEstablished))
        {
            return;
        }

        stages.Advance(SshConnectionStage.PortReachable);

        if (trustedIdentity is not null
            && probe.PresentedHostKey is { } presented
            && trustedIdentity.Matches(presented))
        {
            stages.Advance(SshConnectionStage.HostKeyVerified);
        }
    }

    /// <summary>
    /// After the authenticated session: authenticated when the session says the SSH connect (which includes
    /// user authentication) completed, or it succeeded; the OS step only for an identified Linux/macOS. An
    /// unknown OS is still a success that stops at <see cref="SshConnectionStage.Authenticated"/>.
    /// </summary>
    private static void AdvanceAfterAuthenticatedSession(StageTracker stages, SshSessionResult session)
    {
        if (!session.IsSuccess && !session.AuthenticationCompleted)
        {
            return;
        }

        stages.Advance(SshConnectionStage.Authenticated);

        if (session.IsSuccess
            && session.DetectedOperatingSystem is ServerOperatingSystem.Linux or ServerOperatingSystem.MacOS)
        {
            stages.Advance(SshConnectionStage.OperatingSystemIdentified);
        }
    }

    private async Task<SshSessionResult> ProbeHostKeyAsync(
        Server server,
        TrustedHostKey? trustedHostKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var session = _sessionFactory.CreateHostKeyProbe(server, timeout);
        return await session.ConnectAsync(
                identity => trustedHostKey is not null && trustedHostKey.Identity.Matches(identity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// M14.4b-2: one single-hop ProxyJump operation. Probe-before-auth holds for BOTH hops: the jump's key is
    /// checked against the DIRECT store (jump endpoint) before any jump credential is read; the target's key is
    /// checked, through the tunnel, against the ROUTED store (jump endpoint + LOGICAL target, never the loopback)
    /// before any target credential is read. Nothing is ever trusted automatically on this path. One tunnel per
    /// operation, torn down (await using) before this method returns; one linked deadline spans both hops.
    /// </summary>
    private async Task<SshConnectionResult> ExecuteRoutedAsync(
        SshConnectionRequest request,
        SshSessionOperation operation,
        TimeSpan cpuSampleInterval,
        Action<SshSessionResult>? captureSession,
        WorkloadCollectionPlan workloadPlan,
        Stopwatch stopwatch,
        StageTracker stages,
        CancellationToken cancellationToken)
    {
        var server = request.Server;
        if (!TryValidate(request, out var targetEndpoint)
            || server.Route?.Jump is not { } jump
            || !TryValidateJump(jump, out var jumpEndpoint))
        {
            return Complete(server, SshConnectionErrorCode.InvalidConfiguration, stopwatch.Elapsed, request.Timeout, stages: stages);
        }

        var route = SshRoute.Create(jumpEndpoint, targetEndpoint);
        var jumpDial = new SshDialTarget(jump.Host.Trim(), jump.Port, jump.Username.Trim());

        using var timeoutSource = new CancellationTokenSource(request.Timeout, _timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linkedSource.Token;

        try
        {
            // ---- Hop 1: the jump host, verified against the DIRECT store under its own endpoint. ----
            var trustedJumpKey = await _hostKeyTrustStore.GetAsync(jumpEndpoint, token).ConfigureAwait(false);
            SshSessionResult jumpProbe;
            using (var probe = _sessionFactory.CreateJumpHostKeyProbe(jumpDial, request.Timeout))
            {
                jumpProbe = await probe.ConnectAsync(
                        identity => trustedJumpKey is not null && trustedJumpKey.Identity.Matches(identity),
                        token)
                    .ConfigureAwait(false);
            }

            if (trustedJumpKey is null && jumpProbe.PresentedHostKey is not null)
            {
                return CompleteHop(server, SshConnectionErrorCode.JumpHostKeyUnknown, stopwatch, request,
                    jumpProbe.PresentedHostKey, SshHostKeyHop.Jump, endpoint: jumpEndpoint, stages: stages);
            }

            if (trustedJumpKey is not null && jumpProbe.PresentedHostKey is not null
                && !trustedJumpKey.Identity.Matches(jumpProbe.PresentedHostKey))
            {
                return CompleteHop(server, SshConnectionErrorCode.JumpHostKeyMismatch, stopwatch, request,
                    jumpProbe.PresentedHostKey, SshHostKeyHop.Jump, endpoint: jumpEndpoint, trusted: trustedJumpKey, stages: stages);
            }

            if (jumpProbe.PresentedHostKey is null
                || jumpProbe.ErrorCode is not (SshConnectionErrorCode.None or SshConnectionErrorCode.AuthenticationFailed))
            {
                return Complete(
                    server,
                    ClassifyJump(jumpProbe, hostKeyUnknown: trustedJumpKey is null, cancellationToken, timeoutSource),
                    stopwatch.Elapsed,
                    request.Timeout,
                    exceptionType: jumpProbe.ExceptionType, stages: stages);
            }

            var verifiedJumpKey = trustedJumpKey!;

            // The jump key is trusted: only now is the jump credential read.
            var (jumpLogin, jumpSecret) = await ResolveJumpLoginAsync(server, jump, request.JumpCredentialOverride, token)
                .ConfigureAwait(false);
            IJumpTunnel tunnel;
            try
            {
                if (jumpLogin is null)
                {
                    return Complete(server, SshConnectionErrorCode.JumpCredentialUnavailable, stopwatch.Elapsed, request.Timeout, stages: stages);
                }

                try
                {
                    tunnel = _tunnelFactory.Create(jumpDial, jumpLogin, targetEndpoint, request.Timeout);
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException
                                                      or SshPrivateKeyLoadException)
                {
                    return Complete(server, SshConnectionErrorCode.JumpCredentialUnavailable, stopwatch.Elapsed,
                        request.Timeout, exceptionType: exception.GetType().Name, stages: stages);
                }
            }
            finally
            {
                jumpSecret?.Dispose();
            }

            await using (tunnel.ConfigureAwait(false))
            {
                var open = await tunnel
                    .OpenAsync(identity => verifiedJumpKey.Identity.Matches(identity), token)
                    .ConfigureAwait(false);
                if (!open.IsOpen)
                {
                    // The jump presented a different key on the authenticated connection than on the probe:
                    // a JUMP mismatch, reported for the jump hop (L2) — never as the target.
                    if (open.Stage == JumpTunnelOpenStage.Jump
                        && open.JumpResult is { PresentedHostKey: { } changedJumpKey } rejectedJump
                        && (rejectedJump.HostKeyRejected || rejectedJump.ErrorCode == SshConnectionErrorCode.HostKeyMismatch)
                        && !cancellationToken.IsCancellationRequested
                        && !timeoutSource.IsCancellationRequested)
                    {
                        return CompleteHop(server, SshConnectionErrorCode.JumpHostKeyMismatch, stopwatch, request,
                            changedJumpKey, SshHostKeyHop.Jump, endpoint: jumpEndpoint, trusted: verifiedJumpKey, stages: stages);
                    }

                    var openCode = open.Stage == JumpTunnelOpenStage.TunnelListen
                        ? ProxyJumpFailureClassifier.Classify(new ProxyJumpFailure
                        {
                            Stage = ProxyJumpStage.TunnelListen,
                            Cancelled = cancellationToken.IsCancellationRequested
                        })
                        : ClassifyJump(open.JumpResult!, hostKeyUnknown: false, cancellationToken, timeoutSource);
                    return Complete(server, openCode, stopwatch.Elapsed, request.Timeout,
                        exceptionType: open.ExceptionType ?? open.JumpResult?.ExceptionType, stages: stages);
                }

                // ---- Hop 2: the target through the tunnel, verified against the ROUTED store. ----
                var trustedTargetKey = await _routedHostKeyTrustStore.GetAsync(route, token).ConfigureAwait(false);
                SshSessionResult targetProbe;
                using (var probe = tunnel.CreateTargetSession(server.Username.Trim(), SshLogin.None, request.Timeout))
                {
                    targetProbe = await probe.ConnectAsync(
                            identity => trustedTargetKey is not null && trustedTargetKey.Identity.Matches(identity),
                            token)
                        .ConfigureAwait(false);
                }

                // The steps describe the TARGET as reached through the jump. A probe that ends in a jump-stage
                // failure (the jump or the tunnel, not the target) reached nothing, whatever it saw.
                var targetProbeFailure = targetProbe.PresentedHostKey is null
                                         || targetProbe.ErrorCode is not (SshConnectionErrorCode.None or SshConnectionErrorCode.AuthenticationFailed)
                    ? ClassifyTarget(targetProbe, trustedTargetKey is null, tunnel, cancellationToken, timeoutSource)
                    : (SshConnectionErrorCode?)null;
                if (targetProbeFailure is not { } failure || !IsJumpStageFailure(failure))
                {
                    // Through the tunnel an established connection only proves the LOCAL forwarder, never the target.
                    AdvanceAfterProbe(stages, targetProbe, trustedTargetKey?.Identity, establishedConnectionCounts: false);
                }

                if (trustedTargetKey is null && targetProbe.PresentedHostKey is not null)
                {
                    return CompleteHop(server, SshConnectionErrorCode.RoutedHostKeyUnknown, stopwatch, request,
                        targetProbe.PresentedHostKey, SshHostKeyHop.Target, route: route, stages: stages);
                }

                if (trustedTargetKey is not null && targetProbe.PresentedHostKey is not null
                    && !trustedTargetKey.Identity.Matches(targetProbe.PresentedHostKey))
                {
                    return CompleteHop(server, SshConnectionErrorCode.RoutedHostKeyMismatch, stopwatch, request,
                        targetProbe.PresentedHostKey, SshHostKeyHop.Target, route: route, trustedRouted: trustedTargetKey, stages: stages);
                }

                if (targetProbe.PresentedHostKey is null
                    || targetProbe.ErrorCode is not (SshConnectionErrorCode.None or SshConnectionErrorCode.AuthenticationFailed))
                {
                    return Complete(
                        server,
                        targetProbeFailure!.Value,
                        stopwatch.Elapsed,
                        request.Timeout,
                        exceptionType: targetProbe.ExceptionType, stages: stages);
                }

                var verifiedTargetKey = trustedTargetKey!;
                var username = server.Username.Trim();
                var (sessionResult, sessionRan) = await ConnectAuthenticatedAsync(
                        request,
                        identity => verifiedTargetKey.Identity.Matches(identity),
                        login => tunnel.CreateTargetSession(username, login, request.Timeout),
                        operation,
                        cpuSampleInterval,
                        workloadPlan,
                        token)
                    .ConfigureAwait(false);

                captureSession?.Invoke(sessionResult);
                AdvanceAfterAuthenticatedSession(stages, sessionResult);

                var code = sessionResult.IsSuccess || !sessionRan
                    ? sessionResult.ErrorCode
                    : ClassifyTarget(sessionResult, hostKeyUnknown: false, tunnel, cancellationToken, timeoutSource);
                return Complete(
                    server,
                    code,
                    stopwatch.Elapsed,
                    request.Timeout,
                    detectedOperatingSystem: sessionResult.DetectedOperatingSystem,
                    exceptionType: sessionResult.ExceptionType, stages: stages);
            }
        }
        catch (OperationCanceledException exception)
        {
            var error = cancellationToken.IsCancellationRequested
                ? SshConnectionErrorCode.Cancelled
                : SshConnectionErrorCode.ConnectionTimedOut;
            return Complete(server, error, stopwatch.Elapsed, request.Timeout, exceptionType: exception.GetType().Name, stages: stages);
        }
        catch (Exception exception)
        {
            // Never a direct diagnosis for a routed server.
            return Complete(server, SshConnectionErrorCode.Unexpected, stopwatch.Elapsed, request.Timeout,
                exceptionType: exception.GetType().Name, stages: stages);
        }
    }

    private static SshConnectionErrorCode ClassifyJump(
        SshSessionResult result,
        bool hostKeyUnknown,
        CancellationToken callerToken,
        CancellationTokenSource timeoutSource) =>
        ProxyJumpFailureClassifier.Classify(new ProxyJumpFailure
        {
            Stage = ProxyJumpStage.Jump,
            Cancelled = callerToken.IsCancellationRequested,
            // L3: the linked deadline expiring during the jump probe/open is a timeout, as on the other paths.
            TimedOut = timeoutSource.IsCancellationRequested,
            IdentificationReceived = result.IdentificationReceived,
            HostKeyRejected = result.HostKeyRejected || result.ErrorCode == SshConnectionErrorCode.HostKeyMismatch,
            HostKeyUnknown = hostKeyUnknown,
            MappedCode = result.ErrorCode
        });

    private static SshConnectionErrorCode ClassifyTarget(
        SshSessionResult result,
        bool hostKeyUnknown,
        IJumpTunnel tunnel,
        CancellationToken callerToken,
        CancellationTokenSource timeoutSource) =>
        ProxyJumpFailureClassifier.Classify(new ProxyJumpFailure
        {
            Stage = ProxyJumpStage.Target,
            Cancelled = callerToken.IsCancellationRequested,
            TimedOut = timeoutSource.IsCancellationRequested || result.ErrorCode == SshConnectionErrorCode.ConnectionTimedOut,
            JumpConnected = tunnel.IsJumpConnected,
            IdentificationReceived = result.IdentificationReceived,
            HostKeyRejected = result.HostKeyRejected || result.ErrorCode == SshConnectionErrorCode.HostKeyMismatch,
            HostKeyUnknown = hostKeyUnknown,
            MappedCode = result.ErrorCode
        });

    /// <summary>
    /// The jump host's login: its own credential reference (<see cref="ServerCredentialKind.JumpPassword"/> /
    /// <see cref="ServerCredentialKind.JumpPrivateKeyPassphrase"/>) or the editor's unsaved
    /// <see cref="SshConnectionRequest.JumpCredentialOverride"/>. Never the target's credential. A null login
    /// means the jump credential is unavailable.
    /// </summary>
    private async Task<(SshLogin? Login, SecretValue? Stored)> ResolveJumpLoginAsync(
        Server server,
        JumpHop jump,
        SecretValue? jumpOverride,
        CancellationToken cancellationToken)
    {
        switch (jump.AuthenticationMethod)
        {
            case AuthenticationMethod.Password:
            {
                var secret = jumpOverride;
                SecretValue? stored = null;
                if (secret is null)
                {
                    if (jump.CredentialReferenceId is not { } referenceId || referenceId == Guid.Empty)
                    {
                        return (null, null);
                    }

                    stored = await _credentialStore.ReadAsync(
                            new CredentialReference(server.Id, ServerCredentialKind.JumpPassword, referenceId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    secret = stored;
                }

                return secret is null
                    ? (null, null)
                    : (new SshLogin(SshLoginKind.Password, Password: secret.RevealAsString()), stored);
            }

            case AuthenticationMethod.SshKey:
            {
                if (string.IsNullOrWhiteSpace(jump.PrivateKeyPath))
                {
                    return (null, null);
                }

                var passphrase = jumpOverride;
                SecretValue? stored = null;
                if (passphrase is null && jump.CredentialReferenceId is { } referenceId && referenceId != Guid.Empty)
                {
                    stored = await _credentialStore.ReadAsync(
                            new CredentialReference(server.Id, ServerCredentialKind.JumpPrivateKeyPassphrase, referenceId),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (stored is null)
                    {
                        return (null, null);
                    }

                    passphrase = stored;
                }

                return (new SshLogin(
                        SshLoginKind.PrivateKey,
                        PrivateKeyPath: jump.PrivateKeyPath,
                        Passphrase: passphrase?.RevealAsString()),
                    stored);
            }

            default:
                return (null, null);
        }
    }

    private static bool TryValidateJump(JumpHop jump, out SshEndpoint endpoint)
    {
        endpoint = new SshEndpoint(string.Empty, 0);
        if (string.IsNullOrWhiteSpace(jump.Host)
            || string.IsNullOrWhiteSpace(jump.Username)
            || jump.Port is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            endpoint = SshEndpoint.Create(jump.Host, jump.Port);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private SshConnectionResult CompleteHop(
        Server server,
        SshConnectionErrorCode errorCode,
        Stopwatch stopwatch,
        SshConnectionRequest request,
        HostKeyIdentity presentedHostKey,
        SshHostKeyHop hop,
        SshEndpoint? endpoint = null,
        SshRoute? route = null,
        TrustedHostKey? trusted = null,
        TrustedRoutedHostKey? trustedRouted = null,
        StageTracker? stages = null) =>
        Complete(
            server,
            errorCode,
            stopwatch.Elapsed,
            request.Timeout,
            presentedHostKey,
            trusted,
            hop: hop,
            hostKeyEndpoint: endpoint,
            hostKeyRoute: route,
            trustedRoutedHostKey: trustedRouted,
            stages: stages);

    /// <summary>
    /// Resolves the TARGET's credential (only after its host key was verified by the caller) and runs the
    /// operation in one authenticated session created by <paramref name="createSession"/>. SessionRan is false
    /// when no session was attempted (a credential problem), so a routed caller never reads a missing
    /// identification into it.
    /// </summary>
    private async Task<(SshSessionResult Result, bool SessionRan)> ConnectAuthenticatedAsync(
        SshConnectionRequest request,
        Func<HostKeyIdentity, bool> verifier,
        Func<SshLogin, ISshSession> createSession,
        SshSessionOperation operation,
        TimeSpan cpuSampleInterval,
        WorkloadCollectionPlan workloadPlan,
        CancellationToken cancellationToken)
    {
        SecretValue? storedSecret = null;
        try
        {
            var server = request.Server;
            ISshSession session;

            switch (server.AuthenticationMethod)
            {
                case AuthenticationMethod.Password:
                {
                    var secret = request.CredentialOverride;
                    if (secret is null)
                    {
                        if (server.CredentialReferenceId is not { } referenceId || referenceId == Guid.Empty)
                        {
                            return (Failure(SshConnectionErrorCode.CredentialNotConfigured), false);
                        }

                        storedSecret = await _credentialStore.ReadAsync(
                                new CredentialReference(server.Id, ServerCredentialKind.Password, referenceId),
                                cancellationToken)
                            .ConfigureAwait(false);
                        secret = storedSecret;
                    }

                    if (secret is null)
                    {
                        return (Failure(SshConnectionErrorCode.CredentialUnavailable), false);
                    }

                    session = createSession(new SshLogin(SshLoginKind.Password, Password: secret.RevealAsString()));
                    break;
                }

                case AuthenticationMethod.SshKey:
                {
                    if (string.IsNullOrWhiteSpace(server.PrivateKeyPath))
                    {
                        return (Failure(SshConnectionErrorCode.PrivateKeyUnavailable), false);
                    }

                    var passphrase = request.CredentialOverride;
                    if (passphrase is null && server.CredentialReferenceId is { } referenceId && referenceId != Guid.Empty)
                    {
                        storedSecret = await _credentialStore.ReadAsync(
                                new CredentialReference(
                                    server.Id,
                                    ServerCredentialKind.PrivateKeyPassphrase,
                                    referenceId),
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (storedSecret is null)
                        {
                            return (Failure(SshConnectionErrorCode.CredentialUnavailable), false);
                        }

                        passphrase = storedSecret;
                    }

                    try
                    {
                        session = createSession(new SshLogin(
                            SshLoginKind.PrivateKey,
                            PrivateKeyPath: server.PrivateKeyPath,
                            Passphrase: passphrase?.RevealAsString()));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return (Failure(SshConnectionErrorCode.PrivateKeyUnavailable, exception), false);
                    }
                    catch (SshPrivateKeyLoadException exception)
                    {
                        return (Failure(SshConnectionErrorCode.PrivateKeyInvalid, exception), false);
                    }

                    break;
                }

                default:
                    return (Failure(SshConnectionErrorCode.CredentialNotConfigured), false);
            }

            using (session)
            {
                var result = operation switch
                {
                    SshSessionOperation.Connect => await session
                        .ConnectAsync(verifier, cancellationToken)
                        .ConfigureAwait(false),
                    SshSessionOperation.DetectOperatingSystem => await session
                        .DetectOperatingSystemAsync(verifier, cancellationToken)
                        .ConfigureAwait(false),
                    SshSessionOperation.CollectLinuxMetrics => await session
                        .CollectLinuxMetricsAsync(verifier, cpuSampleInterval, cancellationToken)
                        .ConfigureAwait(false),
                    SshSessionOperation.CollectMacOsMetrics => await session
                        .CollectMacOsMetricsAsync(verifier, cancellationToken)
                        .ConfigureAwait(false),
                    SshSessionOperation.CollectWorkloads => await session
                        .CollectWorkloadsAsync(verifier, workloadPlan, cancellationToken)
                        .ConfigureAwait(false),
                    _ => Failure(SshConnectionErrorCode.Unexpected)
                };
                return (result, true);
            }
        }
        finally
        {
            storedSecret?.Dispose();
        }
    }

    private static bool TryValidate([NotNullWhen(true)] SshConnectionRequest? request, out SshEndpoint endpoint)
    {
        endpoint = new SshEndpoint(string.Empty, 0);

        if (request?.Server is not { } server ||
            string.IsNullOrWhiteSpace(server.Host) ||
            string.IsNullOrWhiteSpace(server.Username) ||
            server.Port is < 1 or > 65535 ||
            request.Timeout <= TimeSpan.Zero ||
            request.Timeout > TimeSpan.FromMinutes(5))
        {
            return false;
        }

        try
        {
            endpoint = SshEndpoint.Create(server.Host, server.Port);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SshSessionResult Failure(
        SshConnectionErrorCode errorCode,
        Exception? exception = null) => new()
    {
        ErrorCode = errorCode,
        ExceptionType = exception?.GetType().Name
    };

    private static SshConnectionErrorCode NormalizeCancellation(
        SshConnectionErrorCode error,
        CancellationToken callerToken,
        CancellationTokenSource timeoutSource)
    {
        if (error != SshConnectionErrorCode.Cancelled)
        {
            return error;
        }

        return callerToken.IsCancellationRequested
            ? SshConnectionErrorCode.Cancelled
            : timeoutSource.IsCancellationRequested
                ? SshConnectionErrorCode.ConnectionTimedOut
                : SshConnectionErrorCode.Cancelled;
    }

    private SshConnectionResult Complete(
        Server? server,
        SshConnectionErrorCode errorCode,
        TimeSpan duration,
        TimeSpan configuredTimeout,
        HostKeyIdentity? presentedHostKey = null,
        TrustedHostKey? trustedHostKey = null,
        ServerOperatingSystem detectedOperatingSystem = ServerOperatingSystem.Unknown,
        string? exceptionType = null,
        SshHostKeyHop hop = SshHostKeyHop.Direct,
        SshEndpoint? hostKeyEndpoint = null,
        SshRoute? hostKeyRoute = null,
        TrustedRoutedHostKey? trustedRoutedHostKey = null,
        StageTracker? stages = null)
    {
        // C6: only ids, the LOGICAL host, the state and an exception TYPE are logged — never ConnectionInfo,
        // an authentication method, a credential, command output or the tunnel's loopback port.
        var state = ToState(errorCode);
        if (errorCode == SshConnectionErrorCode.None)
        {
            _logger.LogInformation(
                "SSH operation completed for server {ServerId} host {Host} with state {State} and timeout {TimeoutMs} ms.",
                server?.Id,
                server?.Host,
                state,
                configuredTimeout.TotalMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "SSH operation completed for server {ServerId} host {Host} with state {State}, duration {DurationMs} ms, timeout {TimeoutMs} ms and exception type {ExceptionType}.",
                server?.Id,
                server?.Host,
                state,
                duration.TotalMilliseconds,
                configuredTimeout.TotalMilliseconds,
                exceptionType);
        }

        return new SshConnectionResult
        {
            State = state,
            ErrorCode = errorCode,
            PresentedHostKey = presentedHostKey,
            TrustedHostKey = trustedHostKey,
            HostKeyHop = hop,
            HostKeyEndpoint = hostKeyEndpoint,
            HostKeyRoute = hostKeyRoute,
            TrustedRoutedHostKey = trustedRoutedHostKey,
            DetectedOperatingSystem = detectedOperatingSystem,
            ReachedStage = stages?.Complete() ?? SshConnectionStage.None,
            Duration = duration
        };
    }

    // M14.5: a failure of the jump or of the local tunnel end is never a step reached on the TARGET.
    private static bool IsJumpStageFailure(SshConnectionErrorCode code) => code is
        SshConnectionErrorCode.JumpConnectionFailed or
        SshConnectionErrorCode.JumpAuthenticationFailed or
        SshConnectionErrorCode.JumpHostKeyUnknown or
        SshConnectionErrorCode.JumpHostKeyMismatch or
        SshConnectionErrorCode.JumpCredentialUnavailable or
        SshConnectionErrorCode.LocalTunnelFailed or
        SshConnectionErrorCode.TargetUnreachableViaJump;

    // M14.4b-2: jump and routed-target host-key codes open the trust panel like a direct key does, but the
    // result's HostKeyHop decides the store (jump → DIRECT store under the jump endpoint; target → ROUTED
    // store under the SshRoute). The editor never writes a hop's key anywhere else.
    internal static ServerConnectionState ToState(SshConnectionErrorCode errorCode) => errorCode switch
    {
        SshConnectionErrorCode.None => ServerConnectionState.Connected,
        SshConnectionErrorCode.AuthenticationFailed or
        SshConnectionErrorCode.JumpAuthenticationFailed => ServerConnectionState.AuthenticationFailed,
        SshConnectionErrorCode.JumpCredentialUnavailable or
        SshConnectionErrorCode.LocalTunnelFailed => ServerConnectionState.Error,
        SshConnectionErrorCode.JumpConnectionFailed or
        SshConnectionErrorCode.TargetUnreachableViaJump => ServerConnectionState.Unreachable,
        SshConnectionErrorCode.HostKeyUnknown or
        SshConnectionErrorCode.JumpHostKeyUnknown or
        SshConnectionErrorCode.RoutedHostKeyUnknown => ServerConnectionState.HostKeyUnknown,
        SshConnectionErrorCode.HostKeyMismatch or
        SshConnectionErrorCode.JumpHostKeyMismatch or
        SshConnectionErrorCode.RoutedHostKeyMismatch => ServerConnectionState.HostKeyMismatch,
        SshConnectionErrorCode.ConnectionTimedOut => ServerConnectionState.TimedOut,
        SshConnectionErrorCode.Cancelled => ServerConnectionState.Cancelled,
        SshConnectionErrorCode.DnsResolutionFailed or
        SshConnectionErrorCode.ConnectionRefused or
        SshConnectionErrorCode.HostUnreachable or
        SshConnectionErrorCode.NetworkUnavailable => ServerConnectionState.Unreachable,
        _ => ServerConnectionState.Error
    };

    /// <summary>
    /// M14.5 — the "Test connection" steps of ONE operation. Only moves forward; each step is reported to the
    /// optional progress exactly once, in order, with every intermediate step reported before a later one. Once
    /// the result is built (<see cref="Complete"/>) nothing more is reported, so the progress never shows a step
    /// the result does not carry. A throwing progress sink never changes the outcome: its exception TYPE is
    /// logged and the operation continues.
    /// </summary>
    internal sealed class StageTracker(IProgress<SshConnectionStage>? progress, ILogger logger)
    {
        private bool _completed;

        public SshConnectionStage Reached { get; private set; } = SshConnectionStage.None;

        public void Advance(SshConnectionStage stage)
        {
            if (_completed)
            {
                return;
            }

            while (Reached < stage)
            {
                Reached++;
                Report(Reached);
            }
        }

        /// <summary>Freezes the tracker as the result is built and returns the stage the result carries.</summary>
        public SshConnectionStage Complete()
        {
            _completed = true;
            return Reached;
        }

        private void Report(SshConnectionStage stage)
        {
            if (progress is null)
            {
                return;
            }

            try
            {
                progress.Report(stage);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "SSH connection stage progress observer failed with exception type {ExceptionType}; the operation continues.",
                    exception.GetType().Name);
            }
        }
    }

    private enum SshSessionOperation
    {
        Connect,
        DetectOperatingSystem,
        CollectLinuxMetrics,
        CollectMacOsMetrics,
        CollectWorkloads
    }
}
