using System.Globalization;

namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Parses "LC_ALL=C df -P -B1 /" output. Reads the last six whitespace
/// tokens from the data rows so a wrapped filesystem-name line (df wraps
/// onto its own line when the name is long) is still handled correctly.
/// The mount point must be exactly "/", and the usage percentage is taken
/// verbatim from df's own Capacity column rather than recomputed from
/// used/total, since df already accounts for filesystem-reserved blocks
/// that a naive division would miss.
/// </summary>
public static class DiskUsageParser
{
    public static DiskUsageParseResult Parse(string? dfOutput)
    {
        if (string.IsNullOrWhiteSpace(dfOutput))
        {
            return DiskUsageParseResult.Empty;
        }

        var dataLines = dfOutput
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Skip(1)
            .ToArray();

        if (dataLines.Length == 0)
        {
            return DiskUsageParseResult.Empty;
        }

        var tokens = dataLines
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        // Filesystem name (>=1 token) + 1-blocks + used + available + capacity% + mounted-on.
        if (tokens.Length < 6)
        {
            return DiskUsageParseResult.Empty;
        }

        var mountedOn = tokens[^1];
        var capacityToken = tokens[^2];
        var availableToken = tokens[^3];
        var usedToken = tokens[^4];
        var totalToken = tokens[^5];

        if (!string.Equals(mountedOn, "/", StringComparison.Ordinal))
        {
            return DiskUsageParseResult.Empty;
        }

        if (!capacityToken.EndsWith('%'))
        {
            return DiskUsageParseResult.Empty;
        }

        if (!int.TryParse(
                capacityToken[..^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var capacityPercent) ||
            capacityPercent is < 0 or > 100)
        {
            return DiskUsageParseResult.Empty;
        }

        if (!long.TryParse(totalToken, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
            total <= 0 ||
            !long.TryParse(usedToken, NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
            !long.TryParse(availableToken, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            used > total)
        {
            return DiskUsageParseResult.Empty;
        }

        return new DiskUsageParseResult(total, used, capacityPercent);
    }
}

public readonly record struct DiskUsageParseResult(long? TotalBytes, long? UsedBytes, double? UsagePercent)
{
    public static readonly DiskUsageParseResult Empty = new(null, null, null);
}
