using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ServerMonitor.Core.SshConfig;

/// <summary>A single jump hop as written in a <c>ProxyJump</c> value: <c>[user@]host[:port]</c>.</summary>
/// <param name="Host">The destination ssh resolves for the jump (brackets of an IPv6 literal removed).</param>
/// <param name="User">An explicit <c>user@</c>, which wins over the jump's config <c>User</c> (like <c>-l</c>).</param>
/// <param name="Port">An explicit <c>:port</c>, which wins over the jump's config <c>Port</c> (like <c>-p</c>).</param>
public sealed record SshConfigJumpSpec(string Host, string? User, int? Port);

public enum SshConfigProxyJumpParse
{
    SingleHop,

    /// <summary>A comma list: more than one hop.</summary>
    MultiHop,

    /// <summary>Anything that is not positively the supported single-hop grammar.</summary>
    Unparsable
}

/// <summary>
/// Pure parser for the supported subset of a <c>ProxyJump</c> value, following OpenSSH
/// <c>parse_jump</c>/<c>parse_user_host_port</c> but failing closed on every construct it does not
/// positively verify: <c>ssh://</c> URIs, <c>%</c> tokens, <c>/</c> delimiters, an empty port,
/// brackets around anything but an IPv6 literal, and unusual characters in the host or user.
/// </summary>
public static class SshConfigProxyJump
{
    /// <summary>
    /// Parses the value of one applicable, non-<c>none</c> <c>ProxyJump</c> line. OpenSSH reads the
    /// value raw (no quote, escape or comment handling), so it is accepted only when the raw text is
    /// exactly the single tokenized argument: a quoted, escaped or commented value is unparsable.
    /// </summary>
    public static SshConfigProxyJumpParse Parse(SshConfigDirective directive, out SshConfigJumpSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(directive);
        spec = null;
        if (!directive.IsValid
            || directive.Arguments.Count != 1
            || !string.Equals(directive.Arguments[0], directive.RawArguments, StringComparison.Ordinal))
        {
            return SshConfigProxyJumpParse.Unparsable;
        }

        return Parse(directive.Arguments[0], out spec);
    }

    /// <summary>Parses a raw <c>ProxyJump</c> value (already known not to be the exact <c>none</c>).</summary>
    public static SshConfigProxyJumpParse Parse(string value, out SshConfigJumpSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(value);
        spec = null;
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            return SshConfigProxyJumpParse.Unparsable;
        }

        if (value.Contains(','))
        {
            return SshConfigProxyJumpParse.MultiHop;
        }

        // "none" in another case: OpenSSH versions disagree on whether it means "no proxy".
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            || value.Contains('%')
            || value.Contains('/'))
        {
            return SshConfigProxyJumpParse.Unparsable;
        }

        // parse_user_host_port: the user is everything before the LAST '@'.
        string? user = null;
        var rest = value;
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            user = value[..at];
            rest = value[(at + 1)..];
            if (!IsUser(user))
            {
                return SshConfigProxyJumpParse.Unparsable;
            }
        }

        string host;
        string? portText = null;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
            {
                return SshConfigProxyJumpParse.Unparsable;
            }

            host = rest[1..close];
            var after = rest[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                {
                    return SshConfigProxyJumpParse.Unparsable;
                }

                portText = after[1..];
            }

            if (!IsIPv6Literal(host))
            {
                return SshConfigProxyJumpParse.Unparsable;
            }
        }
        else
        {
            var colon = rest.IndexOf(':');
            host = colon < 0 ? rest : rest[..colon];
            portText = colon < 0 ? null : rest[(colon + 1)..];
            if (!IsHostName(host))
            {
                return SshConfigProxyJumpParse.Unparsable;
            }
        }

        int? port = null;
        if (portText is not null)
        {
            // "host:" (empty port) and unbracketed IPv6 ("fe80::1") are refused.
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed is < 1 or > 65535)
            {
                return SshConfigProxyJumpParse.Unparsable;
            }

            port = parsed;
        }

        spec = new SshConfigJumpSpec(host, user, port);
        return SshConfigProxyJumpParse.SingleHop;
    }

    // A DNS name, alias or IPv4 literal. No leading '-' (never an option to ssh) or '.'.
    private static bool IsHostName(string host) =>
        host.Length > 0
        && host[0] is not ('-' or '.')
        && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool IsUser(string user) =>
        user.Length > 0
        && user[0] != '-'
        && user.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@');

    private static bool IsIPv6Literal(string host) =>
        host.Contains(':')
        && host.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
        && IPAddress.TryParse(host, out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6;
}
