using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.SSH;
using static ServerMonitor.Infrastructure.Tests.SSH.SshRoutedTestDoubles;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.5 D-1 (Beacon QA): a port that accepts the connection but never identifies as SSH (an HTTP service
/// answering 400, then closing gracefully or with a reset) is "port reached + protocol problem", never
/// Unexpected/None. Measured on SSH.NET 2026.0.0 before the fix: the RST variant surfaced as
/// SocketException(ConnectionReset) -> Unexpected, the graceful one as SshConnectionException(ConnectionLost,
/// "closed before a valid SSH identification string was received") -> RemoteDisconnected, both with no
/// identification and ReachedStage None; and a close WITHOUT any byte raises exactly the same exceptions.
/// The real-network tests use the production probe and the production service against loopback listeners.
/// </summary>
public sealed class SshNonSshServiceTests
{
    private const string Http400 = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n";

    // ------------------------------------------------------------------ mapper

    [Theory]
    [InlineData(SocketError.ConnectionReset, SshConnectionErrorCode.RemoteDisconnected)]
    [InlineData(SocketError.ConnectionAborted, SshConnectionErrorCode.RemoteDisconnected)]
    [InlineData(SocketError.ConnectionRefused, SshConnectionErrorCode.ConnectionRefused)]
    [InlineData(SocketError.HostUnreachable, SshConnectionErrorCode.HostUnreachable)]
    [InlineData(SocketError.TimedOut, SshConnectionErrorCode.ConnectionTimedOut)]
    public void Socket_errors_map_to_their_family(SocketError error, SshConnectionErrorCode expected)
    {
        Assert.Equal(expected, SshExceptionMapper.Map(new SocketException((int)error)));
    }

    public static TheoryData<Exception, bool> EstablishedEvidence() => new()
    {
        { new SocketException((int)SocketError.ConnectionReset), true },
        { new SocketException((int)SocketError.ConnectionAborted), true },
        { new SshConnectionException("closed before identification", DisconnectReason.ConnectionLost), true },
        { new SshConnectionException("wrapped", DisconnectReason.None, new SocketException((int)SocketError.ConnectionReset)), true },
        { new SocketException((int)SocketError.ConnectionRefused), false },
        { new SocketException((int)SocketError.HostNotFound), false },
        { new SocketException((int)SocketError.HostUnreachable), false },
        { new SocketException((int)SocketError.NetworkUnreachable), false },
        { new SocketException((int)SocketError.TimedOut), false },
        { new SshOperationTimeoutException("timeout"), false },
        { new OperationCanceledException(), false },
        { new SshAuthenticationException("denied"), false },
        { new SshConnectionException("kex", DisconnectReason.KeyExchangeFailed), false },
        { new InvalidOperationException(), false }
    };

    [Theory]
    [MemberData(nameof(EstablishedEvidence))]
    public void Only_failures_of_an_established_connection_prove_one(Exception exception, bool expected)
    {
        Assert.Equal(expected, SshExceptionMapper.ProvesEstablishedConnection(exception));
    }

    [Fact]
    public void Before_identification_an_established_connection_is_a_protocol_error()
    {
        var closed = new SshConnectionException("closed before identification", DisconnectReason.ConnectionLost);
        var reset = new SocketException((int)SocketError.ConnectionReset);

        Assert.Equal(SshConnectionErrorCode.ProtocolError, SshExceptionMapper.Map(closed, identificationReceived: false));
        Assert.Equal(SshConnectionErrorCode.ProtocolError, SshExceptionMapper.Map(reset, identificationReceived: false));

        // After the identification, the same failures keep their own family.
        Assert.Equal(SshConnectionErrorCode.RemoteDisconnected, SshExceptionMapper.Map(closed, identificationReceived: true));
        Assert.Equal(SshConnectionErrorCode.RemoteDisconnected, SshExceptionMapper.Map(reset, identificationReceived: true));

        // Without proof of a connection nothing changes.
        Assert.Equal(
            SshConnectionErrorCode.ConnectionRefused,
            SshExceptionMapper.Map(new SocketException((int)SocketError.ConnectionRefused), identificationReceived: false));
        Assert.Equal(
            SshConnectionErrorCode.AuthenticationFailed,
            SshExceptionMapper.Map(new SshAuthenticationException("denied"), identificationReceived: false));
    }

    // ------------------------------------------------- real probe, loopback

    [Theory]
    [InlineData(ListenerMode.HttpThenReset)]
    [InlineData(ListenerMode.HttpThenClose)]
    [InlineData(ListenerMode.CloseWithoutBytes)]
    public async Task Real_probe_against_a_non_ssh_listener_proves_the_connection(ListenerMode mode)
    {
        await using var listener = LoopbackListener.Start(mode);

        using var probe = new SshNetSessionFactory().CreateHostKeyProbe(DirectServer(listener.Port), TimeSpan.FromSeconds(10));
        var result = await probe.ConnectAsync(_ => false, CancellationToken.None);

        Assert.Equal(SshConnectionErrorCode.ProtocolError, result.ErrorCode);
        Assert.False(result.IdentificationReceived);
        Assert.Null(result.PresentedHostKey);
        Assert.True(result.ConnectionEstablished);
        Assert.True(listener.Accepted >= 1);
    }

    [Fact]
    public async Task Real_probe_against_a_refused_port_proves_nothing()
    {
        var port = ReleasedPort();

        using var probe = new SshNetSessionFactory().CreateHostKeyProbe(DirectServer(port), TimeSpan.FromSeconds(10));
        var result = await probe.ConnectAsync(_ => false, CancellationToken.None);

        Assert.Equal(SshConnectionErrorCode.ConnectionRefused, result.ErrorCode);
        Assert.False(result.IdentificationReceived);
        Assert.False(result.ConnectionEstablished);
    }

    // ----------------------------------------------- real service, loopback

    [Theory]
    [InlineData(ListenerMode.HttpThenReset)]
    [InlineData(ListenerMode.HttpThenClose)]
    public async Task Test_connection_to_a_non_ssh_service_reaches_the_port_and_reports_a_protocol_error(ListenerMode mode)
    {
        await using var listener = LoopbackListener.Start(mode);
        var (service, credentials, progress) = RealService();

        var result = await service.TestConnectionAsync(Request(DirectServer(listener.Port), progress));

        Assert.Equal(SshConnectionErrorCode.ProtocolError, result.ErrorCode);
        Assert.Equal(ServerConnectionState.Error, result.State);
        Assert.Equal(SshConnectionStage.PortReachable, result.ReachedStage);
        Assert.Equal([SshConnectionStage.PortReachable], progress.Reports);
        Assert.Null(result.PresentedHostKey);
        Assert.Empty(credentials.Reads);
    }

    [Fact]
    public async Task Test_connection_to_a_refused_port_reaches_nothing()
    {
        var (service, _, progress) = RealService();

        var result = await service.TestConnectionAsync(Request(DirectServer(ReleasedPort()), progress));

        Assert.Equal(SshConnectionErrorCode.ConnectionRefused, result.ErrorCode);
        Assert.Equal(SshConnectionStage.None, result.ReachedStage);
        Assert.Empty(progress.Reports);
    }

    // ---------------------------------------------------------------- helpers

    public enum ListenerMode
    {
        HttpThenReset,
        HttpThenClose,
        CloseWithoutBytes
    }

    private static (SshConnectionService Service, CredentialStore Credentials, RecordingProgress Progress) RealService()
    {
        var credentials = new CredentialStore();
        credentials.Secrets[ServerCredentialKind.Password] = "not-a-real-password";
        var service = new SshConnectionService(
            new DirectTrustStore(),
            new RoutedTrustStore(),
            credentials,
            NullLogger<SshConnectionService>.Instance);
        return (service, credentials, new RecordingProgress());
    }

    private static Server DirectServer(int port) => new()
    {
        Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Name = "Non-SSH",
        Host = "127.0.0.1",
        Port = port,
        Username = "tester",
        AuthenticationMethod = AuthenticationMethod.Password,
        CredentialReferenceId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    };

    private static SshConnectionRequest Request(Server server, IProgress<SshConnectionStage> progress) => new()
    {
        Server = server,
        Timeout = TimeSpan.FromSeconds(10),
        StageProgress = progress
    };

    /// <summary>A loopback port that was just released, so a connect is refused.</summary>
    private static int ReleasedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingProgress : IProgress<SshConnectionStage>
    {
        public List<SshConnectionStage> Reports { get; } = [];

        public void Report(SshConnectionStage value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    /// <summary>
    /// A loopback-only TCP service that is not SSH. Every accepted connection gets the same treatment; no timer
    /// decides anything (a graceful close waits for the client's own close).
    /// </summary>
    private sealed class LoopbackListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ListenerMode _mode;
        private Task _loop = Task.CompletedTask;
        private int _accepted;

        private LoopbackListener(ListenerMode mode) => _mode = mode;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public static LoopbackListener Start(ListenerMode mode)
        {
            var listener = new LoopbackListener(mode);
            listener._listener.Start();
            listener._loop = listener.RunAsync();
            return listener;
        }

        private async Task RunAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _accepted);
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    if (_mode != ListenerMode.CloseWithoutBytes)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(Http400), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                    }

                    if (_mode == ListenerMode.HttpThenReset)
                    {
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Client.Close();
                        return;
                    }

                    // Graceful: FIN, then read until the client closes its side.
                    client.Client.Shutdown(SocketShutdown.Send);
                    var buffer = new byte[4096];
                    while (await stream.ReadAsync(buffer, _stop.Token) > 0)
                    {
                    }
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
