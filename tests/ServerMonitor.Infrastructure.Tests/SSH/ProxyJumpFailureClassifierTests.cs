using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using ServerMonitor.Core.Enums;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class ProxyJumpFailureClassifierTests
{
    // SSH.NET 2026.0.0's exact text for a peer that closes before its version line (measured in the spike):
    // through a jump, this is the ONLY symptom of a refused/failed direct-tcpip channel.
    private const string ClosedBeforeIdentification =
        "The connection to the remote server was closed before a valid SSH identification string was received.";

    private static readonly SshConnectionErrorCode[] JumpCodes =
    [
        SshConnectionErrorCode.JumpConnectionFailed,
        SshConnectionErrorCode.JumpAuthenticationFailed,
        SshConnectionErrorCode.JumpHostKeyUnknown,
        SshConnectionErrorCode.JumpHostKeyMismatch,
        SshConnectionErrorCode.JumpCredentialUnavailable
    ];

    public static TheoryData<string> Shapes => new(ShapeFactories.Keys);

    private static readonly Dictionary<string, Func<Exception>> ShapeFactories = new()
    {
        ["auth"] = () => new SshAuthenticationException("Permission denied (publickey)."),
        ["hostkey-rejected"] = () => new SshConnectionException("Key exchange negotiation failed.", DisconnectReason.KeyExchangeFailed),
        ["socket-refused"] = () => new SocketException((int)SocketError.ConnectionRefused),
        ["socket-dns"] = () => new SocketException((int)SocketError.HostNotFound),
        ["socket-unreachable"] = () => new SocketException((int)SocketError.HostUnreachable),
        ["wrapped-socket"] = () => new SshConnectionException("wrapped", new SocketException((int)SocketError.NetworkUnreachable)),
        ["closed-before-identification"] = () => new SshConnectionException(ClosedBeforeIdentification),
        ["connection-lost"] = () => new SshConnectionException("lost", DisconnectReason.ConnectionLost),
        ["protocol"] = () => new SshConnectionException("bad", DisconnectReason.ProtocolError),
        ["timeout"] = () => new SshOperationTimeoutException("Socket read timed out."),
        ["operation-cancelled"] = () => new OperationCanceledException(),
        ["key-load"] = () => new SshPrivateKeyLoadException(new InvalidOperationException()),
        ["generic-ssh"] = () => new SshException("generic"),
        ["unexpected"] = () => new InvalidOperationException("unexpected")
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Jump_stage_never_produces_a_target_code(string shape)
    {
        foreach (var (rejected, unknown) in FlagCombinations())
        {
            var code = Classify(ProxyJumpStage.Jump, shape, rejected, unknown);

            Assert.Contains(code, JumpCodes);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Channel_and_target_stages_never_produce_a_jump_code(string shape)
    {
        foreach (var stage in new[] { ProxyJumpStage.Channel, ProxyJumpStage.Target })
        {
            foreach (var (rejected, unknown) in FlagCombinations())
            {
                var code = Classify(stage, shape, rejected, unknown);

                Assert.DoesNotContain(code, JumpCodes);
                Assert.NotEqual(SshConnectionErrorCode.None, code);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void No_stage_ever_enters_the_direct_trust_flow(string shape)
    {
        // HostKeyUnknown/HostKeyMismatch open the DIRECT trust panel (direct store, bare host:port). Nothing
        // seen through a jump may produce them.
        foreach (var stage in Enum.GetValues<ProxyJumpStage>())
        {
            foreach (var (rejected, unknown) in FlagCombinations())
            {
                var code = Classify(stage, shape, rejected, unknown);

                Assert.NotEqual(SshConnectionErrorCode.HostKeyUnknown, code);
                Assert.NotEqual(SshConnectionErrorCode.HostKeyMismatch, code);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Cancellation_is_stage_neutral(string shape)
    {
        foreach (var stage in Enum.GetValues<ProxyJumpStage>())
        {
            Assert.Equal(
                SshConnectionErrorCode.Cancelled,
                ProxyJumpFailureClassifier.Classify(stage, ShapeFactories[shape](), true, true, cancelled: true));
        }
    }

    [Fact]
    public void Measured_jump_shapes_map_to_their_jump_codes()
    {
        Assert.Equal(SshConnectionErrorCode.JumpAuthenticationFailed, Classify(ProxyJumpStage.Jump, "auth"));
        Assert.Equal(SshConnectionErrorCode.JumpHostKeyUnknown, Classify(ProxyJumpStage.Jump, "hostkey-rejected", rejected: true, unknown: true));
        Assert.Equal(SshConnectionErrorCode.JumpHostKeyMismatch, Classify(ProxyJumpStage.Jump, "hostkey-rejected", rejected: true, unknown: false));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, Classify(ProxyJumpStage.Jump, "socket-refused"));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, Classify(ProxyJumpStage.Jump, "socket-dns"));
        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, Classify(ProxyJumpStage.Jump, "timeout"));
        Assert.Equal(SshConnectionErrorCode.JumpCredentialUnavailable, Classify(ProxyJumpStage.Jump, "key-load"));
    }

    [Fact]
    public void Channel_refusal_is_target_unreachable_via_jump()
    {
        // The refusal surfaces ONLY on the target client, as "closed before identification".
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, Classify(ProxyJumpStage.Target, "closed-before-identification"));
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, Classify(ProxyJumpStage.Channel, "closed-before-identification"));
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, Classify(ProxyJumpStage.Channel, "socket-refused"));
    }

    [Fact]
    public void Target_socket_failures_are_the_tunnel_not_a_target_network_diagnosis()
    {
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, Classify(ProxyJumpStage.Target, "socket-refused"));
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, Classify(ProxyJumpStage.Target, "socket-dns"));
        Assert.Equal(SshConnectionErrorCode.TargetUnreachableViaJump, Classify(ProxyJumpStage.Target, "wrapped-socket"));
    }

    [Fact]
    public void Target_shapes_keep_their_target_codes()
    {
        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, Classify(ProxyJumpStage.Target, "auth"));
        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyUnknown, Classify(ProxyJumpStage.Target, "hostkey-rejected", rejected: true, unknown: true));
        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyMismatch, Classify(ProxyJumpStage.Target, "hostkey-rejected", rejected: true, unknown: false));
        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, Classify(ProxyJumpStage.Target, "timeout"));
        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, Classify(ProxyJumpStage.Target, "operation-cancelled"));
        Assert.Equal(SshConnectionErrorCode.RemoteDisconnected, Classify(ProxyJumpStage.Target, "connection-lost"));
        Assert.Equal(SshConnectionErrorCode.ProtocolError, Classify(ProxyJumpStage.Target, "protocol"));
    }

    [Fact]
    public void Undefined_stage_and_null_exception_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProxyJumpFailureClassifier.Classify(
            (ProxyJumpStage)99, new SshException("x"), false, false, false));
        Assert.Throws<ArgumentNullException>(() => ProxyJumpFailureClassifier.Classify(
            ProxyJumpStage.Jump, null!, false, false, false));
    }

    private static SshConnectionErrorCode Classify(
        ProxyJumpStage stage,
        string shape,
        bool rejected = false,
        bool unknown = false) =>
        ProxyJumpFailureClassifier.Classify(stage, ShapeFactories[shape](), rejected, unknown, cancelled: false);

    private static IEnumerable<(bool Rejected, bool Unknown)> FlagCombinations() =>
        [(false, false), (true, false), (true, true), (false, true)];
}
