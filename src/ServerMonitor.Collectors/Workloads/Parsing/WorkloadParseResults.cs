using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Workloads.Parsing;

/// <summary>
/// Diagnostic result of parsing a Docker container listing. Beyond the valid records it reports whether
/// there was any input (<see cref="HadInput"/>) and how many present-but-rejected records were seen
/// (<see cref="MalformedCount"/>). <see cref="IsUnrecognized"/> distinguishes a genuinely empty inventory
/// from a corrupt/unrecognized one: input was present but nothing valid could be materialized
/// (unknown ≠ empty). <see cref="Truncated"/> is set when the list was capped at
/// <see cref="WorkloadLimits.MaxContainers"/>.
/// </summary>
public sealed record DockerContainerListResult(
    IReadOnlyList<ContainerInfo> Containers,
    bool Truncated,
    bool HadInput,
    int MalformedCount)
{
    public static readonly DockerContainerListResult Empty = new([], false, false, 0);

    /// <summary>Input was present but no valid record parsed — corrupt/unrecognized, not empty.</summary>
    public bool IsUnrecognized => HadInput && Containers.Count == 0;
}

/// <summary>
/// Diagnostic result of parsing a service listing (systemd or launchd). See
/// <see cref="DockerContainerListResult"/> for the meaning of the diagnostic fields;
/// <see cref="Truncated"/> is set when the list was capped at <see cref="WorkloadLimits.MaxServices"/>.
/// </summary>
public sealed record ServiceListResult(
    IReadOnlyList<ServiceInfo> Services,
    bool Truncated,
    bool HadInput,
    int MalformedCount)
{
    public static readonly ServiceListResult Empty = new([], false, false, 0);

    /// <summary>Input was present but no valid record parsed — corrupt/unrecognized, not empty.</summary>
    public bool IsUnrecognized => HadInput && Services.Count == 0;
}
