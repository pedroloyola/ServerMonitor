using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Models;

/// <summary>
/// A non-direct way to reach a server: today exactly one SSH jump host (M14.4b, single hop).
/// Persisted only in <c>routed-servers.json</c>; a server carrying a route is never dialled direct.
/// </summary>
public sealed record ServerRoute
{
    public JumpHop? Jump { get; init; }
}

/// <summary>
/// The jump (bastion) host of a <see cref="ServerRoute"/>. Its trust is the ordinary DIRECT entry for
/// its endpoint in <c>known-hosts.json</c>; its secret is an independent credential reference
/// (<see cref="ServerCredentialKind.JumpPassword"/> / <see cref="ServerCredentialKind.JumpPrivateKeyPassphrase"/>).
/// </summary>
public sealed record JumpHop
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 22;

    public string Username { get; init; } = string.Empty;

    public AuthenticationMethod AuthenticationMethod { get; init; } = AuthenticationMethod.NotConfigured;

    public string? PrivateKeyPath { get; init; }

    public Guid? CredentialReferenceId { get; init; }
}
