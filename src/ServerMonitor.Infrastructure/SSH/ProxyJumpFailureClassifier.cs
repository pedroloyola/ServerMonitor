using ServerMonitor.Core.Enums;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>Where a single-hop ProxyJump attempt failed.</summary>
public enum ProxyJumpStage
{
    /// <summary>TCP connect, key exchange, host-key check and authentication to the jump host.</summary>
    Jump,

    /// <summary>The LOCAL loopback listener of the tunnel (bind / ownership proof).</summary>
    TunnelListen,

    /// <summary>
    /// Everything carried through the tunnel: the jump's direct-tcpip channel, the target's SSH handshake,
    /// host-key check, authentication and the session itself.
    /// </summary>
    Target
}

/// <summary>
/// The observed STATE of a failed ProxyJump attempt. No exception text, no SSH.NET message: only booleans and
/// the exception-type mapping (<see cref="SshExceptionMapper"/>) of the failure.
/// </summary>
public readonly record struct ProxyJumpFailure
{
    public required ProxyJumpStage Stage { get; init; }

    /// <summary>The caller cancelled.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The operation deadline elapsed (linked timeout, or the SSH.NET timeout of the same value).</summary>
    public bool TimedOut { get; init; }

    /// <summary>The jump session was still connected when the failure was observed.</summary>
    public bool JumpConnected { get; init; }

    /// <summary>The failing hop's SSH identification string was received (<c>ServerVersion</c> set).</summary>
    public bool IdentificationReceived { get; init; }

    public bool HostKeyRejected { get; init; }

    /// <summary>The rejected key had no entry in its store (as opposed to a different entry).</summary>
    public bool HostKeyUnknown { get; init; }

    /// <summary><see cref="SshExceptionMapper"/>'s code for the failure (type-based, never text).</summary>
    public SshConnectionErrorCode MappedCode { get; init; }
}

/// <summary>
/// Pure, structural mapping of a ProxyJump failure to an <see cref="SshConnectionErrorCode"/> (M14.4b-2 §3).
/// Through a jump, a refused/failed direct-tcpip channel raises no forwarded-port exception: the ONLY signal is
/// that the target client never received an identification string (measured with SSH.NET 2026.0.0: null
/// <c>ServerVersion</c> after a refused channel, after a jump-side connect failure and before a silent
/// target's banner; set after a successful identification, including a rejected key).
/// <para>
/// Order: cancelled → timeout → jump dropped → no identification → host key → authentication → the target
/// mapping. Stage separation is the invariant: a jump-stage failure never yields a target code, a target-stage
/// failure never yields a jump code other than the jump dropping (<see cref="SshConnectionErrorCode.JumpConnectionFailed"/>),
/// and nothing through a jump ever yields the DIRECT trust codes or a direct network diagnosis.
/// </para>
/// </summary>
public static class ProxyJumpFailureClassifier
{
    public static SshConnectionErrorCode Classify(in ProxyJumpFailure failure)
    {
        if (failure.Cancelled)
        {
            return SshConnectionErrorCode.Cancelled;
        }

        return failure.Stage switch
        {
            ProxyJumpStage.Jump => ClassifyJump(failure),
            ProxyJumpStage.TunnelListen => SshConnectionErrorCode.LocalTunnelFailed,
            ProxyJumpStage.Target => ClassifyTarget(failure),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
    }

    private static SshConnectionErrorCode ClassifyJump(in ProxyJumpFailure failure)
    {
        if (failure.HostKeyRejected)
        {
            return failure.HostKeyUnknown
                ? SshConnectionErrorCode.JumpHostKeyUnknown
                : SshConnectionErrorCode.JumpHostKeyMismatch;
        }

        return failure.MappedCode switch
        {
            SshConnectionErrorCode.AuthenticationFailed => SshConnectionErrorCode.JumpAuthenticationFailed,
            SshConnectionErrorCode.CredentialNotConfigured or
            SshConnectionErrorCode.CredentialUnavailable or
            SshConnectionErrorCode.PrivateKeyUnavailable or
            SshConnectionErrorCode.PrivateKeyInvalid => SshConnectionErrorCode.JumpCredentialUnavailable,
            // TCP, DNS, timeout, key exchange, protocol, anything else: the jump could not be established.
            _ => SshConnectionErrorCode.JumpConnectionFailed
        };
    }

    private static SshConnectionErrorCode ClassifyTarget(in ProxyJumpFailure failure)
    {
        if (failure.TimedOut)
        {
            return SshConnectionErrorCode.ConnectionTimedOut;
        }

        if (!failure.JumpConnected)
        {
            return SshConnectionErrorCode.JumpConnectionFailed;
        }

        if (!failure.IdentificationReceived)
        {
            return SshConnectionErrorCode.TargetUnreachableViaJump;
        }

        // Never HostKeyUnknown/HostKeyMismatch: those are the DIRECT trust flow (direct store, bare host:port).
        if (failure.HostKeyRejected)
        {
            return failure.HostKeyUnknown
                ? SshConnectionErrorCode.RoutedHostKeyUnknown
                : SshConnectionErrorCode.RoutedHostKeyMismatch;
        }

        return failure.MappedCode switch
        {
            SshConnectionErrorCode.AuthenticationFailed => SshConnectionErrorCode.AuthenticationFailed,
            // The target transport IS the tunnel: a socket failure is the tunnel, never a target network diagnosis.
            SshConnectionErrorCode.DnsResolutionFailed or
            SshConnectionErrorCode.ConnectionRefused or
            SshConnectionErrorCode.HostUnreachable or
            SshConnectionErrorCode.NetworkUnavailable => SshConnectionErrorCode.TargetUnreachableViaJump,
            // Not caller-cancelled (handled first), so a cancellation here is the deadline.
            SshConnectionErrorCode.Cancelled or
            SshConnectionErrorCode.ConnectionTimedOut => SshConnectionErrorCode.ConnectionTimedOut,
            // A key refusal that was not flagged is still a refusal in the ROUTED scope.
            SshConnectionErrorCode.HostKeyUnknown => SshConnectionErrorCode.RoutedHostKeyUnknown,
            SshConnectionErrorCode.HostKeyMismatch => SshConnectionErrorCode.RoutedHostKeyMismatch,
            SshConnectionErrorCode.None => SshConnectionErrorCode.Unexpected,
            var code when IsJumpCode(code) => SshConnectionErrorCode.Unexpected,
            var code => code
        };
    }

    private static bool IsJumpCode(SshConnectionErrorCode code) => code is
        SshConnectionErrorCode.JumpConnectionFailed or
        SshConnectionErrorCode.JumpAuthenticationFailed or
        SshConnectionErrorCode.JumpHostKeyUnknown or
        SshConnectionErrorCode.JumpHostKeyMismatch or
        SshConnectionErrorCode.JumpCredentialUnavailable or
        SshConnectionErrorCode.LocalTunnelFailed;
}
