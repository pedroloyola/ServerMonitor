namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Parses "cat /proc/loadavg" output, e.g. "0.52 0.40 0.31 1/123 4567". The first three fields are the
/// 1, 5 and 15 minute load averages; the running/total tasks and the last PID are not needed. The file
/// comes from the kernel, so BusyBox (Alpine), Proxmox and containers print the same line. Returns null
/// (unknown) for empty, short or malformed output, never 0.
/// </summary>
public static class ProcLoadAvgParser
{
    public static LoadAverage? Parse(string? loadAvgOutput)
    {
        if (string.IsNullOrWhiteSpace(loadAvgOutput))
        {
            return null;
        }

        var firstLine = loadAvgOutput
            .Split('\n')[0]
            .TrimEnd('\r');

        var tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length < 3
            ? null
            : LoadAverage.FromTokens(tokens[0], tokens[1], tokens[2]);
    }
}
