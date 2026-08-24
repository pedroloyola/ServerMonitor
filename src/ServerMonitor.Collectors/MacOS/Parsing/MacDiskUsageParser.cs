using System.Globalization;

namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Parses "df -P -k /" output on macOS (BSD df). The POSIX (-P) format is one
/// data row of: Filesystem, 1024-blocks, Used, Available, Capacity, Mounted-on.
/// macOS df has no GNU -B byte flag, so -k reports 1024-byte blocks that are
/// multiplied to bytes here. Reads the last six whitespace tokens so a wrapped
/// filesystem-name line is still handled. The mount point must be exactly "/";
/// the percentage is taken from df's own Capacity column, which already
/// accounts for reserved blocks. Only the root volume is measured — no APFS
/// snapshots, no additional or network mounts.
/// </summary>
public static class MacDiskUsageParser
{
    private const long BlockBytes = 1024;

    public static MacDiskUsageResult Parse(string? dfOutput)
    {
        if (string.IsNullOrWhiteSpace(dfOutput))
        {
            return MacDiskUsageResult.Empty;
        }

        var dataLines = dfOutput
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Skip(1)
            .ToArray();

        if (dataLines.Length == 0)
        {
            return MacDiskUsageResult.Empty;
        }

        var tokens = dataLines
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        if (tokens.Length < 6)
        {
            return MacDiskUsageResult.Empty;
        }

        var mountedOn = tokens[^1];
        var capacityToken = tokens[^2];
        var availableToken = tokens[^3];
        var usedToken = tokens[^4];
        var totalToken = tokens[^5];

        if (!string.Equals(mountedOn, "/", StringComparison.Ordinal) || !capacityToken.EndsWith('%'))
        {
            return MacDiskUsageResult.Empty;
        }

        if (!int.TryParse(capacityToken[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var capacityPercent) ||
            capacityPercent is < 0 or > 100)
        {
            return MacDiskUsageResult.Empty;
        }

        if (!long.TryParse(totalToken, NumberStyles.None, CultureInfo.InvariantCulture, out var totalBlocks) ||
            totalBlocks <= 0 ||
            !long.TryParse(usedToken, NumberStyles.None, CultureInfo.InvariantCulture, out var usedBlocks) ||
            !long.TryParse(availableToken, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            usedBlocks > totalBlocks)
        {
            return MacDiskUsageResult.Empty;
        }

        try
        {
            checked
            {
                return new MacDiskUsageResult(totalBlocks * BlockBytes, usedBlocks * BlockBytes, capacityPercent);
            }
        }
        catch (OverflowException)
        {
            return MacDiskUsageResult.Empty;
        }
    }
}

public readonly record struct MacDiskUsageResult(long? TotalBytes, long? UsedBytes, double? UsagePercent)
{
    public static readonly MacDiskUsageResult Empty = new(null, null, null);
}
