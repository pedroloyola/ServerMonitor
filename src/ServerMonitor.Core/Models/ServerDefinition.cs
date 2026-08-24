using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

public sealed record ServerDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    public ServerOperatingSystem OperatingSystem { get; init; } = ServerOperatingSystem.Unknown;

    public AuthenticationMethod AuthenticationMethod { get; init; } = AuthenticationMethod.SshKey;

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsEnabled { get; init; } = true;
}
