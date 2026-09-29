using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>Production <see cref="IJumpTunnelFactory"/>: SSH.NET <c>SshClient</c> + <c>ForwardedPortLocal</c>.</summary>
internal sealed class SshNetJumpTunnelFactory(ISshDialSessionFactory targetSessions, ILogger logger) : IJumpTunnelFactory
{
    public IJumpTunnel Create(SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(jumpLogin);
        ArgumentNullException.ThrowIfNull(target);

        // A jump private key is loaded here; its load failure surfaces to the caller as the jump's credential.
        var (authentication, resource) = SshNetSessionFactory.CreateAuthentication(jump.Username, jumpLogin);
        try
        {
            var connectionInfo = SshNetSessionFactory.CreateConnectionInfo(jump, authentication, timeout);
            var client = new SshNetJumpClient(connectionInfo, authentication, resource, logger);
            return new SshJumpTunnel(client, target, targetSessions, logger);
        }
        catch
        {
            authentication.Dispose();
            resource?.Dispose();
            throw;
        }
    }
}

internal sealed class SshNetJumpClient(
    ConnectionInfo connectionInfo,
    Renci.SshNet.AuthenticationMethod authentication,
    IDisposable? authenticationResource,
    ILogger logger) : IJumpClient
{
    private readonly SshClient _client = new(connectionInfo);
    private int _disposed;

    public bool IsConnected
    {
        get
        {
            try
            {
                return Volatile.Read(ref _disposed) == 0 && _client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public async Task<SshSessionResult> ConnectAsync(
        Func<HostKeyIdentity, bool> hostKeyVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        HostKeyIdentity? presented = null;
        var rejected = false;

        void OnHostKeyReceived(object? sender, HostKeyEventArgs args)
        {
            args.CanTrust = false;
            try
            {
                presented = HostKeyIdentity.Create(args.HostKeyName, $"SHA256:{args.FingerPrintSHA256}");
                args.CanTrust = hostKeyVerifier(presented);
                rejected = !args.CanTrust;
            }
            catch
            {
                args.CanTrust = false;
                rejected = true;
            }
        }

        _client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SshSessionResult
            {
                ErrorCode = SshConnectionErrorCode.None,
                PresentedHostKey = presented,
                IdentificationReceived = true
            };
        }
        catch (Exception exception)
        {
            return new SshSessionResult
            {
                ErrorCode = rejected ? SshConnectionErrorCode.HostKeyMismatch : SshExceptionMapper.Map(exception),
                PresentedHostKey = presented,
                HostKeyRejected = rejected,
                IdentificationReceived = !string.IsNullOrEmpty(connectionInfo.ServerVersion),
                ExceptionType = exception.GetType().Name
            };
        }
        finally
        {
            _client.HostKeyReceived -= OnHostKeyReceived;
        }
    }

    public ILocalForward CreateLocalForward(string bindHost, string targetHost, uint targetPort)
    {
        // C2: port 0 → an ephemeral port chosen by the OS; the bind host is the caller's constant.
        var port = new ForwardedPortLocal(bindHost, 0, targetHost, targetPort);
        try
        {
            _client.AddForwardedPort(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }

        return new SshNetLocalForward(port, logger);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch
        {
            // Dispose below still releases the socket.
        }

        _client.Dispose();
        authentication.Dispose();
        authenticationResource?.Dispose();
    }
}

internal sealed class SshNetLocalForward(ForwardedPortLocal port, ILogger logger) : ILocalForward
{
    private int _disposed;

    public string BoundHost => port.BoundHost;

    public uint BoundPort => port.BoundPort;

    public bool IsStarted => port.IsStarted;

    public void Start(Action<string, uint> onRequest)
    {
        ArgumentNullException.ThrowIfNull(onRequest);

        // C1: the handler is attached BEFORE the listener exists, so no connection is ever unguarded.
        port.RequestReceived += (_, args) => onRequest(args.OriginatorHost, args.OriginatorPort);
        port.Exception += (_, args) => logger.LogDebug(
            "ProxyJump forward reported {ExceptionType}.",
            args.Exception.GetType().Name);
        port.Start();
    }

    public void Stop() => port.Stop();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        port.Dispose();
    }
}
