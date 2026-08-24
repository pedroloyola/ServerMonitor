namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Parses "cat /etc/os-release" KEY=VALUE output. Only NAME, VERSION,
/// VERSION_ID and PRETTY_NAME are read and stored; every other key is
/// discarded even if well-formed. Name prefers NAME, falling back to
/// PRETTY_NAME; version prefers VERSION_ID, falling back to VERSION. A
/// value with an opening quote but no matching closing quote (or a stray
/// quote with no opening one) is treated as invalid and dropped.
/// </summary>
public static class OsReleaseParser
{
    private const int MaximumDisplayValueLength = 256;

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "NAME", "VERSION", "VERSION_ID", "PRETTY_NAME"
    };

    public static OsReleaseParseResult Parse(string? osRelease)
    {
        if (string.IsNullOrWhiteSpace(osRelease))
        {
            return OsReleaseParseResult.Empty;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in osRelease.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (!AllowedKeys.Contains(key))
            {
                continue;
            }

            if (!TryUnquote(line[(separatorIndex + 1)..].Trim(), out var value))
            {
                continue;
            }

            values[key] = value;
        }

        var name = FirstNonEmpty(values, "NAME", "PRETTY_NAME");
        var version = FirstNonEmpty(values, "VERSION_ID", "VERSION");

        return new OsReleaseParseResult(name, version);
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryUnquote(string value, out string result)
    {
        result = value;
        if (value.Length == 0)
        {
            return IsSafeDisplayValue(result);
        }

        var opensWithQuote = value[0] is '"' or '\'';
        if (!opensWithQuote)
        {
            // An unquoted value must not contain a stray quote character.
            if (value.Contains('"') || value.Contains('\''))
            {
                return false;
            }

            return IsSafeDisplayValue(result);
        }

        if (value.Length < 2 || value[^1] != value[0])
        {
            // Opening quote without a matching closing quote: invalid.
            return false;
        }

        result = value[1..^1];
        return IsSafeDisplayValue(result);
    }

    private static bool IsSafeDisplayValue(string value) =>
        value.Length <= MaximumDisplayValueLength
        && !value.Any(char.IsControl);
}

public readonly record struct OsReleaseParseResult(string? Name, string? Version)
{
    public static readonly OsReleaseParseResult Empty = new(null, null);
}
