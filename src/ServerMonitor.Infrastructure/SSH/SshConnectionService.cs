using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Collectors.Linux;

namespace ServerMonitor.Infrastructure.SSH;

public sealed class SshConnectionService : ISshConnectionService, ILinuxMetricsRemoteSource
{
    private readonly IHostKeyTrustStore _hostKeyTrustStore;
    private readonly IServerCredentialStore _credentialStore;
    private readonly ILogger<SshConnectionService> _logger;
    private readonly ISshSessionFactory _sessionFactory;

    public SshConnectionService(
        IHostKeyTrustStore hostKeyTrustStore,
        IServerCredentialStore credentialStore,
        ILogger<SshConnectionService> logger)
        : this(hostKeyTrustStore, credentialStore, logger, new SshNetSessionFactory())
    {
    }

    internal SshConnectionService(
        IHostKeyTrustStore hostKeyTrustStore,
        IServerCredentialStore credentialStore,
        ILogger<SshConnectionService> logger,
        ISshSessionFactory sessionFactory)
    {
        _hostKeyTrustStore = hostKeyTrustStore ?? throw new ArgumentNullException(nameof(hostKeyTrustStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public Task<SshConnectionResult> ConnectAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, SshSessionOperation.Connect, TimeSpan.Zero, null, cancellationToken);

    public Task<SshConnectionResult> TestConnectionAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, SshSessionOperation.DetectOperatingSystem, TimeSpan.Zero, null, cancellationToken);

    public Task<SshConnectionResult> DetectOperatingSystemAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, SshSessionOperation.DetectOperatingSystem, TimeSpan.Zero, null, cancellationToken);

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
                value => data = value,
                cancellationToken)
            .ConfigureAwait(false);

        return new LinuxMetricsRemoteResult
        {
            ConnectionResult = connectionResult,
            Data = data
        };
    }

    private async Task<SshConnectionResult> ExecuteAsync(
        SshConnectionRequest request,
        SshSessionOperation operation,
        TimeSpan cpuSampleInterval,
        Action<LinuxMetricsRawData?>? captureLinuxMetrics,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!TryValidate(request, out var endpoint))
        {
            return Complete(
                request?.Server,
                SshConnectionErrorCode.InvalidConfiguration,
                stopwatch.Elapsed,
                request?.Timeout ?? TimeSpan.Zero);
        }

        using var timeoutSource = new CancellationTokenSource(request.Timeout);
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

            if (trustedHostKey is null && probe.PresentedHostKey is not null)
            {
                return Complete(
                    request.Server,
                    SshConnectionErrorCode.HostKeyUnknown,
                    stopwatch.Elapsed,
                    request.Timeout,
                    probe.PresentedHostKey);
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
                    trustedHostKey);
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
                    exceptionType: probe.ExceptionType);
            }

            var sessionResult = await ConnectAuthenticatedAsync(
                    request,
                    trustedHostKey!,
                    operation,
                    cpuSampleInterval,
                    linkedSource.Token)
                .ConfigureAwait(false);

            captureLinuxMetrics?.Invoke(sessionResult.LinuxMetrics);

            return Complete(
                request.Server,
                NormalizeCancellation(sessionResult.ErrorCode, cancellationToken, timeoutSource),
                stopwatch.Elapsed,
                request.Timeout,
                sessionResult.PresentedHostKey,
                trustedHostKey,
                sessionResult.DetectedOperatingSystem,
                sessionResult.ExceptionType);
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
                exceptionType: exception.GetType().Name);
        }
        catch (Exception exception)
        {
            return Complete(
                request.Server,
                SshExceptionMapper.Map(exception),
                stopwatch.Elapsed,
                request.Timeout,
                exceptionType: exception.GetType().Name);
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

    private async Task<SshSessionResult> ConnectAuthenticatedAsync(
        SshConnectionRequest request,
        TrustedHostKey trustedHostKey,
        SshSessionOperation operation,
        TimeSpan cpuSampleInterval,
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
                            return Failure(SshConnectionErrorCode.CredentialNotConfigured);
                        }

                        storedSecret = await _credentialStore.ReadAsync(
                                new CredentialReference(server.Id, ServerCredentialKind.Password, referenceId),
                                cancellationToken)
                            .ConfigureAwait(false);
                        secret = storedSecret;
                    }

                    if (secret is null)
                    {
                        return Failure(SshConnectionErrorCode.CredentialUnavailable);
                    }

                    session = _sessionFactory.CreatePasswordSession(
                        server,
                        secret.RevealAsString(),
                        request.Timeout);
                    break;
                }

                case AuthenticationMethod.SshKey:
                {
                    if (string.IsNullOrWhiteSpace(server.PrivateKeyPath))
                    {
                        return Failure(SshConnectionErrorCode.PrivateKeyUnavailable);
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
                            return Failure(SshConnectionErrorCode.CredentialUnavailable);
                        }

                        passphrase = storedSecret;
                    }

                    try
                    {
                        session = _sessionFactory.CreatePrivateKeySession(
                            server,
                            server.PrivateKeyPath,
                            passphrase?.RevealAsString(),
                            request.Timeout);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return Failure(SshConnectionErrorCode.PrivateKeyUnavailable, exception);
                    }
                    catch (SshPrivateKeyLoadException exception)
                    {
                        return Failure(SshConnectionErrorCode.PrivateKeyInvalid, exception);
                    }

                    break;
                }

                default:
                    return Failure(SshConnectionErrorCode.CredentialNotConfigured);
            }

            using (session)
            {
                var verifier = (HostKeyIdentity identity) => trustedHostKey.Identity.Matches(identity);
                return operation switch
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
                    _ => Failure(SshConnectionErrorCode.Unexpected)
                };
            }
        }
        finally
        {
            storedSecret?.Dispose();
        }
    }

    private static bool TryValidate(SshConnectionRequest? request, out SshEndpoint endpoint)
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
        string? exceptionType = null)
    {
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
            DetectedOperatingSystem = detectedOperatingSystem,
            Duration = duration
        };
    }

    private static ServerConnectionState ToState(SshConnectionErrorCode errorCode) => errorCode switch
    {
        SshConnectionErrorCode.None => ServerConnectionState.Connected,
        SshConnectionErrorCode.AuthenticationFailed => ServerConnectionState.AuthenticationFailed,
        SshConnectionErrorCode.HostKeyUnknown => ServerConnectionState.HostKeyUnknown,
        SshConnectionErrorCode.HostKeyMismatch => ServerConnectionState.HostKeyMismatch,
        SshConnectionErrorCode.ConnectionTimedOut => ServerConnectionState.TimedOut,
        SshConnectionErrorCode.Cancelled => ServerConnectionState.Cancelled,
        SshConnectionErrorCode.DnsResolutionFailed or
        SshConnectionErrorCode.ConnectionRefused or
        SshConnectionErrorCode.HostUnreachable or
        SshConnectionErrorCode.NetworkUnavailable => ServerConnectionState.Unreachable,
        _ => ServerConnectionState.Error
    };

    private enum SshSessionOperation
    {
        Connect,
        DetectOperatingSystem,
        CollectLinuxMetrics
    }
}
