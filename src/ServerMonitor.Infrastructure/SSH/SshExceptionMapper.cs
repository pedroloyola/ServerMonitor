using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.Infrastructure.SSH;

public static class SshExceptionMapper
{
    public static SshConnectionErrorCode Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return SshConnectionErrorCode.Cancelled;
        }

        if (exception is SshOperationTimeoutException or TimeoutException)
        {
            return SshConnectionErrorCode.ConnectionTimedOut;
        }

        if (exception is SshAuthenticationException)
        {
            return SshConnectionErrorCode.AuthenticationFailed;
        }

        if (Find<SocketException>(exception) is { } socketException)
        {
            return MapSocket(socketException.SocketErrorCode);
        }

        if (exception is SshConnectionException connectionException)
        {
            return connectionException.DisconnectReason switch
            {
                DisconnectReason.KeyExchangeFailed => SshConnectionErrorCode.UnsupportedAlgorithm,
                DisconnectReason.ProtocolError or
                DisconnectReason.ProtocolVersionNotSupported or
                DisconnectReason.MacError or
                DisconnectReason.CompressionError => SshConnectionErrorCode.ProtocolError,
                DisconnectReason.ConnectionLost or
                DisconnectReason.ByApplication or
                DisconnectReason.ServiceNotAvailable => SshConnectionErrorCode.RemoteDisconnected,
                DisconnectReason.HostNotAllowedToConnect => SshConnectionErrorCode.HostUnreachable,
                _ => SshConnectionErrorCode.ProtocolError
            };
        }

        if (exception is SshException)
        {
            return SshConnectionErrorCode.ProtocolError;
        }

        return SshConnectionErrorCode.Unexpected;
    }

    private static SshConnectionErrorCode MapSocket(SocketError error) => error switch
    {
        SocketError.HostNotFound or
        SocketError.NoData or
        SocketError.TryAgain => SshConnectionErrorCode.DnsResolutionFailed,
        SocketError.ConnectionRefused => SshConnectionErrorCode.ConnectionRefused,
        SocketError.HostUnreachable => SshConnectionErrorCode.HostUnreachable,
        SocketError.NetworkDown or
        SocketError.NetworkReset or
        SocketError.NetworkUnreachable => SshConnectionErrorCode.NetworkUnavailable,
        SocketError.TimedOut => SshConnectionErrorCode.ConnectionTimedOut,
        _ => SshConnectionErrorCode.Unexpected
    };

    private static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }
}
