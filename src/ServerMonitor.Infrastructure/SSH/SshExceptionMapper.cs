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

    /// <summary>
    /// M14.5 D-1: a failure before the peer's SSH identification line arrived, on a connection that was
    /// established (<see cref="ProvesEstablishedConnection"/>), means the port answered but the peer never
    /// identified as SSH — an HTTP or other service, or a server dropping the connection before its banner.
    /// Measured on SSH.NET 2026.0.0: the exception is the same whether or not non-SSH bytes were received, so
    /// both are <see cref="SshConnectionErrorCode.ProtocolError"/>. Everything else maps as <see cref="Map(Exception)"/>.
    /// </summary>
    public static SshConnectionErrorCode Map(Exception exception, bool identificationReceived) =>
        !identificationReceived && ProvesEstablishedConnection(exception)
            ? SshConnectionErrorCode.ProtocolError
            : Map(exception);

    /// <summary>
    /// True only for failures SSH.NET raises on an ESTABLISHED TCP connection: the peer closed it
    /// (<see cref="DisconnectReason.ConnectionLost"/>, "closed before a valid SSH identification string was
    /// received") or reset/aborted it (<see cref="SocketError.ConnectionReset"/>, <see cref="SocketError.ConnectionAborted"/>).
    /// A refused, unreachable, unresolved or timed-out connect is never one of these.
    /// </summary>
    public static bool ProvesEstablishedConnection(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Find<SocketException>(exception) is { } socketException)
        {
            return socketException.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
        }

        return exception is SshConnectionException { DisconnectReason: DisconnectReason.ConnectionLost };
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
        // An established connection the peer reset or that was aborted (measured: HTTP 400 then RST).
        SocketError.ConnectionReset or
        SocketError.ConnectionAborted => SshConnectionErrorCode.RemoteDisconnected,
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
