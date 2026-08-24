namespace ServerMonitor.Core.Enums;

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
    Unexpected
}
