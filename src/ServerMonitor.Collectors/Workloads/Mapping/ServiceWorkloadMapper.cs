using ServerMonitor.Collectors.Workloads.Parsing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Workloads.Mapping;

/// <summary>
/// Pure mapper from raw service command outcomes to a <see cref="ServiceSnapshot"/>. The
/// <see cref="ServiceManager"/> is always resolved through <see cref="WorkloadManagerPolicy"/> (the
/// single Core routing authority, §69) — this class only classifies availability from exit/stderr and
/// parses the successful listing. systemd detection (systemctl present and acting as init) is derived
/// from the <c>list-units</c> outcome; launchd is macOS-only.
/// </summary>
public static class ServiceWorkloadMapper
{
    public static ServiceSnapshot Map(
        ServerOperatingSystem operatingSystem,
        RemoteCommandOutcome? systemdListUnits,
        RemoteCommandOutcome? systemdUnitFiles,
        RemoteCommandOutcome? launchdPrintSystem) => operatingSystem switch
    {
        ServerOperatingSystem.Linux => MapSystemd(systemdListUnits, systemdUnitFiles),
        ServerOperatingSystem.MacOS => MapLaunchd(launchdPrintSystem),
        _ => new ServiceSnapshot
        {
            // No supported manager was probed for this OS; routing decision stays in the policy.
            Manager = WorkloadManagerPolicy.Resolve(operatingSystem, systemdDetected: false),
            Availability = WorkloadServiceAvailability.Unknown
        }
    };

    private static ServiceSnapshot MapSystemd(RemoteCommandOutcome? units, RemoteCommandOutcome? unitFiles)
    {
        if (units is not { WasExecuted: true })
        {
            return new ServiceSnapshot
            {
                Manager = ServiceManager.Unsupported,
                Availability = WorkloadServiceAvailability.Unknown
            };
        }

        var stderr = units.StandardError ?? string.Empty;
        var missing = units.ExitStatus == 127 || WorkloadStderrSignals.CommandNotFound(stderr);
        var notInit = WorkloadStderrSignals.SystemdNotBooted(stderr);
        var detected = !missing && !notInit;
        var manager = WorkloadManagerPolicy.Resolve(ServerOperatingSystem.Linux, detected);

        var availability = ClassifySystemd(units, stderr, missing, notInit);
        if (availability != WorkloadServiceAvailability.Available)
        {
            return new ServiceSnapshot { Manager = manager, Availability = availability };
        }

        var unitFilesOutput = unitFiles is { WasExecuted: true, OutputExceededLimit: false, ExitStatus: 0 }
            ? unitFiles.StandardOutput
            : null;

        var parsed = SystemdServicesParser.Parse(units.StandardOutput, unitFilesOutput);
        if (parsed.IsUnrecognized)
        {
            // list-units answered (exit 0) but nothing valid parsed: corrupt/unrecognized, not empty.
            return new ServiceSnapshot { Manager = manager, Availability = WorkloadServiceAvailability.Error };
        }

        return new ServiceSnapshot
        {
            Manager = manager,
            Availability = WorkloadServiceAvailability.Available,
            Services = parsed.Services,
            Truncated = parsed.Truncated
        };
    }

    private static WorkloadServiceAvailability ClassifySystemd(
        RemoteCommandOutcome units,
        string stderr,
        bool missing,
        bool notInit)
    {
        if (missing)
        {
            return WorkloadServiceAvailability.NotInstalled;
        }

        if (notInit)
        {
            return WorkloadServiceAvailability.Unavailable; // systemd is not the running init (not PID 1).
        }

        if (units.OutputExceededLimit)
        {
            return WorkloadServiceAvailability.Error;
        }

        if (WorkloadStderrSignals.PermissionDenied(stderr) || WorkloadStderrSignals.SystemdAccessDenied(stderr))
        {
            return WorkloadServiceAvailability.PermissionDenied;
        }

        return units.ExitStatus == 0
            ? WorkloadServiceAvailability.Available
            : WorkloadServiceAvailability.Error;
    }

    private static ServiceSnapshot MapLaunchd(RemoteCommandOutcome? print)
    {
        var manager = WorkloadManagerPolicy.Resolve(ServerOperatingSystem.MacOS, systemdDetected: false);

        if (print is not { WasExecuted: true })
        {
            return new ServiceSnapshot { Manager = manager, Availability = WorkloadServiceAvailability.Unknown };
        }

        var stderr = print.StandardError ?? string.Empty;

        if (print.ExitStatus == 127 || WorkloadStderrSignals.CommandNotFound(stderr))
        {
            return new ServiceSnapshot { Manager = manager, Availability = WorkloadServiceAvailability.NotInstalled };
        }

        if (print.OutputExceededLimit)
        {
            return new ServiceSnapshot { Manager = manager, Availability = WorkloadServiceAvailability.Error };
        }

        // A successful exit is a valid dump: parse it. The success stdout is NEVER scanned for error
        // phrases — a legitimate service label/path could contain "permission"/"not permitted" and would
        // otherwise mask the entire inventory as PermissionDenied. Availability is decided by the exit
        // status and stderr, not by substrings inside a successful listing.
        if (print.ExitStatus == 0)
        {
            var parsed = LaunchdPrintSystemParser.Parse(print.StandardOutput);
            if (parsed.IsUnrecognized)
            {
                // Services block present but no valid row parsed: corrupt/unrecognized, not empty.
                return new ServiceSnapshot { Manager = manager, Availability = WorkloadServiceAvailability.Error };
            }

            return new ServiceSnapshot
            {
                Manager = manager,
                Availability = WorkloadServiceAvailability.Available,
                Services = parsed.Services,
                Truncated = parsed.Truncated
            };
        }

        // Non-zero exit: the command failed, so its output is not a valid inventory. Distinguish the R2
        // "system domain is root-only" case (EPERM/EIO / domain-print error) from a generic failure by
        // matching known launchctl error phrases on the FAILED command's stderr — and, because some
        // launchctl versions route the same error to stdout, on the failed stdout too. This is safe only
        // because we already returned for exit 0, so a successful listing is never inspected here.
        var deniedByError = WorkloadStderrSignals.LaunchdDenied(stderr) ||
                            WorkloadStderrSignals.LaunchdDenied(print.StandardOutput ?? string.Empty);
        return new ServiceSnapshot
        {
            Manager = manager,
            Availability = deniedByError
                ? WorkloadServiceAvailability.PermissionDenied
                : WorkloadServiceAvailability.Error
        };
    }
}
