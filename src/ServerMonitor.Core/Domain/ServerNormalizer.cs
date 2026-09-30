using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.Core.Domain;

/// <summary>
/// The one normalization every saved server goes through (extracted from <see cref="ServerService"/>, M14.6
/// V3): trim name/host/username, canonical full key paths (<see cref="Path.GetFullPath(string)"/> is a pure
/// string operation, no I/O), the refresh-interval policy, and the same for a route's jump host. A restored
/// server is accepted only when normalizing it changes nothing, so a backup can never plant a value the
/// editor could not have produced.
/// </summary>
public static class ServerNormalizer
{
    public static ServerInput Normalize(ServerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input with
        {
            Name = Text(input.Name),
            Host = Text(input.Host),
            Username = Text(input.Username),
            PrivateKeyPath = KeyPath(input.PrivateKeyPath),
            RefreshIntervalSeconds = RefreshIntervalPolicy.Normalize(input.RefreshIntervalSeconds),
            Route = Normalize(input.Route)
        };
    }

    public static Server Normalize(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server with
        {
            Name = Text(server.Name),
            Host = Text(server.Host),
            Username = Text(server.Username),
            PrivateKeyPath = KeyPath(server.PrivateKeyPath),
            RefreshIntervalSeconds = RefreshIntervalPolicy.Normalize(server.RefreshIntervalSeconds),
            Route = Normalize(server.Route)
        };
    }

    public static ServerRoute? Normalize(ServerRoute? route) =>
        route?.Jump is not { } jump
            ? route
            : route with
            {
                Jump = jump with
                {
                    Host = Text(jump.Host),
                    Username = Text(jump.Username),
                    PrivateKeyPath = KeyPath(jump.PrivateKeyPath)
                }
            };

    /// <summary>True when <paramref name="path"/> is fully qualified and already canonical, which rejects
    /// relative (<c>key</c>) and drive-relative (<c>C:key</c>) forms. Pure string check.</summary>
    public static bool IsCanonicalKeyPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Text(string? value) => (value ?? string.Empty).Trim();

    private static string? KeyPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
}
