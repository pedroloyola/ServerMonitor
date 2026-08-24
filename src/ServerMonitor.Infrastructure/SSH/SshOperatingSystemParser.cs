using ServerMonitor.Core.Enums;

namespace ServerMonitor.Infrastructure.SSH;

public static class SshOperatingSystemParser
{
    public static ServerOperatingSystem ParseUname(string? output)
    {
        var value = output?.Trim();
        if (string.Equals(value, "Linux", StringComparison.OrdinalIgnoreCase))
        {
            return ServerOperatingSystem.Linux;
        }

        if (string.Equals(value, "Darwin", StringComparison.OrdinalIgnoreCase))
        {
            return ServerOperatingSystem.MacOS;
        }

        return ServerOperatingSystem.Unknown;
    }
}
