using System.Globalization;

namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Parses "sysctl -n vm.loadavg hw.logicalcpu" output: two lines, "{ 1.23 1.45 1.67 }" and the logical
/// CPU count ("8"). Each line is read on its own, so a malformed count line never hides a valid load and
/// the reverse; a lone load line (the count missing) keeps the load. Both values come from ONE exec, so
/// when sysctl itself fails (non-zero exit, for example an unknown name) the session gets no output at all
/// and both are unknown. Anything that does not match exactly is null (unknown), never 0.
/// </summary>
public static class MacLoadAverageParser
{
    private const int MaxCountLength = 6;

    public static MacLoadAverageResult Parse(string? sysctlOutput)
    {
        if (string.IsNullOrWhiteSpace(sysctlOutput))
        {
            return default;
        }

        var lines = sysctlOutput
            .Split('\n')
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        return lines.Length switch
        {
            1 => new MacLoadAverageResult(ParseLoad(lines[0]), null),
            2 => new MacLoadAverageResult(ParseLoad(lines[0]), ParseCount(lines[1])),
            _ => default
        };
    }

    private static LoadAverage? ParseLoad(string line)
    {
        if (!line.StartsWith('{') || !line.EndsWith('}'))
        {
            return null;
        }

        var tokens = line[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 3
            ? LoadAverage.FromTokens(tokens[0], tokens[1], tokens[2])
            : null;
    }

    private static int? ParseCount(string line) =>
        line.Length <= MaxCountLength &&
        int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var count) &&
        count > 0
            ? count
            : null;
}

/// <summary>The macOS load average and logical CPU count; either may be unknown on its own.</summary>
public readonly record struct MacLoadAverageResult(LoadAverage? Load, int? LogicalCpuCount);
