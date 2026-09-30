namespace ServerMonitor.Core.Enums;

/// <summary>
/// M14.5 — the four user-visible steps of "Test connection", in order. A result's
/// <see cref="Models.SshConnectionResult.ReachedStage"/> is the LAST step that completed; the step after it is
/// the one that failed. Set by the connection service from what it actually observed, never inferred by the UI.
/// Exception (races): a result never carries less than what its progress already reported, so a host-key
/// mismatch or a jump failure can arrive with <see cref="HostKeyVerified"/>; consumers decide the failed step by
/// the error-code family first (see <see cref="Models.SshConnectionResult.ReachedStage"/>).
/// Append only — never renumber.
/// </summary>
public enum SshConnectionStage
{
    /// <summary>Nothing completed: the SSH port was not reached (DNS, refused, unreachable, timeout, bad config).</summary>
    None,

    /// <summary>The SSH port answered (TCP connected; for a routed server: reached THROUGH the jump host).</summary>
    PortReachable,

    /// <summary>The presented host key matched the trusted key for this hop (SHA-256). Never set on first sight.</summary>
    HostKeyVerified,

    /// <summary>The server accepted the credential (key or password).</summary>
    Authenticated,

    /// <summary>The remote OS was identified as Linux or macOS (uname). Unknown OS stops at <see cref="Authenticated"/>.</summary>
    OperatingSystemIdentified
}
