namespace ServerMonitor.Core.Enums;

/// <summary>
/// Append only — never renumber. Not persisted today (history stores health only, the widget snapshot
/// carries no error code, the connection-state store is in memory), but resolved to resource keys by name.
/// </summary>
public enum SshConnectionErrorCode
{
    None,
    InvalidConfiguration,
    CredentialNotConfigured,
    CredentialUnavailable,
    PrivateKeyUnavailable,
    PrivateKeyInvalid,
    AuthenticationFailed,
    HostKeyUnknown,
    HostKeyMismatch,
    UnsupportedAlgorithm,
    DnsResolutionFailed,
    ConnectionRefused,
    HostUnreachable,
    NetworkUnavailable,
    ConnectionTimedOut,
    RemoteDisconnected,
    ProtocolError,
    Cancelled,
    Unexpected,

    // M14.4b route (jump host) failures. A jump-stage failure never reports a target code.
    JumpConnectionFailed,
    JumpAuthenticationFailed,
    JumpHostKeyUnknown,
    JumpHostKeyMismatch,
    JumpCredentialUnavailable,
    TargetUnreachableViaJump,

    // The TARGET's host key, seen through a jump. Distinct from HostKeyUnknown/HostKeyMismatch, which drive
    // the DIRECT trust flow: a routed target's key must only ever be trusted in the routed store.
    RoutedHostKeyUnknown,
    RoutedHostKeyMismatch,

    // M14.4b-2: the LOCAL end of the jump tunnel could not be established as required (IPv4 loopback,
    // ephemeral port, owned by this process). Never a jump or target diagnosis.
    LocalTunnelFailed
}
