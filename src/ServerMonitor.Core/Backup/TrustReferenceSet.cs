using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Backup;

/// <summary>
/// The trusted host keys a set of servers can actually use (M14.6 H-4, referenced-only trust). Mirrors the
/// connect path (<c>SshConnectionService</c>) lookup for lookup:
/// <list type="bullet">
/// <item>a DIRECT server looks up its own endpoint in the direct store;</item>
/// <item>a ROUTED server looks up its jump endpoint in the direct store and <c>(jump, target)</c> in the
/// routed store — its target endpoint is never looked up in the direct store;</item>
/// <item>a server whose endpoint(s) the connect path rejects (blank host/username, port out of range, or a
/// host <see cref="SshEndpoint.Create"/> refuses) contributes NOTHING and never throws (Vigil N1); a routed
/// server contributes only when both its target and its jump validate, exactly as the connect path fails
/// before any lookup otherwise.</item>
/// </list>
/// Normalization is <see cref="SshEndpoint.Create"/>/<see cref="SshRoute.Create"/>, the stores' own key
/// normalization. The one definition used by the export filter, the backup reader and the restore summary.
/// </summary>
public sealed class TrustReferenceSet
{
    private TrustReferenceSet(IReadOnlySet<SshEndpoint> direct, IReadOnlySet<SshRoute> routed)
    {
        Direct = direct;
        Routed = routed;
    }

    public IReadOnlySet<SshEndpoint> Direct { get; }

    public IReadOnlySet<SshRoute> Routed { get; }

    public static TrustReferenceSet From(IEnumerable<Server> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var direct = new HashSet<SshEndpoint>();
        var routed = new HashSet<SshRoute>();
        foreach (var server in servers)
        {
            if (server is null || !TryEndpoint(server.Host, server.Username, server.Port, out var target))
            {
                continue;
            }

            if (server.Route is null)
            {
                direct.Add(target);
                continue;
            }

            if (server.Route.Jump is { } jump && TryEndpoint(jump.Host, jump.Username, jump.Port, out var via))
            {
                direct.Add(via);
                routed.Add(SshRoute.Create(via, target));
            }
        }

        return new TrustReferenceSet(direct, routed);
    }

    private static bool TryEndpoint(string? host, string? username, int port, out SshEndpoint endpoint)
    {
        endpoint = new SshEndpoint(string.Empty, 0);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || port is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            endpoint = SshEndpoint.Create(host, port);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
