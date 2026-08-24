using System.Globalization;

namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Parses "cat /proc/uptime" output. The first field is the system uptime
/// in seconds; the second (idle time) is not needed here.
/// </summary>
public static class ProcUptimeParser
{
    public static TimeSpan? Parse(string? uptimeOutput)
    {
        if (string.IsNullOrWhiteSpace(uptimeOutput))
        {
            return null;
        }

        var firstLine = uptimeOutput
            .Split('\n')[0]
            .TrimEnd('\r')
            .Trim();

        var tokens = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(
                tokens[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds) ||
            double.IsNaN(seconds) ||
            double.IsInfinity(seconds) ||
            seconds < 0)
        {
            return null;
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
