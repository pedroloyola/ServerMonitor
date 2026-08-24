using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Security;

internal static class CredentialTargetName
{
    internal const string ProductionPrefix = "pedroloyola.ServerMonitor:v1:ssh";

    public static string Create(CredentialReference reference)
    {
        if (!reference.IsValid)
        {
            throw new ArgumentException("The credential reference is invalid.", nameof(reference));
        }

        var kind = reference.Kind switch
        {
            ServerCredentialKind.Password => "password",
            ServerCredentialKind.PrivateKeyPassphrase => "key-passphrase",
            _ => throw new ArgumentException("The credential kind is invalid.", nameof(reference))
        };

        return $"{ProductionPrefix}:{reference.ServerId:N}:{kind}:{reference.ReferenceId:N}";
    }
}
