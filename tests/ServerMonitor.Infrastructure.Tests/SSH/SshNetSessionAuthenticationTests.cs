using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerMonitor.Core.Enums;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.5 (Vigil LOW-2): the PRODUCTION <see cref="SshNetSession"/> counts a session as authenticated
/// (<see cref="SshSessionResult.AuthenticationCompleted"/>) only AFTER the SSH connect — which includes user
/// authentication — returned, and keeps that fact when a later step fails. Only the connect itself is substituted
/// (through the session's connector seam), or a real refused loopback port is dialled.
/// </summary>
public sealed class SshNetSessionAuthenticationTests
{
    [Fact]
    public async Task A_connect_that_fails_is_never_counted_as_authenticated()
    {
        using var session = Session((_, _) => throw new SshAuthenticationException("Permission denied (publickey)."));

        var result = await session.DetectOperatingSystemAsync(_ => true, CancellationToken.None);

        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, result.ErrorCode);
        Assert.False(result.AuthenticationCompleted);
    }

    [Fact]
    public async Task A_connect_cancelled_before_it_returns_is_never_counted_as_authenticated()
    {
        using var cancel = new CancellationTokenSource();
        using var session = Session(async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
        });

        var result = await session.ConnectAsync(_ => true, cancel.Token);

        Assert.NotEqual(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.False(result.AuthenticationCompleted);
    }

    [Fact]
    public async Task A_step_failing_after_the_connect_returned_keeps_the_session_authenticated()
    {
        // The connect "succeeds" without a real server, so the uname command that follows fails on an
        // unconnected client: the operation fails AFTER authentication, which must still be reported.
        var connected = false;
        using var session = Session((_, _) =>
        {
            connected = true;
            return Task.CompletedTask;
        });

        var result = await session.DetectOperatingSystemAsync(_ => true, CancellationToken.None);

        Assert.True(connected);
        Assert.NotEqual(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.True(result.AuthenticationCompleted);
    }

    [Fact]
    public async Task Real_connect_to_a_refused_loopback_port_is_never_counted_as_authenticated()
    {
        // The production connector (no seam): a port that was just released refuses the TCP connect.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var session = new SshNetSession(Info(port), Password(), authenticationResource: null);

        var result = await session.ConnectAsync(_ => true, CancellationToken.None);

        Assert.NotEqual(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.False(result.IdentificationReceived);
        Assert.False(result.AuthenticationCompleted);
    }

    private static SshNetSession Session(SshNetSession.ClientConnector connect) =>
        new(Info(22), Password(), authenticationResource: null, connectGate: null, connectClient: connect);

    private static PasswordAuthenticationMethod Password() => new("tester", "not-a-real-password");

    private static ConnectionInfo Info(int port) =>
        new("127.0.0.1", port, "tester", Password()) { Timeout = TimeSpan.FromSeconds(5) };
}
