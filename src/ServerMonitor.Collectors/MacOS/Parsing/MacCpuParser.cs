using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Computes global CPU usage from "top -l 2 -n 0" output. macOS exposes no
/// cumulative per-CPU tick counter through a base-system CLI (the Linux
/// /proc/stat delta approach is not available), so top self-samples over ~1s
/// and prints a "CPU usage:" line per sample. The last line reflects the
/// interval; usage is user + sys. A computed 0% is a real value, not unknown;
/// unparseable input yields null.
/// </summary>
public static partial class MacCpuParser
{
    private const int MaxLines = 512;

    public static double? CalculateUsagePercent(string? topOutput)
    {
        if (string.IsNullOrWhiteSpace(topOutput))
        {
            return null;
        }

        string? lastCpuLine = null;
        var lineCount = 0;
        foreach (var rawLine in topOutput.Split('\n'))
        {
            if (++lineCount > MaxLines)
            {
                break;
            }

            var line = rawLine.TrimEnd('\r').TrimStart();
            if (line.StartsWith("CPU usage:", StringComparison.Ordinal))
            {
                lastCpuLine = line;
            }
        }

        if (lastCpuLine is null)
        {
            return null;
        }

        var match = CpuUsageRegex().Match(lastCpuLine);
        if (!match.Success ||
            !TryParsePercent(match.Groups["user"].Value, out var user) ||
            !TryParsePercent(match.Groups["sys"].Value, out var system))
        {
            return null;
        }

        var usage = user + system;
        if (double.IsNaN(usage) || double.IsInfinity(usage))
        {
            return null;
        }

        return Math.Clamp(usage, 0d, 100d);
    }

    private static bool TryParsePercent(string token, out double value)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value < 0)
        {
            value = 0;
            return false;
        }

        return true;
    }

    [GeneratedRegex(
        @"(?<user>-?[0-9.]+)%\s*user,\s*(?<sys>-?[0-9.]+)%\s*sys",
        RegexOptions.CultureInvariant)]
    private static partial Regex CpuUsageRegex();
}
