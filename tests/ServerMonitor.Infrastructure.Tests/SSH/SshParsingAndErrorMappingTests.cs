using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using ServerMonitor.Core.Enums;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class SshParsingAndErrorMappingTests
{
    [Theory]
    [InlineData("Linux\n", ServerOperatingSystem.Linux)]
    [InlineData("darwin\r\n", ServerOperatingSystem.MacOS)]
    [InlineData("FreeBSD", ServerOperatingSystem.Unknown)]
    [InlineData("", ServerOperatingSystem.Unknown)]
    [InlineData(null, ServerOperatingSystem.Unknown)]
    public void Uname_parser_accepts_only_supported_operating_systems(
        string? output,
        ServerOperatingSystem expected) =>
        Assert.Equal(expected, SshOperatingSystemParser.ParseUname(output));

    [Fact]
    public void Socket_error_is_mapped_even_when_wrapped()
    {
        var exception = new SshConnectionException(
            "connection failed",
            new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(
            SshConnectionErrorCode.ConnectionRefused,
            SshExceptionMapper.Map(exception));
    }

    [Theory]
    [MemberData(nameof(ExceptionMappings))]
    public void Typed_exceptions_have_stable_error_codes(
        Exception exception,
        SshConnectionErrorCode expected) =>
        Assert.Equal(expected, SshExceptionMapper.Map(exception));

    public static TheoryData<Exception, SshConnectionErrorCode> ExceptionMappings => new()
    {
        { new SshAuthenticationException(), SshConnectionErrorCode.AuthenticationFailed },
        { new SshOperationTimeoutException(), SshConnectionErrorCode.ConnectionTimedOut },
        {
            new SshConnectionException("key exchange", DisconnectReason.KeyExchangeFailed),
            SshConnectionErrorCode.UnsupportedAlgorithm
        },
        {
            new SshConnectionException("lost", DisconnectReason.ConnectionLost),
            SshConnectionErrorCode.RemoteDisconnected
        },
        { new OperationCanceledException(), SshConnectionErrorCode.Cancelled },
        { new InvalidOperationException(), SshConnectionErrorCode.Unexpected }
    };
}
