using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Workloads.Parsing;

/// <summary>
/// Pure parser for the systemd service listing. It joins two read-only command outputs by unit id:
/// <list type="bullet">
///   <item><c>systemctl list-units --type=service --no-legend --no-pager --plain</c> — columns
///   <c>UNIT LOAD ACTIVE SUB DESCRIPTION</c> give the runtime state.</item>
///   <item><c>systemctl list-unit-files --type=service --no-legend --no-pager</c> — columns
///   <c>UNIT_FILE STATE [PRESET]</c> give the boot-startup configuration.</item>
/// </list>
/// Nothing here talks to SSH. Runtime <see cref="ServiceState"/> comes from <c>ACTIVE</c>/<c>SUB</c>;
/// <see cref="ServiceStartupState"/> from the joined unit-files state (absent → <c>null</c>, unknown).
/// Strings are sanitized; the list is bounded by <see cref="WorkloadLimits.MaxServices"/>.
/// </summary>
public static class SystemdServicesParser
{
    private const string ServiceSuffix = ".service";

    public static ServiceListResult Parse(string? listUnitsOutput, string? listUnitFilesOutput)
    {
        var startupByUnit = ParseUnitFiles(listUnitFilesOutput);

        if (string.IsNullOrWhiteSpace(listUnitsOutput))
        {
            return ServiceListResult.Empty;
        }

        var services = new List<ServiceInfo>();
        var truncated = false;
        var hadInput = false;
        var malformedCount = 0;

        foreach (var rawLine in listUnitsOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            hadInput = true;

            // UNIT LOAD ACTIVE SUB DESCRIPTION… — split into at most 5 pieces so the description keeps
            // its internal spaces; systemctl pads columns, so collapse runs of whitespace.
            var parts = line.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                malformedCount++;
                continue;
            }

            var unit = parts[0];
            if (!unit.EndsWith(ServiceSuffix, StringComparison.Ordinal))
            {
                malformedCount++; // stray/non-service line: present but not a usable service record.
                continue;
            }

            if (services.Count >= WorkloadLimits.MaxServices)
            {
                truncated = true;
                break;
            }

            var activeState = parts[2];
            var subState = parts[3];
            var description = parts.Length >= 5 ? parts[4] : null;

            services.Add(new ServiceInfo
            {
                Id = WorkloadTextSanitizer.Sanitize(unit),
                Name = WorkloadTextSanitizer.Sanitize(ShortName(unit)),
                DisplayName = WorkloadTextSanitizer.SanitizeOptional(description),
                State = MapState(activeState),
                SubState = WorkloadTextSanitizer.SanitizeOptional(subState),
                StartupState = startupByUnit.TryGetValue(unit, out var startup) ? startup : null
            });
        }

        return new ServiceListResult(services, truncated, hadInput, malformedCount);
    }

    private static Dictionary<string, ServiceStartupState> ParseUnitFiles(string? listUnitFilesOutput)
    {
        var map = new Dictionary<string, ServiceStartupState>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(listUnitFilesOutput))
        {
            return map;
        }

        foreach (var rawLine in listUnitFilesOutput.Split('\n'))
        {
            if (map.Count >= WorkloadLimits.MaxServices)
            {
                break;
            }

            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var unitFile = parts[0];
            if (!unitFile.EndsWith(ServiceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            map[unitFile] = MapStartup(parts[1]);
        }

        return map;
    }

    private static string ShortName(string unit) =>
        unit.EndsWith(ServiceSuffix, StringComparison.Ordinal)
            ? unit[..^ServiceSuffix.Length]
            : unit;

    private static ServiceState MapState(string? activeState) => activeState?.Trim().ToLowerInvariant() switch
    {
        // "active" and "reloading" are both up; SubState carries nuance ("running"/"exited"/…).
        "active" or "reloading" => ServiceState.Running,
        "activating" => ServiceState.Starting,
        "deactivating" => ServiceState.Stopping,
        "inactive" => ServiceState.Stopped,
        "failed" => ServiceState.Failed,
        _ => ServiceState.Unknown
    };

    private static ServiceStartupState MapStartup(string state) => state.Trim().ToLowerInvariant() switch
    {
        "enabled" or "enabled-runtime" => ServiceStartupState.Enabled,
        "disabled" => ServiceStartupState.Disabled,
        "static" or "indirect" or "generated" or "transient" or "alias" => ServiceStartupState.Static,
        "masked" or "masked-runtime" => ServiceStartupState.Masked,
        _ => ServiceStartupState.Unknown // recognized column, unrecognized value (bad/linked/…).
    };
}
