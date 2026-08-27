namespace ServerMonitor.Infrastructure.Collectors.Docker;

/// <summary>
/// The fixed, code-controlled catalog of Docker commands (M11, READ-ONLY). Every command only observes
/// state — there is no start/stop/restart/kill/exec/rm/update/pause/compose here, ever. No user,
/// configuration or UI value is concatenated into these strings; the CLI is English-only (Go), so its
/// output/errors are locale-stable. The same catalog serves Linux and macOS: Docker is probed
/// independently of the service manager (§69).
/// </summary>
internal static class DockerCommandCatalog
{
    // Availability probe + daemon version in one shot. "{{.Server.Version}}" only resolves when the
    // CLI can reach the daemon, so a single command distinguishes client-only from daemon-up; the
    // exit status / stderr then classify NotInstalled / PermissionDenied / Unavailable / Error.
    internal const string Version = "docker version --format '{{.Server.Version}}'";

    // Container inventory as NDJSON: "{{json .}}" emits one complete JSON object per line, robustly
    // parseable without jq. "-a" includes stopped/exited containers (a monitor must see what is not
    // running); "--no-trunc" keeps ids/values intact — truncation is done explicitly in the parser.
    internal const string ContainerList = "docker ps -a --no-trunc --format '{{json .}}'";

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        Version,
        ContainerList
    ]);
}
