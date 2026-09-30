using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// M14.5 — what the editor shows under a failed "Test connection" step: a localized hint
/// (<c>ConnectionHint{code}</c>, present for every code but None in every culture), an optional command the
/// user can copy and run on the server, and whether the "How do I prepare my server?" helper is offered.
/// Commands are fixed text: no form value is ever substituted into them, and none uses sudo/root.
/// </summary>
public static class ConnectionDiagnosis
{
    public static string HintKey(SshConnectionErrorCode code) => $"ConnectionHint{code}";

    /// <summary>A read-only command worth running on the server for this failure, or null.</summary>
    public static string? CommandFor(SshConnectionErrorCode code) => code switch
    {
        SshConnectionErrorCode.ConnectionRefused => "systemctl status ssh",
        SshConnectionErrorCode.AuthenticationFailed => "cat ~/.ssh/authorized_keys",
        SshConnectionErrorCode.JumpAuthenticationFailed => "cat ~/.ssh/authorized_keys",
        _ => null
    };

    /// <summary>Port and authentication failures of the server itself link to the preparation helper.</summary>
    public static bool OffersPrepHelp(SshConnectionErrorCode code) => code is
        SshConnectionErrorCode.ConnectionRefused
        or SshConnectionErrorCode.ConnectionTimedOut
        or SshConnectionErrorCode.HostUnreachable
        or SshConnectionErrorCode.AuthenticationFailed
        or SshConnectionErrorCode.PrivateKeyUnavailable;
}
