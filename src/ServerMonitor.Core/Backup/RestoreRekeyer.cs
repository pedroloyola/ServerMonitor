using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Backup;

/// <summary>A credential carried by a backup, under the reference id it had on the source machine.</summary>
public sealed record BackupCredential(Guid ServerId, ServerCredentialKind Kind, Guid ReferenceId, SecretValue Secret)
{
    public override string ToString() => $"{nameof(BackupCredential)} {{ {Kind} }}";
}

public sealed record RekeyedCredential(CredentialReference Reference, SecretValue Secret)
{
    public override string ToString() => $"{nameof(RekeyedCredential)} {{ {Reference.Kind} }}";
}

public sealed record RekeyedRestore(
    IReadOnlyList<Server> Servers,
    IReadOnlyList<RekeyedCredential> Credentials,
    IReadOnlyList<CredentialReference> ReferencesWithoutSecret);

/// <summary>
/// Re-keys a validated backup (M14.6 D2). Every credential reference a restored server implies gets a FRESH
/// id, so each new Credential Manager target is new: writing it is additive, nothing current is overwritten
/// before the commit point, and a rollback never needs an old secret. A server whose secret the backup
/// flagged missing also gets a fresh id with no secret behind it (the user re-enters it, as today).
/// A reference id whose authentication method implies no secret kind can never be read and is cleared.
/// Pure: no I/O; <paramref name="newId"/> is injectable for tests.
/// </summary>
public static class RestoreRekeyer
{
    public static RekeyedRestore Rekey(
        IReadOnlyList<Server> servers,
        IReadOnlyList<BackupCredential> credentials,
        Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(newId);

        var secrets = credentials.ToDictionary(credential => (credential.ServerId, credential.Kind), credential => credential.Secret);
        var rekeyedServers = new List<Server>(servers.Count);
        var writes = new List<RekeyedCredential>();
        var withoutSecret = new List<CredentialReference>();

        foreach (var server in servers)
        {
            var rekeyed = server;
            if (ServerCredentialReferences.TryTarget(server) is { } target)
            {
                var fresh = Fresh(target, newId);
                rekeyed = rekeyed with { CredentialReferenceId = fresh.ReferenceId };
                Record(fresh, secrets, writes, withoutSecret);
            }
            else if (server.CredentialReferenceId is not null)
            {
                rekeyed = rekeyed with { CredentialReferenceId = null };
            }

            if (server.Route?.Jump is { } jump)
            {
                Guid? jumpId = null;
                if (ServerCredentialReferences.Jump(server) is { } jumpReference)
                {
                    var fresh = Fresh(jumpReference, newId);
                    jumpId = fresh.ReferenceId;
                    Record(fresh, secrets, writes, withoutSecret);
                }

                if (jumpId != jump.CredentialReferenceId)
                {
                    rekeyed = rekeyed with { Route = rekeyed.Route! with { Jump = jump with { CredentialReferenceId = jumpId } } };
                }
            }

            rekeyedServers.Add(rekeyed);
        }

        return new RekeyedRestore(rekeyedServers, writes, withoutSecret);
    }

    private static CredentialReference Fresh(CredentialReference original, Func<Guid> newId)
    {
        var id = newId();
        if (id == Guid.Empty || id == original.ReferenceId)
        {
            throw new InvalidOperationException("Re-keying requires a fresh, non-empty reference id.");
        }

        return original with { ReferenceId = id };
    }

    private static void Record(
        CredentialReference fresh,
        Dictionary<(Guid ServerId, ServerCredentialKind Kind), SecretValue> secrets,
        List<RekeyedCredential> writes,
        List<CredentialReference> withoutSecret)
    {
        if (secrets.TryGetValue((fresh.ServerId, fresh.Kind), out var secret))
        {
            writes.Add(new RekeyedCredential(fresh, secret));
        }
        else
        {
            withoutSecret.Add(fresh);
        }
    }
}
