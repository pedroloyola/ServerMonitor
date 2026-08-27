using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Workloads.Parsing;

/// <summary>
/// Pure parser for <c>launchctl print system</c> output (macOS system/daemon domain only, §24). The
/// relevant part is the <c>services = { … }</c> table, whose rows are <c>PID  &lt;last-exit&gt;  LABEL</c>.
/// <para>
/// <b>State (H-04, finalized against the real macOS 26.6 mac-mini dump).</b> The runtime state is derived
/// from column 1 only: a positive PID → <see cref="ServiceState.Running"/>, otherwise
/// <see cref="ServiceState.Stopped"/>. The second column is the <i>last</i> exit token (observed values
/// <c>{-, 0, 1}</c>) and is deliberately <b>not</b> mapped to <see cref="ServiceState.Failed"/>: on the real
/// host three legitimate one-shot loaders (<c>com.apple.wifiFirmwareLoader</c>,
/// <c>com.apple.iomfb_fdr_loader</c>, and a third custom job) exit 1 <i>by design</i>,
/// and this summary lacks the KeepAlive / intended-state signal needed to tell a genuine failure from a
/// normal non-zero one-shot exit. Distinguishing them would require the per-service
/// <c>print system/&lt;label&gt;</c> detail (O(n), outside the closed read-only catalog). Reporting a
/// not-running job as Stopped is the honest floor: a rare genuine crash is under-reported rather than
/// healthy one-shots over-reported as Failed (unknown ≠ fabricated). launchd therefore never yields Failed
/// at this level. See ADR-016 §H-04.
/// </para>
/// launchd exposes no description, sub-state or enable-state here, so those Core fields stay <c>null</c>
/// (§60/§61). Nothing talks to SSH; strings are sanitized and the list is bounded by
/// <see cref="WorkloadLimits.MaxServices"/>. The result is diagnostic: a services block that is present but
/// yields no valid row is <see cref="ServiceListResult.IsUnrecognized"/> (corrupt), not empty.
/// </summary>
public static class LaunchdPrintSystemParser
{
    public static ServiceListResult Parse(string? printSystemOutput)
    {
        if (string.IsNullOrWhiteSpace(printSystemOutput))
        {
            return ServiceListResult.Empty;
        }

        var services = new List<ServiceInfo>();
        var truncated = false;
        var insideBlock = false;
        var hadInput = false;
        var malformedCount = 0;

        foreach (var rawLine in printSystemOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!insideBlock)
            {
                if (IsServicesBlockStart(line))
                {
                    insideBlock = true;
                }

                continue;
            }

            if (line == "}")
            {
                break; // end of the services table.
            }

            hadInput = true; // a row line inside the services block.

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                malformedCount++;
                continue;
            }

            if (services.Count >= WorkloadLimits.MaxServices)
            {
                truncated = true;
                break;
            }

            var pid = parts[0];
            var label = parts[2];

            // Name is the FULL launchd label. Reverse-DNS labels (com.apple.sshd, com.acme.agent) must not
            // collapse to a leading/trailing segment — the leading segment is almost always "com" and even
            // the trailing one can collide across labels. The whole label is the stable, distinct identity
            // (mirroring how the systemd unit id is kept whole).
            var sanitizedLabel = WorkloadTextSanitizer.Sanitize(label);
            services.Add(new ServiceInfo
            {
                Id = sanitizedLabel,
                Name = sanitizedLabel,
                DisplayName = null,
                State = MapState(pid),
                SubState = null,
                StartupState = null
            });
        }

        return new ServiceListResult(services, truncated, hadInput, malformedCount);
    }

    private static bool IsServicesBlockStart(string trimmedLine) =>
        trimmedLine.Replace(" ", string.Empty) == "services={";

    // Runtime state is derived from column 1 (PID) only. A positive PID means the daemon is running now;
    // anything else (0 or a non-PID token) means it is not running → Stopped. The second column is the LAST
    // exit token and is intentionally NOT used for the state, so a not-running job is never over-reported as
    // Failed. See <see cref="LaunchdPrintSystemParser"/> for the H-04 rationale (validated against the real
    // macOS 26.6 dump, where three legitimate one-shot loaders exit 1 by design).
    private static ServiceState MapState(string pid) =>
        int.TryParse(pid, out var pidValue) && pidValue > 0
            ? ServiceState.Running
            : ServiceState.Stopped;
}
