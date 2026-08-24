using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Security;

public readonly record struct CredentialReference(
    Guid ServerId,
    ServerCredentialKind Kind,
    Guid ReferenceId)
{
    public static CredentialReference Create(Guid serverId, ServerCredentialKind kind) =>
        new(serverId, kind, Guid.NewGuid());

    public bool IsValid => ServerId != Guid.Empty
        && ReferenceId != Guid.Empty
        && Enum.IsDefined(Kind);
}
