namespace ServerMonitor.Core.Enums;

public enum ServerConnectionState
{
    NeverConnected,
    Connecting,
    Connected,
    AuthenticationFailed,
    HostKeyUnknown,
    HostKeyMismatch,
    TimedOut,
    Unreachable,
    Cancelled,
    Error
}
