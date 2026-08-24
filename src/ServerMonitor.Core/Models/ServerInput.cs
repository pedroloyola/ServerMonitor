using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

public sealed record ServerInput
{
    public string Name { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public ServerOperatingSystem OperatingSystem { get; init; } = ServerOperatingSystem.Auto;

    public AuthenticationMethod AuthenticationMethod { get; init; } = AuthenticationMethod.NotConfigured;

    public string? PrivateKeyPath { get; init; }

    public Guid? CredentialReferenceId { get; init; }
}
