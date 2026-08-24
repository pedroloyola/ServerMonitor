namespace ServerMonitor.Collectors.MacOS.Parsing;

/// <summary>
/// Parses "sw_vers" KEY:VALUE output. Only ProductName, ProductVersion and
/// BuildVersion are read; unknown lines are ignored. Values are trimmed,
/// length-capped and rejected if they contain control characters. Commercial
/// version names (Sonoma, Sequoia, …) are intentionally not mapped in this
/// milestone.
/// </summary>
public static class SwVersParser
{
    private const int MaxValueLength = 128;

    public static SwVersResult Parse(string? swVers)
    {
        if (string.IsNullOrWhiteSpace(swVers))
        {
            return SwVersResult.Empty;
        }

        string? productName = null;
        string? productVersion = null;
        string? buildVersion = null;

        foreach (var rawLine in swVers.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (!TrySanitize(line[(separatorIndex + 1)..].Trim(), out var value))
            {
                continue;
            }

            switch (key)
            {
                case "ProductName": productName = value; break;
                case "ProductVersion": productVersion = value; break;
                case "BuildVersion": buildVersion = value; break;
            }
        }

        return new SwVersResult(productName, productVersion, buildVersion);
    }

    private static bool TrySanitize(string value, out string? result)
    {
        result = null;
        if (value.Length is 0 or > MaxValueLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        result = value;
        return true;
    }
}

public readonly record struct SwVersResult(string? ProductName, string? ProductVersion, string? BuildVersion)
{
    public static readonly SwVersResult Empty = new(null, null, null);
}
