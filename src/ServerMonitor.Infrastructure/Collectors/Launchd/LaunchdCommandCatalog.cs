namespace ServerMonitor.Infrastructure.Collectors.Launchd;

/// <summary>
/// The fixed, code-controlled catalog of launchd commands (M11, READ-ONLY, macOS). Only observes state —
/// no bootstrap/bootout/kickstart/enable/disable, no sudo. Targets the <c>system</c> (daemon) domain
/// only; it never enumerates per-user LaunchAgents or GUI sessions (§24). No user/config/UI value is
/// concatenated. Note (validated on the real mac-mini during QA): "launchctl print system" may require
/// root on modern macOS; a permission error is mapped to a typed PermissionDenied state by the remote
/// source — never escalated with sudo (§8/§82).
/// </summary>
internal static class LaunchdCommandCatalog
{
    internal const string PrintSystem = "launchctl print system";

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        PrintSystem
    ]);
}
