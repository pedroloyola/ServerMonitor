namespace ServerMonitor.Infrastructure.Collectors.Systemd;

/// <summary>
/// The fixed, code-controlled catalog of systemd commands (M11, READ-ONLY, Linux). Only observes state —
/// no start/stop/restart/enable/disable/mask, no sudo. No user/config/UI value is concatenated. The
/// portable "--plain --no-legend" columnar form is used instead of "--output=json" because JSON is
/// version-gated (systemd ≥ 246) and a closed catalog cannot branch on version without an extra probe.
/// "LC_ALL=C" stabilizes formatting, decimals and messages (determinism), as the Linux df catalog does.
/// </summary>
internal static class SystemdCommandCatalog
{
    // Runtime-state inventory: UNIT LOAD ACTIVE SUB DESCRIPTION. --no-legend drops the header/footer,
    // --no-pager avoids blocking on a pager in a non-tty exec session, --plain removes tree markers and
    // bullet dots so the columns stay clean. No "--all": that would flood the list with hundreds of
    // dead/unreferenced units without monitoring value.
    internal const string ListUnits =
        "LC_ALL=C systemctl list-units --type=service --no-legend --no-pager --plain";

    // Enablement (enabled/disabled/static/masked …) in a single non-privileged command, joined to the
    // runtime units by unit id in the parser — avoids one "is-enabled <unit>" per service (O(n) and it
    // would require interpolating the unit name into a command, which the closed catalog forbids).
    internal const string ListUnitFiles =
        "LC_ALL=C systemctl list-unit-files --type=service --no-legend --no-pager";

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        ListUnits,
        ListUnitFiles
    ]);
}
