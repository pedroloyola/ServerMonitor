using System.Net;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>The authenticated jump connection (production: an SSH.NET <c>SshClient</c>).</summary>
internal interface IJumpClient : IDisposable
{
    /// <summary>
    /// Connects and authenticates; the host key is decided by <paramref name="hostKeyVerifier"/> before any
    /// credential is sent. On success the connection stays open for the tunnel.
    /// </summary>
    Task<SshSessionResult> ConnectAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken);

    bool IsConnected { get; }

    ILocalForward CreateLocalForward(string bindHost, string targetHost, uint targetPort);
}

/// <summary>The local end of the direct-tcpip forward (production: an SSH.NET <c>ForwardedPortLocal</c>).</summary>
internal interface ILocalForward : IDisposable
{
    /// <summary>
    /// Subscribes <paramref name="onRequest"/> to every incoming loopback connection BEFORE the listener starts,
    /// then starts it. A throwing <paramref name="onRequest"/> rejects the connection: the socket is closed and
    /// no channel is opened to the jump.
    /// </summary>
    void Start(Action<string, uint> onRequest);

    string BoundHost { get; }

    uint BoundPort { get; }

    bool IsStarted { get; }

    void Stop();
}

internal enum JumpTunnelOpenStage
{
    Opened,
    Jump,
    TunnelListen
}

internal sealed record JumpTunnelOpenResult(JumpTunnelOpenStage Stage, SshSessionResult? JumpResult, string? ExceptionType = null)
{
    public bool IsOpen => Stage == JumpTunnelOpenStage.Opened;
}

/// <summary>A single-hop tunnel: one jump connection, one loopback forward, the target sessions through it.</summary>
internal interface IJumpTunnel : IAsyncDisposable
{
    Task<JumpTunnelOpenResult> OpenAsync(Func<HostKeyIdentity, bool> jumpHostKeyVerifier, CancellationToken cancellationToken);

    bool IsJumpConnected { get; }

    /// <summary>A new target session dialled through the tunnel; owned (and torn down) by the tunnel.</summary>
    ISshSession CreateTargetSession(string username, SshLogin login, TimeSpan timeout);
}

internal interface IJumpTunnelFactory
{
    /// <summary>
    /// Creates the tunnel for ONE <see cref="SshConnectionService"/> operation. The jump login is consumed here
    /// (a jump private key is loaded now); nothing is connected until <see cref="IJumpTunnel.OpenAsync"/>.
    /// </summary>
    IJumpTunnel Create(SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target, TimeSpan timeout);
}

/// <summary>
/// M14.4b-2 §1 (C5): the single owner of a ProxyJump tunnel — the jump client, the loopback forward and every
/// target session handed out. One teardown, idempotent, on every path: target sessions → forward → jump. Its
/// lifetime never exceeds one service operation (no pooling, no reuse).
/// <para>
/// C1: the forward's request handler is subscribed before the listener starts and admits a connection only
/// through <see cref="LoopbackOriginatorGate"/>. C2: the listener binds the literal <see cref="LoopbackHost"/>
/// on port 0 and is then proved, from the TCP table, to be an IPv4-loopback listener owned by this process.
/// C3: target sessions dial <see cref="LoopbackHost"/> and exactly the bound port.
/// </para>
/// </summary>
internal sealed class SshJumpTunnel : IJumpTunnel
{
    /// <summary>The ONLY bind and dial host of the tunnel. Not configurable; no fallback.</summary>
    internal const string LoopbackHost = "127.0.0.1";

    private readonly IJumpClient _jump;
    private readonly SshEndpoint _target;
    private readonly ISshDialSessionFactory _targetSessions;
    private readonly ITcpOwnerLookup _ownerLookup;
    private readonly int _processId;
    private readonly ILogger _logger;
    private readonly LoopbackOriginatorGate _gate;
    private readonly List<ISshSession> _targetSessionsHandedOut = [];
    private readonly object _sync = new();
    private ILocalForward? _forward;
    private bool _opened;
    private int _disposed;

