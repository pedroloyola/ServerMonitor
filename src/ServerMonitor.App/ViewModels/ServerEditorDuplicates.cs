using System.Globalization;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.7 H-UI7-2: "this server is already in your list" - a NON-blocking notice, in memory only (no persistence, no index).
/// Identity = the normalized <see cref="SshEndpoint"/> (host, port) + the SSH user (ordinal, trimmed) + the route's via
/// endpoint (null for a direct server). The list compared against is every saved server, visible and hidden; an edit
/// never matches itself. Save stays allowed whatever this says.
/// </summary>
public static class ServerEditorDuplicates
{
    public sealed record Identity(SshEndpoint Endpoint, string Username, SshEndpoint? Via);

    /// <summary>The identity of what the form describes now, or null while it is incomplete or unparsable.</summary>
    public static Identity? Of(string host, string port, string username, bool useJump, string jumpHost, string jumpPort)
    {
        if (!TryEndpoint(host, port, out var endpoint) || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        SshEndpoint? via = null;
        if (useJump && !TryEndpoint(jumpHost, jumpPort, out via))
        {
            return null;
        }

        return new Identity(endpoint!, username.Trim(), via);
    }

    public static Identity? Of(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var jump = server.Route?.Jump;
        return Of(
            server.Host,
            server.Port.ToString(CultureInfo.InvariantCulture),
            server.Username,
            jump is not null,
            jump?.Host ?? string.Empty,
            (jump?.Port ?? 22).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The identity an SSH config profile would import (host, user, port and jump as the resolver read them), or null when
    /// the profile does not name a host and a user.
    /// </summary>
    public static Identity? Of(SshConfigHostEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.HostName is null || entry.User is null)
        {
            return null;
        }

        return Of(
            entry.HostName,
            (entry.Port ?? 22).ToString(CultureInfo.InvariantCulture),
            entry.User,
            entry.Jump is not null,
            entry.Jump?.HostName ?? string.Empty,
            (entry.Jump?.Port ?? 22).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A saved server with its identity, computed once per visit (Cortex n-1) - not again on every keystroke.</summary>
    public sealed record KnownServer(Server Server, Identity? Identity);

    public static IReadOnlyList<KnownServer> Index(IReadOnlyList<Server> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return servers.Select(server => new KnownServer(server, Of(server))).ToList();
    }

    /// <summary>The first saved server with the same identity, never <paramref name="self"/> (the server being edited).</summary>
    public static Server? Find(IReadOnlyList<KnownServer> known, Identity? identity, Guid? self)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (identity is null)
        {
            return null;
        }

        return known.FirstOrDefault(entry => entry.Server.Id != self && entry.Identity == identity)?.Server;
    }

    private static bool TryEndpoint(string host, string port, out SshEndpoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(host)
            || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            endpoint = SshEndpoint.Create(host, parsed);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
