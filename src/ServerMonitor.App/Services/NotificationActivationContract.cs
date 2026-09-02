namespace ServerMonitor.App.Services;

/// <summary>What produced a notification. A closed set — there is no "other".</summary>
public enum NotificationKind
{
    /// <summary>A server health notification (M8).</summary>
    ServerHealth,

    /// <summary>The single first-close notice explaining background monitoring (M13 S2 §D.1).</summary>
    BackgroundCloseNotice
}

/// <summary>What clicking a notification does. A closed set, and the ONLY routing vocabulary.</summary>
public enum NotificationAction
{
    /// <summary>Fail-closed default: do nothing at all.</summary>
    None,

    /// <summary>Surface the app on the Dashboard (the M8 health behaviour, now explicit).</summary>
    OpenDashboard,

    /// <summary>Surface the app directly on Settings → Background, never on the Dashboard.</summary>
    OpenBackgroundSettings
}

/// <summary>
/// The closed, typed activation contract for notifications (M13 S2 §D.1; Vigil C7/C8).
/// <para>
/// Before S2 the platform adapter threw <c>AppNotificationActivatedEventArgs</c> away and the service
/// restored the window on ANY click. That is why the first-close notice would have re-opened the
/// Dashboard the user had just closed — inherited by accident rather than designed. Routing is now
/// explicit and minimal:
/// </para>
/// <list type="bullet">
/// <item><b>Two keys, closed vocabularies.</b> <c>kind</c> and <c>action</c>, each parsed against an
/// enum. Nothing else is read.</item>
/// <item><b>Zero action parameters.</b> The payload carries no server id, hostname, address, display
/// name, fleet count, snapshot-derived value, free text, or URI/query/fragment — there is no field for
/// any of them, so none can be smuggled through.</item>
/// <item><b>Fail closed.</b> An unknown, missing, malformed or mismatched kind/action resolves to
/// <see cref="NotificationAction.None"/>, which does nothing.</item>
/// </list>
/// </summary>
public static class NotificationActivationContract
{
    public const string KindKey = "kind";

    public const string ActionKey = "action";

    /// <summary>The arguments a health notification carries. Explicit, no longer implied by absence.</summary>
    public static IReadOnlyDictionary<string, string> ForServerHealth() =>
        Build(NotificationKind.ServerHealth, NotificationAction.OpenDashboard);

    /// <summary>The arguments the single background notice carries.</summary>
    public static IReadOnlyDictionary<string, string> ForBackgroundCloseNotice() =>
        Build(NotificationKind.BackgroundCloseNotice, NotificationAction.OpenBackgroundSettings);

    /// <summary>
    /// Resolves an activation payload to an action, rejecting anything that is not exactly one of the
    /// pairs this app produces. The kind/action pairing is verified too: a valid action under the wrong
    /// kind is not a valid activation.
    /// </summary>
    public static NotificationAction ResolveAction(IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments is null
            || !arguments.TryGetValue(KindKey, out var rawKind)
            || !arguments.TryGetValue(ActionKey, out var rawAction))
        {
            return NotificationAction.None;
        }

        // Vigil CI-1: an EXACT allowlist of the two pairs this app produces, matched ordinally on the
        // literal wire strings. Enum.TryParse was too lax for a contract that claims a closed vocabulary:
        // it accepts an enum's NUMERIC representation ("0", "1"), accepts comma-separated combinations,
        // and would silently absorb any member added later. Nothing outside these two rows resolves.
        return (rawKind, rawAction) switch
        {
            ("ServerHealth", "OpenDashboard") => NotificationAction.OpenDashboard,
            ("BackgroundCloseNotice", "OpenBackgroundSettings") => NotificationAction.OpenBackgroundSettings,
            _ => NotificationAction.None
        };
    }

    private static Dictionary<string, string> Build(NotificationKind kind, NotificationAction action) => new()
    {
        [KindKey] = kind.ToString(),
        [ActionKey] = action.ToString()
    };
}
