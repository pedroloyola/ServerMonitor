using System.Net.Sockets;
using Renci.SshNet.Common;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>Where a single-hop ProxyJump attempt failed.</summary>
public enum ProxyJumpStage
{
    /// <summary>TCP connect, key exchange, host-key check and authentication to the jump host.</summary>
    Jump,

    /// <summary>Opening the direct-tcpip channel to the target through the authenticated jump host.</summary>
    Channel,

    /// <summary>The target's SSH handshake, host-key check and authentication, carried through the jump.</summary>
    Target
}

/// <summary>
/// Pure mapping of a ProxyJump failure to an <see cref="SshConnectionErrorCode"/> (M14.4b-1 §4), built on
/// the shapes measured with SSH.NET 2026.0.0:
/// jump auth = <see cref="SshAuthenticationException"/> on the jump client; jump host key rejected =
/// <see cref="SshConnectionException"/> after HostKeyReceived set CanTrust=false; jump TCP =
/// <see cref="SocketException"/>; a jump that refuses the direct-tcpip channel raises NO forwarded-port
/// exception and surfaces ONLY as a target-side <see cref="SshConnectionException"/> "closed before a valid
/// SSH identification string"; cancellation = <see cref="OperationCanceledException"/>; timeout =
/// <see cref="SshOperationTimeoutException"/>.
/// <para>
/// Stage separation is the invariant: a jump-stage failure never yields a target code (the user would be
/// sent to fix the wrong host), and a channel/target failure never yields a jump code. Only
/// <see cref="SshConnectionErrorCode.Cancelled"/> is stage-neutral.
/// </para>
/// </summary>
public static class ProxyJumpFailureClassifier
{
    // SSH.NET's exact text when the peer closes before sending its version line. Through a jump this is
    // what a refused/failed direct-tcpip channel looks like from the target client.
    private const string ClosedBeforeIdentification = "closed before a valid SSH identification string";

    public static SshConnectionErrorCode Classify(
        ProxyJumpStage stage,
        Exception exception,
        bool hostKeyRejected,
        bool hostKeyUnknown,
        bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (cancelled)
        {
            return SshConnectionErrorCode.Cancelled;
        }

        return stage switch
        {
            ProxyJumpStage.Jump => ClassifyJump(exception, hostKeyRejected, hostKeyUnknown),
            ProxyJumpStage.Channel => SshConnectionErrorCode.TargetUnreachableViaJump,
            ProxyJumpStage.Target => ClassifyTarget(exception, hostKeyRejected, hostKeyUnknown),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
    }

    private static SshConnectionErrorCode ClassifyJump(
        Exception exception,
        bool hostKeyRejected,
        bool hostKeyUnknown)
    {
        if (hostKeyRejected)
        {
            return hostKeyUnknown
                ? SshConnectionErrorCode.JumpHostKeyUnknown
                : SshConnectionErrorCode.JumpHostKeyMismatch;
        }

        if (exception is SshAuthenticationException)
        {
            return SshConnectionErrorCode.JumpAuthenticationFailed;
        }

        if (exception is SshPrivateKeyLoadException)
        {
            return SshConnectionErrorCode.JumpCredentialUnavailable;
        }

        // TCP, DNS, timeout, key exchange, protocol, anything else: the jump could not be established.
        return SshConnectionErrorCode.JumpConnectionFailed;
    }

    private static SshConnectionErrorCode ClassifyTarget(
        Exception exception,
        bool hostKeyRejected,
        bool hostKeyUnknown)
    {
        // Never HostKeyUnknown/HostKeyMismatch: those would route a routed target's key into the DIRECT
        // trust flow (direct store, bare host:port). The routed codes keep it in the routed scope.
        if (hostKeyRejected)
        {
            return hostKeyUnknown
                ? SshConnectionErrorCode.RoutedHostKeyUnknown
                : SshConnectionErrorCode.RoutedHostKeyMismatch;
        }

        if (exception is SshConnectionException connectionException
            && connectionException.Message.Contains(ClosedBeforeIdentification, StringComparison.OrdinalIgnoreCase))
        {
            return SshConnectionErrorCode.TargetUnreachableViaJump;
        }

        // The target transport is the tunnel, so a socket failure is the tunnel failing — never a target
        // DNS/refused/unreachable diagnosis.
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException)
            {
                return SshConnectionErrorCode.TargetUnreachableViaJump;
            }
        }

        // Not caller-cancelled (handled above), so an OperationCanceledException here is the timeout.
        if (exception is OperationCanceledException)
        {
            return SshConnectionErrorCode.ConnectionTimedOut;
        }

        return SshExceptionMapper.Map(exception);
    }
}
