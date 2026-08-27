using ServerMonitor.Collectors.Workloads.Parsing;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Workloads.Mapping;

/// <summary>
/// Pure mapper from the raw Docker command outcomes to a <see cref="DockerSnapshot"/>. Availability is
/// classified from the <c>docker version</c> probe's exit status and stderr — the distinction between
/// NotInstalled / PermissionDenied / Unavailable is impossible from stdout alone. Only when the daemon
/// answered the probe is the container list parsed; a listing failure after a good probe is a transient
/// <see cref="DockerAvailability.Error"/>, never fabricated data.
/// </summary>
public static class DockerWorkloadMapper
{
    public static DockerSnapshot Map(RemoteCommandOutcome? version, RemoteCommandOutcome? containerList)
    {
        if (version is not { WasExecuted: true })
        {
            // Docker was not probed (e.g. IncludeDocker=false or session never ran commands).
            return DockerSnapshot.Unknown;
        }

        var availability = ClassifyAvailability(version);
        if (availability != DockerAvailability.Available)
        {
            return new DockerSnapshot { Availability = availability };
        }

        if (containerList is not { WasExecuted: true, OutputExceededLimit: false, ExitStatus: 0 } ||
            containerList.StandardOutput is null)
        {
            // The daemon answered the version probe but the listing did not complete cleanly.
            return new DockerSnapshot { Availability = DockerAvailability.Error };
        }

        var parsed = DockerPsJsonParser.Parse(containerList.StandardOutput);
        if (parsed.IsUnrecognized)
        {
            // Non-empty output that produced no valid record: corrupt or an incompatible upstream format,
            // not a real empty inventory (unknown ≠ empty).
            return new DockerSnapshot { Availability = DockerAvailability.Error };
        }

        return new DockerSnapshot
        {
            Availability = DockerAvailability.Available,
            Containers = parsed.Containers,
            Truncated = parsed.Truncated
        };
    }

    private static DockerAvailability ClassifyAvailability(RemoteCommandOutcome version)
    {
        if (version.OutputExceededLimit)
        {
            return DockerAvailability.Error;
        }

        var stderr = version.StandardError ?? string.Empty;

        if (version.ExitStatus == 127 || WorkloadStderrSignals.CommandNotFound(stderr))
        {
            return DockerAvailability.NotInstalled;
        }

        if (WorkloadStderrSignals.PermissionDenied(stderr))
        {
            return DockerAvailability.PermissionDenied;
        }

        if (WorkloadStderrSignals.DockerDaemonUnreachable(stderr))
        {
            return DockerAvailability.Unavailable;
        }

        return version.ExitStatus == 0 ? DockerAvailability.Available : DockerAvailability.Error;
    }
}
