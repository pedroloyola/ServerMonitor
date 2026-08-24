using System.Globalization;
using System.Net;

namespace ServerMonitor.Core.Models;

public sealed record SshEndpoint(string Host, int Port)
{
    public static SshEndpoint Create(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var trimmed = host.Trim().TrimEnd('.');
        var normalized = IPAddress.TryParse(trimmed, out var address)
            ? address.ToString()
            : new IdnMapping().GetAscii(trimmed).ToLowerInvariant();

        return new SshEndpoint(normalized, port);
    }

    public override string ToString() => Host.Contains(':', StringComparison.Ordinal)
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";
}
