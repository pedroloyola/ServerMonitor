namespace ServerMonitor.Collectors.Linux.Parsing;

/// <summary>
/// Parses "cat /proc/sys/kernel/hostname" output: a single trimmed line, no
/// control characters, at most 255 characters (RFC 1035 hostname limit).
/// Anything with more than one line of content is rejected outright rather
/// than guessing which line is the real hostname.
/// </summary>
public static class HostnameParser
{
    private const int MaxLength = 255;

    public static string? Parse(string? hostnameOutput)
    {
        if (string.IsNullOrWhiteSpace(hostnameOutput))
        {
            return null;
        }

        var trimmed = hostnameOutput.TrimEnd('\r', '\n');
        if (trimmed.Contains('\n'))
        {
            return null;
        }

        var hostname = trimmed.Trim();
        if (hostname.Length == 0 || hostname.Length > MaxLength)
        {
            return null;
        }

        foreach (var ch in hostname)
        {
            if (char.IsControl(ch))
            {
                return null;
            }
        }

        return hostname;
    }
}
