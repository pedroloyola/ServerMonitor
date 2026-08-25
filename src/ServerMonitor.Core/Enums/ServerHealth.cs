namespace ServerMonitor.Core.Enums;

/// <summary>
/// Normalized health of a server, distinct from <see cref="ServerConnectionState"/>.
/// Connection describes the last SSH outcome; health describes what the operator
/// should feel about the server. A server can be <c>AuthenticationFailed</c> at the
/// connection level while its health is <see cref="Unknown"/> (not <see cref="Offline"/>).
/// </summary>
public enum ServerHealth
{
    /// <summary>No usable data yet, or a non-transient problem needing the user's attention.</summary>
    Unknown,

    /// <summary>Reachable and every available metric is within warning thresholds.</summary>
    Healthy,

    /// <summary>Reachable but at least one metric crossed its warning threshold.</summary>
    Warning,

    /// <summary>Reachable but at least one metric crossed its critical threshold.</summary>
    Critical,

    /// <summary>Could not reach the server after the retry policy was exhausted.</summary>
    Offline
}