    internal SshJumpTunnel(
        IJumpClient jump,
        SshEndpoint target,
        ISshDialSessionFactory targetSessions,
        ILogger logger,
        ITcpOwnerLookup? ownerLookup = null,
        int? processId = null)
    {
        _jump = jump ?? throw new ArgumentNullException(nameof(jump));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _targetSessions = targetSessions ?? throw new ArgumentNullException(nameof(targetSessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownerLookup = ownerLookup ?? IpHelperTcpOwnerLookup.Instance;
        _processId = processId ?? Environment.ProcessId;
        _gate = new LoopbackOriginatorGate(_ownerLookup, _processId);
    }

    internal LoopbackOriginatorGate Gate => _gate;

    public bool IsJumpConnected => _jump.IsConnected;

    public async Task<JumpTunnelOpenResult> OpenAsync(
        Func<HostKeyIdentity, bool> jumpHostKeyVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jumpHostKeyVerifier);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_forward is not null)
        {
            throw new InvalidOperationException("The tunnel is single-use.");
        }

        var jumpResult = await _jump.ConnectAsync(jumpHostKeyVerifier, cancellationToken).ConfigureAwait(false);
        if (!jumpResult.IsSuccess)
        {
            return new JumpTunnelOpenResult(JumpTunnelOpenStage.Jump, jumpResult);
        }

        try
        {
            var forward = _jump.CreateLocalForward(LoopbackHost, _target.Host, (uint)_target.Port);
            lock (_sync)
            {
                _forward = forward;
            }

            forward.Start(OnRequestReceived);

            if (!IsVerifiedLoopbackListener(forward.BoundHost, forward.BoundPort, _ownerLookup, _processId))
            {
                return new JumpTunnelOpenResult(JumpTunnelOpenStage.TunnelListen, jumpResult);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new JumpTunnelOpenResult(JumpTunnelOpenStage.TunnelListen, jumpResult, exception.GetType().Name);
        }

        _opened = true;
        return new JumpTunnelOpenResult(JumpTunnelOpenStage.Opened, jumpResult);
    }

    public ISshSession CreateTargetSession(string username, SshLogin login, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!_opened || _forward is null)
        {
            throw new InvalidOperationException("The tunnel is not open.");
        }

        var session = _targetSessions.Create(TargetDial(_forward.BoundPort, username), login, timeout, _gate);
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                session.Dispose();
                throw new ObjectDisposedException(nameof(SshJumpTunnel));
            }

            _targetSessionsHandedOut.Add(session);
        }

        return session;
    }

    /// <summary>C3: the ONLY dial target of a target session — the literal loopback and exactly the bound port.</summary>
    internal static SshDialTarget TargetDial(uint boundPort, string username)
    {
        if (boundPort is 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(boundPort));
        }

        return new SshDialTarget(LoopbackHost, (int)boundPort, username);
    }

    /// <summary>
    /// C2: the listener must be the literal IPv4 loopback on an ephemeral (non-zero) port, and the TCP table
    /// must show an IPv4 listener on exactly <c>127.0.0.1:boundPort</c> owned by this process. A bind that
    /// landed on <c>[::1]</c> (as <c>"localhost"</c> does) or anywhere else fails this check.
    /// </summary>
    internal static bool IsVerifiedLoopbackListener(
        string? boundHost,
        uint boundPort,
        ITcpOwnerLookup ownerLookup,
        int processId)
    {
        if (!string.Equals(boundHost, LoopbackHost, StringComparison.Ordinal)
            || !IPAddress.TryParse(boundHost, out var address)
            || !address.Equals(IPAddress.Loopback)
            || boundPort is 0 or > 65535)
        {
            return false;
        }

        try
        {
            return ownerLookup.IsLoopbackListenerOwnedBy((int)boundPort, processId);
        }
        catch
        {
            return false;
        }
    }

    private void OnRequestReceived(string originatorHost, uint originatorPort)
    {
        var boundPort = _forward?.BoundPort ?? 0;
        var decision = _gate.Evaluate(originatorHost, originatorPort, boundPort);
        if (decision.Admitted)
        {
            return;
        }

        // C6: owner PID + originator port only — never the ConnectionInfo, never a credential.
        _logger.LogWarning(
            "ProxyJump tunnel rejected a loopback connection: reason {Reason}, owner PID {OwnerPid}, originator port {OriginatorPort}.",
            decision.Rejection,
            decision.OwnerPid,
            originatorPort);

        // Throwing from the request handler closes the foreign socket and opens NO channel (measured, pinned by E2E).
        throw new TunnelOriginatorRejectedException(decision.Rejection);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _gate.Seal();

        ISshSession[] sessions;
        ILocalForward? forward;
        lock (_sync)
        {
            sessions = [.. _targetSessionsHandedOut];
            _targetSessionsHandedOut.Clear();
            forward = _forward;
        }

        // 1. Target sessions (their sockets ride the tunnel).
        foreach (var session in sessions)
        {
            TryTeardown(session.Dispose, "target");
        }

        // 2. The loopback forward: stop the listener, then release it.
        if (forward is not null)
        {
            TryTeardown(
                () =>
                {
                    if (forward.IsStarted)
                    {
                        forward.Stop();
                    }
                },
                "forward.stop");
            TryTeardown(forward.Dispose, "forward");
        }

        // 3. The jump connection last.
        TryTeardown(_jump.Dispose, "jump");
        return ValueTask.CompletedTask;
    }

    private void TryTeardown(Action action, string step)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            // Teardown continues; only the step and exception type are logged.
            _logger.LogDebug("ProxyJump tunnel teardown step {Step} failed with {ExceptionType}.", step, exception.GetType().Name);
        }
    }
}

internal sealed class TunnelOriginatorRejectedException(OriginatorRejection rejection)
    : Exception("The loopback connection was not opened by this process for the armed target connect.")
{
    public OriginatorRejection Rejection { get; } = rejection;
}
