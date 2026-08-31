namespace ServerMonitor.ActivationContract;

/// <summary>
/// The allowlisted Adaptive Card <c>Action.Execute</c> verbs the widget emits and the provider handles
/// (§13). The verb names are the only two recognized; anything else is ignored. The server id travels in
/// the action's data as an opaque guid string under <see cref="ServerIdDataKey"/> — never any other field.
/// </summary>
public static class ActivationVerbs
{
    public const string OpenDashboard = "openDashboard";

    public const string OpenServer = "openServer";

    /// <summary>The single data key carrying the opaque server id for <see cref="OpenServer"/>.</summary>
    public const string ServerIdDataKey = "serverId";

    /// <summary>The verb for an intent (used by the card renderer).</summary>
    public static string ForIntent(ActivationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent.Kind == ActivationIntentKind.OpenServer ? OpenServer : OpenDashboard;
    }

    /// <summary>
    /// Maps an allowlisted verb + optional opaque server id to an intent. Returns <c>null</c> for an
    /// unrecognized verb, or for <see cref="OpenServer"/> without a valid non-empty id. Never throws.
    /// </summary>
    public static ActivationIntent? TryToIntent(string? verb, Guid? serverId)
    {
        if (string.Equals(verb, OpenDashboard, StringComparison.Ordinal))
        {
            return ActivationIntent.Dashboard;
        }

        if (string.Equals(verb, OpenServer, StringComparison.Ordinal))
        {
            return serverId is { } id && id != Guid.Empty ? ActivationIntent.Server(id) : null;
        }

        return null;
    }
}
