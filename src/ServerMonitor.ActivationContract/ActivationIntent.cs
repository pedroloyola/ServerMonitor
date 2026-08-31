namespace ServerMonitor.ActivationContract;

/// <summary>What a widget activation asks the app to do. Deliberately tiny (§8): observation/navigation
/// only — never a command, service action, or write. M13 Community is read-only.</summary>
public enum ActivationIntentKind
{
    /// <summary>Open the dashboard (server list).</summary>
    OpenDashboard,

    /// <summary>Open the dashboard focused on one server, identified by its opaque id.</summary>
    OpenServer
}

/// <summary>
/// A validated, normalized activation request. Carries ONLY the intent kind and, for
/// <see cref="ActivationIntentKind.OpenServer"/>, an opaque server id — never an IP, hostname, username,
/// credential, or display name (§9/§26). The app resolves the id against its own server store (the
/// authority), so a stale/removed id falls back safely (§11).
/// </summary>
public sealed record ActivationIntent
{
    public required ActivationIntentKind Kind { get; init; }

    /// <summary>The opaque server id for <see cref="ActivationIntentKind.OpenServer"/>; otherwise null.</summary>
    public Guid? ServerId { get; init; }

    public static ActivationIntent Dashboard { get; } = new() { Kind = ActivationIntentKind.OpenDashboard };

    public static ActivationIntent Server(Guid serverId) =>
        new() { Kind = ActivationIntentKind.OpenServer, ServerId = serverId };
}
