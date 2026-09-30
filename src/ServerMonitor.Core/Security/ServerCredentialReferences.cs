using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Security;

/// <summary>
/// The one derivation of the Credential Manager references a saved server implies: its own secret (kind from
/// <see cref="Server.AuthenticationMethod"/>) and its jump host's secret. Shared by the profile service, the
/// backup export, the backup reader's exact-credential-set check and the restore re-keying (M14.6 §2.1).
/// </summary>
public static class ServerCredentialReferences
{
    /// <summary>The target secret reference, or <see langword="null"/> when none is stored. Throws when a
    /// reference id is set but the authentication method has no secret kind (unchanged profile-service rule).</summary>
    public static CredentialReference? Target(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.CredentialReferenceId is not Guid referenceId)
        {
            return null;
        }

        return new CredentialReference(server.Id, GetCredentialKind(server.AuthenticationMethod), referenceId);
    }

    /// <summary>Like <see cref="Target"/>, but <see langword="null"/> instead of throwing when the
    /// authentication method has no secret kind (such a reference can never be read, so implies nothing).</summary>
    public static CredentialReference? TryTarget(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.CredentialReferenceId is Guid referenceId
            && TryGetCredentialKind(server.AuthenticationMethod) is { } kind
                ? new CredentialReference(server.Id, kind, referenceId)
                : null;
    }

    public static CredentialReference? Jump(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Route?.Jump is not { CredentialReferenceId: Guid referenceId } jump
            || GetJumpCredentialKind(jump.AuthenticationMethod) is not { } kind)
        {
            return null;
        }

        return new CredentialReference(server.Id, kind, referenceId);
    }

    /// <summary>Every reference the server implies (target first, then jump), never throwing.</summary>
    public static IEnumerable<CredentialReference> All(Server server)
    {
        if (TryTarget(server) is { } target)
        {
            yield return target;
        }

        if (Jump(server) is { } jump)
        {
            yield return jump;
        }
    }

    public static ServerCredentialKind GetCredentialKind(AuthenticationMethod authenticationMethod) =>
        TryGetCredentialKind(authenticationMethod)
        ?? throw new ArgumentException("Authentication must be configured.", nameof(authenticationMethod));

    public static ServerCredentialKind? TryGetCredentialKind(AuthenticationMethod authenticationMethod) =>
        authenticationMethod switch
        {
            AuthenticationMethod.Password => ServerCredentialKind.Password,
            AuthenticationMethod.SshKey => ServerCredentialKind.PrivateKeyPassphrase,
            _ => null
        };

    public static ServerCredentialKind? GetJumpCredentialKind(AuthenticationMethod authenticationMethod) =>
        authenticationMethod switch
        {
            AuthenticationMethod.Password => ServerCredentialKind.JumpPassword,
            AuthenticationMethod.SshKey => ServerCredentialKind.JumpPrivateKeyPassphrase,
            _ => null
        };

    public static bool IsJumpKind(ServerCredentialKind kind) =>
        kind is ServerCredentialKind.JumpPassword or ServerCredentialKind.JumpPrivateKeyPassphrase;
}
