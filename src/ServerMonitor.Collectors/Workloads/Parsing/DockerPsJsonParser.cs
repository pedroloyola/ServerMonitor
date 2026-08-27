using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Workloads.Parsing;

/// <summary>
/// Pure parser for <c>docker ps -a --no-trunc --format '{{json .}}'</c> output: newline-delimited JSON
/// (one container object per line). Nothing here talks to SSH or Docker; it maps already-collected text
/// to Core models. A malformed line is skipped (that container is simply not listed), never fabricated,
/// and never aborts the whole batch. Container lifecycle comes from the engine's <c>State</c> field;
/// health is a <b>separate</b> axis parsed from the <c>Status</c> parenthetical because
/// <c>docker ps --format</c> exposes no <c>.Health</c> field. Strings are sanitized; the list is bounded
/// by <see cref="WorkloadLimits.MaxContainers"/>.
/// </summary>
public static partial class DockerPsJsonParser
{
    public static DockerContainerListResult Parse(string? psOutput)
    {
        if (string.IsNullOrWhiteSpace(psOutput))
        {
            return DockerContainerListResult.Empty;
        }

        var containers = new List<ContainerInfo>();
        var truncated = false;
        var hadInput = false;
        var malformedCount = 0;

        foreach (var rawLine in psOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            hadInput = true;

            ContainerInfo? container;
            try
            {
                container = ParseLine(line);
            }
            catch (JsonException)
            {
                // A single malformed line is a container we cannot trust; skip it, do not fabricate.
                malformedCount++;
                continue;
            }

            if (container is null)
            {
                // Valid JSON but not a usable container (wrong shape or no minimal identity).
                malformedCount++;
                continue;
            }

            if (containers.Count >= WorkloadLimits.MaxContainers)
            {
                truncated = true;
                break;
            }

            containers.Add(container);
        }

        return new DockerContainerListResult(containers, truncated, hadInput, malformedCount);
    }

    private static ContainerInfo? ParseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var rawId = ReadString(root, "ID");

        // Minimal identity: a real container always has a string id. A JSON object with the id missing or
        // of the wrong type is not a container and must not materialize as an empty/Unknown row.
        if (string.IsNullOrEmpty(rawId))
        {
            return null;
        }

        var rawStatus = ReadString(root, "Status");

        return new ContainerInfo
        {
            ContainerId = WorkloadTextSanitizer.Sanitize(ShortId(rawId)),
            Name = WorkloadTextSanitizer.Sanitize(FirstName(ReadString(root, "Names"))),
            Image = WorkloadTextSanitizer.Sanitize(ReadString(root, "Image")),
            State = MapState(ReadString(root, "State")),
            StatusText = WorkloadTextSanitizer.Sanitize(rawStatus),
            // Health is parsed from the RAW (docker-generated) status, before display sanitization.
            Health = MapHealth(rawStatus),
            CreatedAt = TryParseCreatedAt(ReadString(root, "CreatedAt"))
        };
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return id;
        }

        return id.Length > 12 ? id[..12] : id;
    }

    private static string? FirstName(string? names)
    {
        if (string.IsNullOrEmpty(names))
        {
            return names;
        }

        var comma = names.IndexOf(',');
        var first = comma >= 0 ? names[..comma] : names;
        return first.TrimStart('/');
    }

    private static ContainerState MapState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "created" => ContainerState.Created,
        "running" => ContainerState.Running,
        "restarting" => ContainerState.Restarting,
        "paused" => ContainerState.Paused,
        "exited" => ContainerState.Exited,
        "dead" => ContainerState.Dead,
        "removing" => ContainerState.Removing,
        _ => ContainerState.Unknown
    };

    private static ContainerHealth MapHealth(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return ContainerHealth.Unknown;
        }

        // docker embeds health in the status parenthetical; "(healthy)" is not a substring of
        // "(unhealthy)" (the '(' guards it), so token matching is order-independent.
        if (status.Contains("(unhealthy)", StringComparison.Ordinal))
        {
            return ContainerHealth.Unhealthy;
        }

        if (status.Contains("(healthy)", StringComparison.Ordinal))
        {
            return ContainerHealth.Healthy;
        }

        if (status.Contains("(health: starting)", StringComparison.Ordinal))
        {
            return ContainerHealth.Starting;
        }

        // A status with no health parenthetical means the container declares no health check.
        return ContainerHealth.None;
    }

    private static DateTimeOffset? TryParseCreatedAt(string? createdAt)
    {
        if (string.IsNullOrWhiteSpace(createdAt))
        {
            return null;
        }

        // Go's default time layout, e.g. "2024-06-01 12:34:56 +0000 UTC". The trailing zone abbreviation
        // is unparseable, so match only the numeric date/time/offset prefix and reconstruct an ISO value.
        var match = CreatedAtPattern().Match(createdAt);
        if (!match.Success)
        {
            return null;
        }

        var iso = $"{match.Groups["date"].Value}T{match.Groups["time"].Value}" +
                  $"{match.Groups["oh"].Value}:{match.Groups["om"].Value}";

        return DateTimeOffset.TryParse(
            iso,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
    }

    [GeneratedRegex(
        @"^\s*(?<date>\d{4}-\d{2}-\d{2})[ T](?<time>\d{2}:\d{2}:\d{2})\s*(?<oh>[+-]\d{2}):?(?<om>\d{2})")]
    private static partial Regex CreatedAtPattern();
}
