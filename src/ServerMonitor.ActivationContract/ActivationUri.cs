using System.Globalization;

namespace ServerMonitor.ActivationContract;

/// <summary>
/// The strict <c>serveralyzer://</c> deep-link grammar (ADR-018 §7). Only two shapes are valid:
/// <list type="bullet">
///   <item><c>serveralyzer://dashboard</c> → <see cref="ActivationIntentKind.OpenDashboard"/></item>
///   <item><c>serveralyzer://server/{opaque-guid}</c> → <see cref="ActivationIntentKind.OpenServer"/></item>
/// </list>
/// The URI is treated as UNTRUSTED even though our own widget forms it (§10): any local process can
/// invoke the protocol. Parsing is total and neutral — it rejects a wrong scheme, unknown host, extra
/// path segments, any query or fragment, a bad/empty guid, an over-length string, or anything unexpected,
/// returning <c>null</c> and never throwing, never executing input. There is no free path, command, URL,
/// or argument grammar — by construction it cannot carry one.
/// </summary>
public static class ActivationUri
{
    public const string Scheme = "serveralyzer";

    public const string DashboardHost = "dashboard";

    public const string ServerHost = "server";

    /// <summary>Defensive upper bound on the whole URI (a valid one is well under this).</summary>
    public const int MaxUriLength = 256;

    public static string Format(ActivationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent.Kind switch
        {
            ActivationIntentKind.OpenServer when intent.ServerId is { } id =>
                $"{Scheme}://{ServerHost}/{id.ToString("D", CultureInfo.InvariantCulture)}",
            _ => $"{Scheme}://{DashboardHost}"
        };
    }

    /// <summary>Parses an untrusted activation URI. Returns <c>null</c> for anything invalid.</summary>
    public static ActivationIntent? TryParse(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || uri.Length > MaxUriLength)
        {
            return null;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // No query and no fragment are ever allowed — the grammar carries nothing beyond host + one id.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            return null;
        }

        // Reject any authority extras (userinfo / explicit port).
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort)
        {
            return null;
        }

        var host = parsed.Host;

        if (string.Equals(host, DashboardHost, StringComparison.OrdinalIgnoreCase))
        {
            // A bare dashboard link must have no path segments.
            return HasNoPath(parsed) ? ActivationIntent.Dashboard : null;
        }

        if (string.Equals(host, ServerHost, StringComparison.OrdinalIgnoreCase))
        {
            return TryParseServer(parsed);
        }

        return null;
    }

    private static ActivationIntent? TryParseServer(Uri parsed)
    {
        // Exactly one non-empty path segment, which must be an EXACT guid — no encoded slashes, no extra
        // segments, no dotted traversal. Segments() includes the leading "/"; require precisely ["/", id].
        var segments = parsed.Segments;
        if (segments.Length != 2 || segments[0] != "/")
        {
            return null;
        }

        var raw = segments[1];
        // A trailing slash would make an empty third segment; also reject any residual slash in the id.
        if (raw.EndsWith('/') || raw.Contains('/'))
        {
            return null;
        }

        // Use the ORIGINAL (unescaped) form and require the exact 36-char "D" format, so "%2F" or other
        // encodings that decode into structure are rejected rather than silently accepted.
        if (raw.Length != 36 || !Guid.TryParseExact(raw, "D", out var id) || id == Guid.Empty)
        {
            return null;
        }

        return ActivationIntent.Server(id);
    }

    private static bool HasNoPath(Uri parsed) =>
        parsed.AbsolutePath is "" or "/";
}
