namespace ServerMonitor.App.ViewModels;

/// <summary>
/// Non-sensitive seed passed to the server editor when the user adds a network-discovered service.
/// It carries only what a passive mDNS suggestion can offer — a display name, a resolved host or
/// address, and the advertised port. It never carries a username, an authentication method, a
/// credential, a fingerprint or an operating-system guess: the editor still runs the exact M3
/// add / test-connection / host-key-trust / save flow, and the operating system stays
/// <see cref="Core.Enums.ServerOperatingSystem.Auto"/> until a real connection detects it.
/// </summary>
public sealed record ServerDiscoveryPrefill
{
    public required string Name { get; init; }

    public required string Host { get; init; }

    public required int Port { get; init; }
}
