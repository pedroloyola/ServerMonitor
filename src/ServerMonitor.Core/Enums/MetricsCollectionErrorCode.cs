namespace ServerMonitor.Core.Enums;

public enum MetricsCollectionErrorCode
{
    None,
    UnsupportedOperatingSystem,
    InvalidConfiguration,
    ConnectionFailed,
    NoMetricsAvailable,
    Cancelled,
    TimedOut,
    Unexpected
}
